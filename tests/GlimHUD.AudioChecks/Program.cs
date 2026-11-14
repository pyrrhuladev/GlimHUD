using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Windows.Media.Control;
using GlimHUD.Services;
using GlimHUD.Localization;

if (args is ["--silent-fixture", var readyFile])
{
    try
    {
        using var outputDevice = new WasapiOut(AudioClientShareMode.Shared, 100);
        outputDevice.Init(new SilenceProvider(new WaveFormat(48000, 16, 2)));
        outputDevice.Play();
        await File.WriteAllTextAsync(readyFile, "ready");
        await Task.Delay(TimeSpan.FromSeconds(20));
        outputDevice.Stop();
        return 0;
    }
    catch (Exception error)
    {
        await File.WriteAllTextAsync(readyFile + ".error.txt", error.ToString());
        return 1;
    }
}

if (args is ["--fixture-check", var fixtureOutput])
    return await SilentFixtureCheck.RunAsync(fixtureOutput);

if (args is ["--diagnostics", var output])
{
    var media = new List<object>();
    try
    {
        var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        foreach (var session in manager.GetSessions())
            media.Add(new { SourceAppId = session.SourceAppUserModelId, Playback = session.GetPlaybackInfo().PlaybackStatus.ToString() });
    }
    catch (Exception e) { media.Add(new { Error = e.GetType().Name }); }
    var audio = new List<object>();
    try
    {
        using var enumerator = new MMDeviceEnumerator();
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        using (device)
        {
            var sessions = device.AudioSessionManager.Sessions;
            for (var i = 0; i < sessions.Count; i++)
            {
                using var session = sessions[i];
                if (session.IsSystemSoundsSession) continue;
                var pid = (int)session.GetProcessID;
                using var process = Process.GetProcessById(pid);
                audio.Add(new { ProcessId = pid, ProcessName = process.ProcessName, AppId = Native.AppId(pid), State = session.State.ToString() });
            }
        }
    }
    catch (Exception e) { audio.Add(new { Error = e.ToString() }); }
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
    using var audioService = new AudioSessionService();
    var selected = await audioService.GetSnapshotAsync("ru.yandex.desktop.music");
    await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new { Media = media, Audio = audio,
        YandexMusic = new { selected.IsAvailable, selected.DisplayName, selected.Status,
            SelectedSessionId = selected.SessionId, SessionCount = selected.Sessions.Count } }, new JsonSerializerOptions { WriteIndented = true }));
    return 0;
}
var passed = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    Console.WriteLine("PASS " + message);
    passed++;
}
const string source = "ru.yandex.desktop.music";
Check(AudioIdentityRules.GetDisplayName("Yandex Music.exe") == "Yandex Music", "Friendly player names default to English.");
try
{
    L10n.SetLanguage("ru");
    Check(AudioIdentityRules.GetDisplayName("Yandex Music.exe") == "Яндекс Музыка", "Russian changes the player label.");
    Check(AudioIdentityRules.MatchesSource(source, "Yandex Music.exe", null), "Changing the display language preserves source matching.");
}
finally { L10n.SetLanguage("en"); }
Check(AudioIdentityRules.MatchesSource(source, "Яндекс Музыка", null), "Yandex desktop AUMID resolves actual Cyrillic executable without package identity.");
Check(AudioIdentityRules.MatchesSource(source, "Яндекс Музыка.exe", null), "Cyrillic executable extension is accepted.");
Check(AudioIdentityRules.MatchesSource(source, "Yandex Music", null), "English dedicated executable alias is accepted.");
Check(!AudioIdentityRules.MatchesSource(source, "browser", null), "Yandex Browser is never mistaken for Yandex Music.");
Check(!AudioIdentityRules.MatchesSource(source, "firefox", null), "Unrelated browser is rejected.");
Check(!AudioIdentityRules.MatchesSource(source, "Telegram", null), "Unrelated application is rejected.");
Check(!AudioIdentityRules.MatchesSource("ru.yandex.desktop.music.fake", "Яндекс Музыка", null), "Source IDs require an exact known alias.");
Check(AudioIdentityRules.MatchesSource("Chrome.exe", "chrome", null), "Plain executable media source still resolves.");
Check(AudioIdentityRules.MatchesSource("Package!Music", "OtherName", "Package!Music"), "Exact packaged AUMID still resolves.");
var mixed = new[]
{
    new AudioIdentityCandidate("music-a", "Яндекс Музыка", null, @"C:\Music\Яндекс Музыка.exe"),
    new AudioIdentityCandidate("music-b", "Яндекс Музыка", null, @"C:\Music\Яндекс Музыка.exe"),
    new AudioIdentityCandidate("browser", "firefox", null, @"C:\Firefox\firefox.exe"),
    new AudioIdentityCandidate("game", "game", null, @"C:\Games\game.exe")
};
Check(AudioIdentityRules.SelectTargets(mixed, source).Order().SequenceEqual(new[] { "music-a", "music-b" }), "All streams of one verified dedicated music image are grouped; game/browser remain untouched.");
Check(AudioIdentityRules.SelectTargets(mixed.Select(item => item.Id == "music-b" ? item with { ExecutablePath = @"D:\Other\Яндекс Музыка.exe" } : item).ToArray(), source).Count == 0,
    "Two different executable installations remain ambiguous.");
