using System.Text;
using System.Text.Json;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace GlimHUD.Services;

public sealed record AudioLevelSnapshot(float Peak, bool IsAvailable, string? SessionId, DateTimeOffset SampledAt);

/// <summary>
/// Read-only peak metering for the exact audio session(s) selected by the music controls.
/// No endpoint-wide signal, microphone, loopback capture, or audio samples are recorded.
/// </summary>
public sealed class AudioLevelMonitor : IDisposable
{
    private readonly object _sync = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly IAudioLevelReaderFactory _factory;
    private readonly TimeProvider _clock;
    private Thread? _thread;
    private MeterRequest _request = new(false, null, [], 0);
    private AudioLevelSnapshot _snapshot;
    private float _framePeak;
    private DateTimeOffset _framePeakAt;
    private bool _framePending;
    private ulong _audibleSessions;
    private bool _disposed;

    public AudioLevelMonitor() : this(new WindowsAudioLevelReaderFactory()) { }
    internal AudioLevelMonitor(IAudioLevelReaderFactory factory, TimeProvider? clock = null)
    {
        _factory = factory;
        _clock = clock ?? TimeProvider.System;
        _snapshot = new(0, false, null, _clock.GetUtcNow());
    }

    public AudioLevelSnapshot Snapshot => Volatile.Read(ref _snapshot);

    /// <summary>
    /// Consumes the strongest audible peak since the previous UI frame. A short attack
    /// survives a later quiet poll, while the next frame falls back to the latest sample.
    /// Unconsumed attacks expire after 120 ms instead of replaying when a UI resumes.
    /// </summary>
    public AudioLevelSnapshot ReadFrame()
    {
        lock (_sync)
        {
            var latest = _snapshot;
            if (!_disposed && _request.Enabled && latest.IsAvailable && _framePending
                && _clock.GetUtcNow() - _framePeakAt <= TimeSpan.FromMilliseconds(120))
            {
                var frame = new AudioLevelSnapshot(_framePeak, true, latest.SessionId, _framePeakAt);
                ClearFrame();
                return frame;
            }
            ClearFrame();
            return latest;
        }
    }

    /// <summary>Disabled by default; no worker or audio API is opened until a valid target is enabled.</summary>
    public void Configure(bool enabled, string? sessionId)
    {
        var targets = enabled ? AudioLevelRules.DecodeTargets(sessionId) : null;
        enabled &= targets is not null;
        lock (_sync)
        {
            if (_disposed) return;
            var target = enabled ? sessionId : null;
            if (_request.Enabled == enabled && _request.SessionId == target) return;
            _request = new(enabled, target, targets ?? [], _request.Generation + 1);
            ClearFrame();
            _audibleSessions = 0;
            Volatile.Write(ref _snapshot, new(0, false, target, _clock.GetUtcNow()));
            if (enabled && _thread is null)
            {
                _thread = new Thread(Worker) { IsBackground = true, Name = "GlimHUD selected music meter" };
                _thread.SetApartmentState(ApartmentState.MTA);
                _thread.Start();
            }
            _wake.Set();
        }
    }

