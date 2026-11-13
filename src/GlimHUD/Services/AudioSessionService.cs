using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using NAudio.CoreAudioApi;
using GlimHUD.Localization;
using NAudio.CoreAudioApi.Interfaces;

namespace GlimHUD.Services;

public sealed record AudioSessionInfo(
    string Id, int ProcessId, string DisplayName, float Volume, bool IsMuted, bool IsActive);

public sealed record AudioSnapshot(
    bool IsAvailable,
    float Volume,
    bool IsMuted,
    string? SessionId,
    string DisplayName,
    IReadOnlyList<AudioSessionInfo> Sessions,
    string Status);

/// <summary>
/// Controls render sessions independently from the system endpoint volume.
/// Every COM object is created, used and released on one background worker call.
/// </summary>
public sealed class AudioSessionService : IDisposable
{
    private const string GroupPrefix = "GlimHUD.AudioGroup.v1:";
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    public Task<AudioSnapshot> GetSnapshotAsync(
        string? mediaSourceAppId,
        string? preferredSessionId = null,
        CancellationToken cancellationToken = default)
        => RunAsync(() => ReadSnapshot(mediaSourceAppId, preferredSessionId), cancellationToken);

    public Task<bool> SetVolumeAsync(
        float volume,
        string? mediaSourceAppId,
        string? preferredSessionId = null,
        CancellationToken cancellationToken = default)
        => RunAsync(() => float.IsFinite(volume) && MutateSession(mediaSourceAppId, preferredSessionId,
            session => session.SimpleAudioVolume.Volume = Math.Clamp(volume, 0f, 1f)), cancellationToken);

    public Task<bool> SetMutedAsync(
        bool muted,
        string? mediaSourceAppId,
        string? preferredSessionId = null,
        CancellationToken cancellationToken = default)
        => RunAsync(() => MutateSession(mediaSourceAppId, preferredSessionId,
            session => session.SimpleAudioVolume.Mute = muted), cancellationToken);