Check(AudioIdentityRules.SelectTargets(new[] { new AudioIdentityCandidate("a", "chrome", null, @"C:\Chrome.exe"), new AudioIdentityCandidate("b", "chrome", null, @"C:\Chrome.exe") }, "chrome.exe").Count == 0,
    "Multiple browser audio sessions require an explicit selection.");
Check(AudioIdentityRules.SelectTargets(mixed.Select(item => item with { ExecutablePath = null }).ToArray(), source).Count == 0,
    "Unverified paths cannot group several application sessions.");
using (var service = new AudioSessionService())
{
    var missing = "GlimHUD.AudioChecks.Missing." + Guid.NewGuid().ToString("N");
    Check(!await service.SetVolumeAsync(.3f, source, missing), "Missing explicit session never falls back to the music application.");
    Check(!await service.ToggleMutedAsync(source, missing), "Mute toggle rejects a missing explicit session without a fallback.");
    Check(!await service.SetMutedAsync(true, source, "GlimHUD.AudioGroup.v1:invalid"), "Malformed group target is rejected without a mutation.");
    Check(!await service.ToggleMutedAsync(source, "GlimHUD.AudioGroup.v1:invalid"), "Mute toggle rejects a malformed group without a mutation.");
    Check(!await service.SetVolumeAsync(float.NaN, source), "Non-finite volume is rejected.");
}
Console.WriteLine($"{passed} checks passed.");
return 0;