    private void Worker()
    {
        IAudioLevelReader? reader = null;
        long generation = -1;
        var resolveAt = DateTimeOffset.MinValue;
        try
        {
            while (true)
            {
                MeterRequest request;
                lock (_sync)
                {
                    if (_disposed) break;
                    request = _request;
                }
                if (!request.Enabled)
                {
                    Release(reader);
                    reader = null;
                    generation = -1;
                    _wake.WaitOne();
                    continue;
                }

                var now = _clock.GetUtcNow();
                if (request.Generation != generation || now >= resolveAt)
                {
                    Release(reader);
                    reader = null;
                    generation = request.Generation;
                    try { reader = _factory.Open(request.Targets); }
                    catch { /* A removed endpoint or expired target is unavailable, not the master endpoint. */ }
                    resolveAt = now.AddSeconds(2);
                }

                var available = false;
                var reading = default(AudioLevelReading);
                try { available = reader?.TryRead(out reading) == true; }
                catch { available = false; }
                if (!available && reader is not null)
                {
                    Release(reader);
                    reader = null;
                    resolveAt = now.AddMilliseconds(500);
                }
                lock (_sync)
                {
                    // Source changes/disable take effect immediately, including a read already in flight.
                    if (!_disposed && _request.Generation == request.Generation)
                    {
                        var sampledAt = _clock.GetUtcNow();
                        var audibleSessions = available ? reading.AudibleSessions : 0;
                        var peak = audibleSessions != 0 ? AudioLevelRules.ValidPeak(reading.Peak) : 0;
                        // A quiet active stream may retain its short attack for this frame.
                        // Muting, becoming inactive, or changing any group member's audible
                        // state invalidates retained peaks from that prior stream state.
                        if (audibleSessions == 0 || audibleSessions != _audibleSessions
                            || sampledAt - _framePeakAt > TimeSpan.FromMilliseconds(120))
                            ClearFrame();
                        _audibleSessions = audibleSessions;
                        if (audibleSessions != 0 && (!_framePending || peak >= _framePeak))
                        {
                            _framePeak = peak;
                            _framePeakAt = sampledAt;
                            _framePending = true;
                        }
                        Volatile.Write(ref _snapshot, new(peak, available, request.SessionId, sampledAt));
                    }
                }
                // Only cached selected-session peaks are read here; discovery stays on its
                // two-second cadence and no process-wide timer resolution is changed.
                _wake.WaitOne(10);
            }
        }
        finally
        {
            Release(reader);
            // All selected session/endpoint COM wrappers belong to this one MTA worker.
            _wake.Dispose();
        }
    }

    private static void Release(IAudioLevelReader? reader)
    {
        try { reader?.Dispose(); }
        catch { /* A disconnected device must not terminate the metering thread or the application. */ }
    }

    private void ClearFrame()
    {
        _framePending = false;
        _framePeak = 0;
        _framePeakAt = default;
    }

    public void Dispose()
    {
        Thread? thread;
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            ClearFrame();
            _audibleSessions = 0;
            Volatile.Write(ref _snapshot, new(0, false, null, _clock.GetUtcNow()));
            thread = _thread;
            _wake.Set();
            if (thread is null) _wake.Dispose();
        }
        // A misbehaving device must not hang the UI. A blocked read still owns its
        // resources and releases them on the background worker as soon as it returns.
        if (thread is not null && thread != Thread.CurrentThread) thread.Join(750);
    }

    private sealed record MeterRequest(bool Enabled, string? SessionId, IReadOnlyList<AudioLevelTarget> Targets, long Generation);
}

internal readonly record struct AudioLevelTarget(string Id, string DeviceId, string InstanceId);
internal readonly record struct AudioLevelReading(float Peak, ulong AudibleSessions);

internal static class AudioLevelRules
{
    private const string GroupPrefix = "GlimHUD.AudioGroup.v1:";

    internal static IReadOnlyList<AudioLevelTarget>? DecodeTargets(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128 * 1024) return null;
        string[] ids;
        if (value.StartsWith(GroupPrefix, StringComparison.Ordinal))
        {
            try
            {
                ids = JsonSerializer.Deserialize<string[]>(Encoding.UTF8.GetString(
                    Convert.FromBase64String(value[GroupPrefix.Length..]))) ?? [];
            }
            catch (Exception error) when (error is FormatException or JsonException) { return null; }
            if (ids.Length is < 2 or > 64 || ids.Distinct(StringComparer.Ordinal).Count() != ids.Length) return null;
        }
        else ids = [value];