    private async Task<T> RunAsync<T>(Func<T> operation, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return await Task.Run(operation, cancellationToken);
        }
        finally { _gate.Release(); }
    }

    private static AudioSnapshot ReadSnapshot(string? mediaSourceAppId, string? preferredSessionId)
    {
        try
        {
            var sessions = EnumerateSessions();
            var selected = SelectSession(sessions, mediaSourceAppId, preferredSessionId);
            var options = sessions.Select(session => session.Info)
                .OrderByDescending(session => session.IsActive)
                .ThenBy(session => session.DisplayName).ToList();
            if (selected is null)
            {
                var status = !string.IsNullOrWhiteSpace(preferredSessionId)
                    ? L10n.Text("The selected audio session is unavailable", "Выбранная аудиосессия недоступна")
                    : string.IsNullOrWhiteSpace(mediaSourceAppId)
                        ? L10n.Text("Select an audio app in Settings", "Выберите аудиоприложение в настройках")
                        : L10n.Text("Select an audio app: automatic matching is unavailable", "Выберите аудиоприложение: автоматическое сопоставление недоступно");
                return new(false, 0, false, null, L10n.Text("Music volume", "Громкость музыки"), options, status);
            }
            var info = selected.Info;
            if (selected.MemberIds is not null) options.Insert(0, info);
            return new(true, info.Volume, info.IsMuted, info.Id, info.DisplayName, options, "");
        }
        catch
        {
            return new(false, 0, false, null, L10n.Text("Music volume", "Громкость музыки"), Array.Empty<AudioSessionInfo>(),
                L10n.Text("The audio device is temporarily unavailable", "Аудиоустройство временно недоступно"));
        }
    }

    private static bool MutateSession(string? mediaSourceAppId, string? preferredSessionId, Action<AudioSessionControl> mutation)
    {
        try
        {
            // Resolve afresh for each command: stale audio sessions must not redirect a command.
            var selected = SelectSession(EnumerateSessions(), mediaSourceAppId, preferredSessionId);
            if (selected is null) return false;
            var remaining = (selected.MemberIds ?? [selected.Info.Id]).ToHashSet(StringComparer.Ordinal);
            using var enumerator = new MMDeviceEnumerator();
            var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
            foreach (var device in devices)
            {
                using (device)
                {
                    try
                    {
                        var sessions = device.AudioSessionManager.Sessions;
                        for (var index = 0; index < sessions.Count; index++)
                        {
                            using var session = sessions[index];
                            if (session.IsSystemSoundsSession || session.State == AudioSessionState.AudioSessionStateExpired)
                                continue;
                            var sessionId = CreateId(device.ID, session);
                            if (!remaining.Contains(sessionId)) continue;
                            mutation(session);
                            remaining.Remove(sessionId);
                        }
                    }
                    catch (COMException) { /* Endpoint may disappear during a Bluetooth/device switch. */ }
                }
            }
            return remaining.Count == 0;
        }
        catch { /* A failed command is reported to the caller, never rerouted to master volume. */ }
        return false;
    }

    private static List<SessionIdentity> EnumerateSessions()
    {
        var result = new List<SessionIdentity>();
        using var enumerator = new MMDeviceEnumerator();
        var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
        foreach (var device in devices)
        {
            using (device)
            {
                try
                {
                    var sessions = device.AudioSessionManager.Sessions;
                    for (var index = 0; index < sessions.Count; index++)
                    {
                        using var session = sessions[index];
                        try
                        {
                            if (session.IsSystemSoundsSession || session.State == AudioSessionState.AudioSessionStateExpired)
                                continue;
                            var processId = checked((int)session.GetProcessID);
                            if (processId == 0 || processId == Environment.ProcessId) continue;
                            string processName;
                            string? appId;
                            string? executablePath;
                            using (var process = Process.GetProcessById(processId))
                            {
                                processName = process.ProcessName;
                                (appId, executablePath) = ReadProcessIdentity(processId);
                            }
                            var displayName = session.DisplayName;
                            if (string.IsNullOrWhiteSpace(displayName) || displayName.StartsWith('@'))
                                displayName = AudioIdentityRules.GetDisplayName(processName);
                            var info = new AudioSessionInfo(CreateId(device.ID, session), processId,
                                displayName, session.SimpleAudioVolume.Volume, session.SimpleAudioVolume.Mute,
                                session.State == AudioSessionState.AudioSessionStateActive);
                            result.Add(new(info, processName, appId, executablePath));
                        }
                        catch (Exception exception) when (exception is COMException or ArgumentException or InvalidOperationException
                                                         or System.ComponentModel.Win32Exception)
                        {
                            // An application may exit between enumerating a session and reading its PID.
                        }
                    }
                }
                catch (COMException) { /* Continue with remaining active endpoints. */ }
            }
        }
        return result;
    }

    private static SessionIdentity? SelectSession(
        IReadOnlyList<SessionIdentity> sessions, string? sourceAppId, string? preferredSessionId)
    {
        if (!string.IsNullOrWhiteSpace(preferredSessionId))
        {
            if (!preferredSessionId.StartsWith(GroupPrefix, StringComparison.Ordinal))
                return sessions.FirstOrDefault(session => string.Equals(session.Info.Id, preferredSessionId, StringComparison.Ordinal));
            var groupIds = DecodeGroupId(preferredSessionId);
            if (groupIds is null) return null;
            var members = sessions.Where(item => groupIds.Contains(item.Info.Id, StringComparer.Ordinal)).ToArray();
            return members.Length == groupIds.Length ? GroupSessions(members) : null;
        }
        if (string.IsNullOrWhiteSpace(sourceAppId)) return null;

        var ids = AudioIdentityRules.SelectTargets(sessions.Select(item => new AudioIdentityCandidate(
            item.Info.Id, item.ProcessName, item.AppId, item.ExecutablePath)).ToArray(), sourceAppId);
        var matches = sessions.Where(item => ids.Contains(item.Info.Id, StringComparer.Ordinal)).ToArray();
        return matches.Length == 1 ? matches[0] : matches.Length > 1 ? GroupSessions(matches) : null;
    }

    private static SessionIdentity GroupSessions(IReadOnlyList<SessionIdentity> members)
    {
        var ids = members.Select(item => item.Info.Id).Order(StringComparer.Ordinal).ToArray();
        var groupId = GroupPrefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(ids)));
        var first = members[0];
        var audible = members.Where(item => item.Info.IsActive).ToArray();
        IReadOnlyList<SessionIdentity> levels = audible.Length > 0 ? audible : members;
        var info = new AudioSessionInfo(groupId, first.Info.ProcessId, L10n.Text("{0} — all audio streams", "{0} — все аудиопотоки", first.Info.DisplayName),
            levels.Average(item => item.Info.Volume), levels.All(item => item.Info.IsMuted), audible.Length > 0);
        return first with { Info = info, MemberIds = ids };
    }

    private static string[]? DecodeGroupId(string value)
    {
        if (value.Length > 128 * 1024) return null;
        try
        {
            var ids = JsonSerializer.Deserialize<string[]>(Encoding.UTF8.GetString(Convert.FromBase64String(value[GroupPrefix.Length..])));
            return ids is { Length: > 1 and <= 64 } && ids.All(id => !string.IsNullOrWhiteSpace(id))
                   && ids.Distinct(StringComparer.Ordinal).Count() == ids.Length ? ids : null;
        }
        catch (Exception error) when (error is FormatException or JsonException) { return null; }
    }

    private static string NormalizeExecutable(string value)
    {
        value = Path.GetFileName(value);
        return value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? value[..^4] : value;
    }

    private static string CreateId(string deviceId, AudioSessionControl session)
        => deviceId + "\u001f" + session.GetSessionInstanceIdentifier;

    private static (string? AppId, string? ExecutablePath) ReadProcessIdentity(int processId)
    {
        try
        {
            using var process = OpenProcess(0x1000, false, processId);
            if (process.IsInvalid) return (null, null);
            var path = new StringBuilder(32768);
            var pathLength = path.Capacity;
            string? executablePath = QueryFullProcessImageName(process, 0, path, ref pathLength) ? path.ToString() : null;
            uint length = 0;
            if (GetApplicationUserModelId(process, ref length, null) != 122 || length == 0 || length > 32768)
                return (null, executablePath);
            var buffer = new StringBuilder((int)length);
            return (GetApplicationUserModelId(process, ref length, buffer) == 0 ? buffer.ToString() : null, executablePath);
        }
        catch { return (null, null); }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetApplicationUserModelId(SafeProcessHandle process, ref uint applicationUserModelIdLength,
        StringBuilder? applicationUserModelId);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process, int flags, StringBuilder path, ref int size);

    private sealed record SessionIdentity(AudioSessionInfo Info, string ProcessName, string? AppId,
        string? ExecutablePath, IReadOnlyList<string>? MemberIds = null);

    public void Dispose()
    {
        _disposed = true;
        // No long-lived endpoint or session COM references survive a method call.
    }
}