static class Native
{
    public static string? AppId(int pid)
    {
        using var process = OpenProcess(0x1000, false, pid);
        if (process.IsInvalid) return null;
        uint length = 0;
        if (GetApplicationUserModelId(process, ref length, null) != 122 || length > 32768) return null;
        var buffer = new StringBuilder((int)length);
        return GetApplicationUserModelId(process, ref length, buffer) == 0 ? buffer.ToString() : null;
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern int GetApplicationUserModelId(SafeProcessHandle process, ref uint length, StringBuilder? appId);
}

static class SilentFixtureCheck
{
    public static async Task<int> RunAsync(string outputPath)
    {
        var absoluteOutput = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(absoluteOutput)!);
        var ready = absoluteOutput + ".fixture-ready";
        var start = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };
        if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(typeof(Native).Assembly.Location);
        start.ArgumentList.Add("--silent-fixture");
        start.ArgumentList.Add(ready);
        using var fixture = new Process { StartInfo = start };
        AudioSessionInfo? initial = null;
        using var service = new AudioSessionService();
        try
        {
            if (File.Exists(ready)) File.Delete(ready);
            if (!fixture.Start()) throw new InvalidOperationException("Cannot launch the silent fixture.");
            for (var attempt = 0; attempt < 70 && !File.Exists(ready) && !fixture.HasExited; attempt++)
                await Task.Delay(100);
            if (!File.Exists(ready)) throw new InvalidOperationException("Silent WASAPI fixture could not open an endpoint.");
            for (var attempt = 0; attempt < 30 && initial is null; attempt++)
            {
                var snapshot = await service.GetSnapshotAsync(null);
                initial = snapshot.Sessions.FirstOrDefault(item => item.ProcessId == fixture.Id);
                if (initial is null) await Task.Delay(100);
            }
            if (initial is null || initial.ProcessId != fixture.Id)
                throw new InvalidOperationException("Own child audio session was not found; no session was changed.");

            var volumeSet = await service.SetVolumeAsync(.35f, null, initial.Id);
            var afterVolume = await service.GetSnapshotAsync(null, initial.Id);
            if (!volumeSet || !afterVolume.IsAvailable || Math.Abs(afterVolume.Volume - .35f) > .005f)
                throw new InvalidOperationException("Own silent fixture volume did not round-trip.");
            var muted = await service.SetMutedAsync(true, null, initial.Id);
            var afterMute = await service.GetSnapshotAsync(null, initial.Id);
            if (!muted || !afterMute.IsMuted) throw new InvalidOperationException("Own silent fixture mute did not round-trip.");
            var unmuted = await service.SetMutedAsync(false, null, initial.Id);
            var afterUnmute = await service.GetSnapshotAsync(null, initial.Id);
            if (!unmuted || afterUnmute.IsMuted) throw new InvalidOperationException("Own silent fixture unmute did not round-trip.");
            var toggledMute = await service.ToggleMutedAsync(null, initial.Id);
            var afterToggleMute = await service.GetSnapshotAsync(null, initial.Id);
            if (!toggledMute || !afterToggleMute.IsMuted)
                throw new InvalidOperationException("Own silent fixture fresh mute toggle did not mute.");
            var toggledUnmute = await service.ToggleMutedAsync(null, initial.Id);
            var afterToggleUnmute = await service.GetSnapshotAsync(null, initial.Id);
            if (!toggledUnmute || afterToggleUnmute.IsMuted)
                throw new InvalidOperationException("Own silent fixture fresh mute toggle did not unmute.");
            var rapidToggles = await Task.WhenAll(
                service.ToggleMutedAsync(null, initial.Id), service.ToggleMutedAsync(null, initial.Id));
            var afterRapidToggles = await service.GetSnapshotAsync(null, initial.Id);
            if (rapidToggles.Any(success => !success) || afterRapidToggles.IsMuted)
                throw new InvalidOperationException("Two serialized toggles did not restore the own fixture's state.");
            await File.WriteAllTextAsync(absoluteOutput, JsonSerializer.Serialize(new
            {
                Success = true,
                FixtureProcessId = fixture.Id,
                TargetProcessId = initial.ProcessId,
                VolumeSet = afterVolume.Volume,
                Muted = afterMute.IsMuted,
                Unmuted = !afterUnmute.IsMuted,
                FreshMuteToggle = afterToggleMute.IsMuted,
                FreshUnmuteToggle = !afterToggleUnmute.IsMuted,
                RapidTogglesRestoredState = !afterRapidToggles.IsMuted,
                UserApplicationsMutated = false,
                OutputAmplitude = 0
            }, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (Exception error)
        {
            await File.WriteAllTextAsync(absoluteOutput, JsonSerializer.Serialize(new { Success = false, Error = error.ToString() }, new JsonSerializerOptions { WriteIndented = true }));
            return 1;
        }
        finally
        {
            if (initial is not null)
            {
                await service.SetVolumeAsync(initial.Volume, null, initial.Id);
                await service.SetMutedAsync(initial.IsMuted, null, initial.Id);
            }
            try { if (!fixture.HasExited) fixture.Kill(); } catch (InvalidOperationException) { }
        }
    }
}