        var targets = new List<AudioLevelTarget>(ids.Length);
        foreach (var id in ids)
        {
            if (string.IsNullOrWhiteSpace(id) || id.Length > 32768) return null;
            var separator = id.IndexOf('\u001f');
            if (separator <= 0 || separator >= id.Length - 1) return null;
            targets.Add(new(id, id[..separator], id[(separator + 1)..]));
        }
        return targets;
    }

    internal static float ValidPeak(float peak) => float.IsFinite(peak) ? Math.Clamp(peak, 0, 1) : 0;
    internal static float AudiblePeak(float peak, bool muted, float volume, bool active)
        => muted || !active || !float.IsFinite(volume) || volume <= 0 ? 0 : ValidPeak(peak);
}

internal interface IAudioLevelReaderFactory
{
    IAudioLevelReader? Open(IReadOnlyList<AudioLevelTarget> targets);
}

internal interface IAudioLevelReader : IDisposable
{
    bool TryRead(out AudioLevelReading reading);
}

internal sealed class WindowsAudioLevelReaderFactory : IAudioLevelReaderFactory
{
    public IAudioLevelReader? Open(IReadOnlyList<AudioLevelTarget> targets)
    {
        if (targets.Count == 0) return null;
        var devices = new List<MMDevice>();
        var selected = new List<AudioSessionControl>();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            foreach (var group in targets.GroupBy(target => target.DeviceId, StringComparer.Ordinal))
            {
                var device = enumerator.GetDevice(group.Key);
                devices.Add(device);
                if (device.State != DeviceState.Active || device.DataFlow != DataFlow.Render) return null;
                var wanted = group.Select(target => target.InstanceId).ToHashSet(StringComparer.Ordinal);
                var sessions = device.AudioSessionManager.Sessions;
                for (var index = 0; index < sessions.Count; index++)
                {
                    var session = sessions[index];
                    var keep = false;
                    try
                    {
                        if (!session.IsSystemSoundsSession && session.GetProcessID != 0
                            && session.GetProcessID != Environment.ProcessId
                            && session.State != AudioSessionState.AudioSessionStateExpired
                            && wanted.Remove(session.GetSessionInstanceIdentifier))
                        {
                            // Obtain the selected session's meter, never MMDevice.AudioMeterInformation.
                            _ = session.AudioMeterInformation;
                            selected.Add(session);
                            keep = true;
                        }
                    }
                    finally { if (!keep) session.Dispose(); }
                }
                if (wanted.Count != 0) return null;
            }
            if (selected.Count != targets.Count) return null;
            var reader = new WindowsAudioLevelReader(devices.ToArray(), selected.ToArray());
            devices.Clear();
            selected.Clear();
            return reader;
        }
        finally
        {
            foreach (var session in selected) session.Dispose();
            foreach (var device in devices) device.Dispose();
        }
    }
}

internal sealed class WindowsAudioLevelReader(MMDevice[] devices, AudioSessionControl[] sessions) : IAudioLevelReader
{
    public bool TryRead(out AudioLevelReading reading)
    {
        reading = default;
        var peak = 0f;
        var audibleSessions = 0UL;
        foreach (var device in devices)
            if (device.State != DeviceState.Active) return false;
        for (var index = 0; index < sessions.Length; index++)
        {
            var session = sessions[index];
            var state = session.State;
            if (state == AudioSessionState.AudioSessionStateExpired) return false;
            var volume = session.SimpleAudioVolume;
            var muted = volume.Mute;
            var level = volume.Volume;
            var active = state == AudioSessionState.AudioSessionStateActive;
            if (!muted && active && float.IsFinite(level) && level > 0)
                audibleSessions |= 1UL << index;
            var value = AudioLevelRules.AudiblePeak(session.AudioMeterInformation.MasterPeakValue, muted, level, active);
            peak = Math.Max(peak, value);
        }
        reading = new(peak, audibleSessions);
        return true;
    }

    public void Dispose()
    {
        foreach (var session in sessions)
            try { session.Dispose(); } catch { }
        foreach (var device in devices)
            try { device.Dispose(); } catch { }
    }
}