/// <summary>Exact app identities and a narrow dedicated-player alias table; never fuzzy product matching.</summary>
internal static class AudioIdentityRules
{
    public static IReadOnlyList<string> SelectTargets(IReadOnlyList<AudioIdentityCandidate> sessions, string sourceAppId)
    {
        var matches = sessions.Where(item => MatchesSource(sourceAppId, item.ProcessName, item.AppId)).ToArray();
        if (matches.Length <= 1) return matches.Select(item => item.Id).ToArray();
        // Browser sessions may belong to unrelated tabs. Dedicated players can use multiple
        // audio renderer processes, but grouping requires one exact executable image path.
        if (matches.Any(item => IsBrowser(item.ProcessName))) return [];
        var paths = matches.Select(item => item.ExecutablePath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return paths.Length == 1 && !string.IsNullOrWhiteSpace(paths[0])
            ? matches.Select(item => item.Id).ToArray() : [];
    }
    public static bool MatchesSource(string sourceAppId, string processName, string? appId)
    {
        if (string.Equals(sourceAppId, appId, StringComparison.OrdinalIgnoreCase)) return true;
        if (IsYandexMusicSource(sourceAppId) && IsYandexMusicProcess(processName)) return true;
        return !sourceAppId.Contains('!') && string.Equals(NormalizeExecutable(sourceAppId),
            NormalizeExecutable(processName), StringComparison.OrdinalIgnoreCase);
    }
    public static bool IsYandexMusicSource(string value) => Canonical(value) is
        "ruyandexdesktopmusic" or "ruyandexmusic" or "comyandexmusic" or "yandexmusic" or "яндексмузыка";
    public static bool IsYandexMusicProcess(string value) => Canonical(value) is
        "yandexmusic" or "яндексмузыка" or "yandexmusicdesktop";
    public static bool IsBrowser(string value) => Canonical(value) is
        "chrome" or "msedge" or "firefox" or "browser" or "yandex" or "brave" or "opera" or "vivaldi";
    public static string GetDisplayName(string value) => IsYandexMusicProcess(value) ? L10n.Text("Yandex Music", "Яндекс Музыка") : NormalizeExecutable(value).ToLowerInvariant() switch
    {
        "chrome" => "Google Chrome", "msedge" => "Microsoft Edge", "firefox" => "Firefox",
        "browser" or "yandex" => L10n.Text("Yandex Browser", "Яндекс Браузер"), "spotify" => "Spotify", _ => NormalizeExecutable(value)
    };
    private static string Canonical(string value) => new(NormalizeExecutable(value).Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
    private static string NormalizeExecutable(string value)
    {
        value = Path.GetFileName(value);
        return value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? value[..^4] : value;
    }
}

internal sealed record AudioIdentityCandidate(string Id, string ProcessName, string? AppId, string? ExecutablePath);
