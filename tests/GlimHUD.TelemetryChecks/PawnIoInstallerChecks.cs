using System.ComponentModel;
using System.Diagnostics;
using GlimHUD.Services;

namespace GlimHUD.TelemetryChecks;

internal static class PawnIoInstallerChecks
{
    internal static async Task RunAsync()
    {
        var passed = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            passed++;
        }
        Check(PawnIoInstallerService.InstallerSha256 == "1f519a22e47187f70a1379a48ca604981c4fcf694f4e65b734aaa74a9fba3032"
            && PawnIoInstallerService.InstallerSize == 3410960, "The exact official binary digest/length must be compiled into the helper.");
        Check(PawnIoInstallerService.ResolveInstalledVersion([]) is null, "Only both missing registry entries mean no known install.");
        Check(PawnIoInstallerService.ResolveInstalledVersion(["2.1", "2.2.0"]) == new Version(2, 2, 0, 0), "Both registry views must be read dynamically and normalized before choosing the newest known version.");
        Check(PawnIoInstallerService.ResolveInstalledVersion(["2.3.0", "malformed"]) == new Version(2, 3, 0, 0)
            && PawnIoInstallerService.ResolveInstalledVersion(["2.2"], metadataUnavailable: true) == new Version(2, 2, 0, 0),
            "A known current/newer version must remain preserved even if another view cannot be parsed/read.");
        foreach (var values in new string?[][] { [null], ["malformed"], ["2.1", "malformed"] })
        {
            try { PawnIoInstallerService.ResolveInstalledVersion(values); throw new InvalidOperationException("Unknown existing version must block installation."); }
            catch (PawnIoInstalledVersionUnknownException) { passed++; }
        }
        try { PawnIoInstallerService.ResolveInstalledVersion([], metadataUnavailable: true); throw new InvalidOperationException("Unreadable metadata must not be treated as missing."); }
        catch (PawnIoInstalledVersionUnknownException) { passed++; }
        using (var fixture = new Fixture { VersionUnknown = true })
        {
            var result = await fixture.Service().InstallAsync();
            Check(result is { Status: PawnIoInstallStatus.Unavailable, ErrorCode: "InstalledVersionUnknown" } && fixture.Launches == 0 && fixture.Hashes == 0,
                "An unknown/inaccessible existing version must preserve the machine state without UAC or a possible downgrade.");
        }
        foreach (var version in new[] { new Version(2, 2), new Version(2, 2, 0), new Version(2, 2, 0, 0), new Version(2, 3, 0), new Version(3, 0) })
        {
            using var fixture = new Fixture { InstalledVersion = version };
            var result = await fixture.Service().InstallAsync();
            Check(result is { Status: PawnIoInstallStatus.Installed, ErrorCode: "AlreadyInstalled" } && fixture.Launches == 0 && fixture.Hashes == 0,
                "Current/newer known versions must never be downgraded or represented as newly repaired sensors.");
        }
        using (var fixture = new Fixture())
        {
            var result = await fixture.Service().InstallAsync();
            Check(result.Status == PawnIoInstallStatus.Unavailable && result.ErrorCode == "InstallerMissing" && fixture.Launches == 0,
                "A missing bundle must not try a URL, arbitrary executable or process launch.");
        }
        foreach (var exit in new[] { 0, 3010, 1223, 5, 183, -1 })
        {
            using var fixture = new Fixture { Exit = exit, InstalledVersion = new Version(2, 1, 0) };
            fixture.CreatePayload();
            var result = await fixture.Service().InstallAsync();
            var expected = exit == 0 ? PawnIoInstallStatus.Installed : exit == 3010 ? PawnIoInstallStatus.RebootRequired
                : exit == 1223 ? PawnIoInstallStatus.Cancelled : PawnIoInstallStatus.Failed;
            Check(result.Status == expected && result.Win32Code == exit && fixture.Launches == 1, "Real installer exit codes must be mapped without treating arbitrary failures as success.");
            var start = fixture.LastStart!;
            Check(start.UseShellExecute && start.Verb == "runas" && start.FileName == fixture.Payload
                && start.WorkingDirectory == Path.GetDirectoryName(fixture.Payload), "Only this fixed bundled driver executable may receive an explicit elevation request.");
            Check(start.ArgumentList.SequenceEqual(new[] { "-install", "-silent" }), "Only documented install/silent switches are allowed; no reboot, unrestricted or arbitrary arguments.");
            Check(fixture.PayloadLockedAtLaunch && fixture.ProcessDisposed, "The authenticated payload must remain locked through the installer lifetime and process handles released.");
        }
        using (var fixture = new Fixture { Digest = new string('0', 64) })
        {
            fixture.CreatePayload();
            var result = await fixture.Service().InstallAsync();
            Check(result is { Status: PawnIoInstallStatus.Unavailable, ErrorCode: "InstallerSignature" } && fixture.Launches == 0, "A modified bundle must never reach UAC or process execution.");
        }
        using (var fixture = new Fixture())
        {
            fixture.CreatePayload(length: 10);
            var result = await fixture.Service().InstallAsync();
            Check(result.Status == PawnIoInstallStatus.Unavailable && fixture.Hashes == 0 && fixture.Launches == 0, "Wrong-sized files must fail before hashing/launch.");
        }
        using (var fixture = new Fixture { UseRealHash = true })
        {
            fixture.CreatePayload();
            var result = await fixture.Service().InstallAsync();
            Check(result is { Status: PawnIoInstallStatus.Unavailable, ErrorCode: "InstallerSignature" } && fixture.Launches == 0, "Real SHA256 hashing must reject a fabricated same-length executable.");
        }
        foreach (var code in new[] { 1223, 5, 740 })
        {
            using var fixture = new Fixture { LaunchError = new Win32Exception(code) };
            fixture.CreatePayload();
            var result = await fixture.Service().InstallAsync();
            Check(result.Status == (code == 1223 ? PawnIoInstallStatus.Cancelled : PawnIoInstallStatus.Failed) && result.Win32Code == code,
                "Declining UAC must cancel cleanly and other launch errors must keep their actual Win32 code.");
        }
        using (var fixture = new Fixture { ReturnNullProcess = true })
        {
            fixture.CreatePayload();
            var result = await fixture.Service().InstallAsync();
            Check(result.Status == PawnIoInstallStatus.Failed && result.ErrorCode == "InstallerLaunchFailed", "A missing process result must not be reported as successful installation.");
        }
        using (var fixture = new Fixture())
        {
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            var result = await fixture.Service().InstallAsync(cancellation.Token);
            Check(result.Status == PawnIoInstallStatus.Cancelled && fixture.Hashes == 0 && fixture.Launches == 0, "Pre-cancellation must have no hashing/UAC/process side effects.");
        }
        using (var fixture = new Fixture())
        {
            fixture.CreatePayload();
            using var cancellation = new CancellationTokenSource();
            fixture.AfterHash = () => cancellation.Cancel();
            var result = await fixture.Service().InstallAsync(cancellation.Token);
            Check(result.Status == PawnIoInstallStatus.Cancelled && fixture.Launches == 0, "Cancellation immediately after authentication must still prevent launch.");
        }
        using (var fixture = new Fixture())
        {
            fixture.CreatePayload();
            using var cancellation = new CancellationTokenSource();
            fixture.AfterLaunch = () => cancellation.Cancel();
            var result = await fixture.Service().InstallAsync(cancellation.Token);
            Check(result.Status == PawnIoInstallStatus.Installed && fixture.ProcessDisposed, "Cancellation after launch must await the real outcome without killing the installer or faking cancellation.");
        }
        using (var fixture = new Fixture())
        {
            fixture.CreatePayload();
            fixture.PendingExit = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var service = fixture.Service();
            var first = service.InstallAsync();
            var second = await service.InstallAsync();
            Check(second is { Status: PawnIoInstallStatus.Unavailable, ErrorCode: "Busy" } && fixture.Launches == 1, "Concurrent requests must not create duplicate UAC/install operations.");
            fixture.PendingExit.SetResult(3010);
            Check((await first).Status == PawnIoInstallStatus.RebootRequired, "The existing installation must retain its actual restart-required result.");
        }
        Console.WriteLine($"Passed {passed} driver installer checks (fake launcher/process only; no UAC, installer or driver execution).");
    }

    private sealed class Fixture : IDisposable
    {
        private static readonly string Root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "GlimHUD.PawnIoInstallerChecks"));
        public string Directory { get; } = Path.Combine(Root, Guid.NewGuid().ToString("N"));
        public string Payload => Path.Combine(Directory, "tools", "PawnIO", "PawnIO_setup.exe");
        public Version? InstalledVersion { get; set; }
        public bool VersionUnknown { get; set; }
        public string Digest { get; set; } = PawnIoInstallerService.InstallerSha256;
        public int Exit { get; set; }
        public int Launches { get; private set; }
        public int Hashes { get; private set; }
        public bool PayloadLockedAtLaunch { get; private set; }
        public bool ProcessDisposed { get; private set; }
        public bool UseRealHash { get; set; }
        public bool ReturnNullProcess { get; set; }
        public Win32Exception? LaunchError { get; set; }
        public ProcessStartInfo? LastStart { get; private set; }
        public Action? AfterHash { get; set; }
        public Action? AfterLaunch { get; set; }
        public TaskCompletionSource<int>? PendingExit { get; set; }
        public void CreatePayload(long length = PawnIoInstallerService.InstallerSize)
        {
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(Payload)!);
            using var stream = File.Create(Payload);
            stream.SetLength(length);
        }
        public PawnIoInstallerService Service() => new(Directory, () => VersionUnknown ? throw new PawnIoInstalledVersionUnknownException() : InstalledVersion, async (stream, token) =>
        {
            Hashes++;
            var hash = UseRealHash ? await PawnIoInstallerService.HashAsync(stream, token) : Digest;
            AfterHash?.Invoke();
            return hash;
        }, start =>
        {
            Launches++; LastStart = start;
            try { using var writable = File.Open(Payload, FileMode.Open, FileAccess.Write, FileShare.ReadWrite); }
            catch (IOException) { PayloadLockedAtLaunch = true; }
            if (LaunchError is not null) throw LaunchError;
            if (ReturnNullProcess) return null;
            AfterLaunch?.Invoke();
            return new FakeProcess(() => PendingExit?.Task ?? Task.FromResult(Exit), () => ProcessDisposed = true);
        });
        public void Dispose()
        {
            var full = Path.GetFullPath(Directory);
            if (full.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && System.IO.Directory.Exists(full))
                System.IO.Directory.Delete(full, recursive: true);
        }
    }
    private sealed class FakeProcess(Func<Task<int>> wait, Action dispose) : IPawnIoInstallerProcess
    {
        public Task<int> WaitForExitAsync() => wait();
        public void Dispose() => dispose();
    }
}
