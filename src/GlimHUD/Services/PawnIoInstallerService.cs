using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security;
using System.Security.Cryptography;
using Microsoft.Win32;

namespace GlimHUD.Services;

public enum PawnIoInstallStatus { Installed, RebootRequired, Cancelled, Failed, Unavailable }
public sealed record PawnIoInstallResult(PawnIoInstallStatus Status, int? Win32Code = null, string? ErrorCode = null);

/// <summary>
/// Installs the exact bundled official PawnIO release only after an explicit user
/// action. Construction never installs/loads a driver or requests elevation.
/// This machine-wide driver installer deliberately uses runas; application
/// updates continue to use their separate ordinary-user launcher.
/// </summary>
public sealed class PawnIoInstallerService
{
    internal const string InstallerSha256 = "1f519a22e47187f70a1379a48ca604981c4fcf694f4e65b734aaa74a9fba3032";
    internal const long InstallerSize = 3410960;
    internal static readonly Version BundledVersion = new(2, 2, 0, 0);
    private const int UserCancelled = 1223;
    private const int RestartRequired = 3010;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _baseDirectory;
    private readonly Func<Version?> _installedVersion;
    private readonly Func<Stream, CancellationToken, Task<string>> _hash;
    private readonly Func<ProcessStartInfo, IPawnIoInstallerProcess?> _launch;

    public PawnIoInstallerService() : this(AppContext.BaseDirectory, ReadInstalledVersion, HashAsync, StartInstaller) { }
    internal PawnIoInstallerService(string baseDirectory, Func<Version?> installedVersion,
        Func<Stream, CancellationToken, Task<string>> hash, Func<ProcessStartInfo, IPawnIoInstallerProcess?> launch)
    {
        _baseDirectory = Path.GetFullPath(baseDirectory);
        _installedVersion = installedVersion;
        _hash = hash;
        _launch = launch;
    }

    /// <summary>
    /// Cancellation is honored before launch. Once Windows starts the installer,
    /// wait for its real exit without killing it or reporting a misleading cancel.
    /// An already-installed current/newer version is preserved, never downgraded.
    /// </summary>
    public async Task<PawnIoInstallResult> InstallAsync(CancellationToken cancellationToken = default)
    {
        var acquired = false;
        try
        {
            acquired = await _gate.WaitAsync(0, cancellationToken);
            if (!acquired) return new(PawnIoInstallStatus.Unavailable, ErrorCode: "Busy");
            cancellationToken.ThrowIfCancellationRequested();
            var installed = _installedVersion();
            if (installed is not null && NormalizeVersion(installed) >= BundledVersion)
                return new(PawnIoInstallStatus.Installed, ErrorCode: "AlreadyInstalled");
            var path = Path.GetFullPath(Path.Combine(_baseDirectory, "tools", "PawnIO", "PawnIO_setup.exe"));
            if (!File.Exists(path)) return new(PawnIoInstallStatus.Unavailable, ErrorCode: "InstallerMissing");
            // Bundle components must be ordinary files/directories. The path and
            // switches never come from a URL, settings value or external metadata.
            foreach (var component in new[] { _baseDirectory, Path.Combine(_baseDirectory, "tools"), Path.GetDirectoryName(path)!, path })
                if ((File.GetAttributes(component) & FileAttributes.ReparsePoint) != 0)
                    return new(PawnIoInstallStatus.Unavailable, ErrorCode: "InstallerPath");
            // Keep the authenticated executable locked against writes/deletion
            // throughout the UAC handoff and installer lifetime.
            await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (file.Length != InstallerSize) return new(PawnIoInstallStatus.Unavailable, ErrorCode: "InstallerSignature");
            var digest = await _hash(file, cancellationToken);
            if (!string.Equals(digest, InstallerSha256, StringComparison.OrdinalIgnoreCase))
                return new(PawnIoInstallStatus.Unavailable, ErrorCode: "InstallerSignature");
            cancellationToken.ThrowIfCancellationRequested();
            using var process = _launch(CreateStartInfo(path));
            if (process is null) return new(PawnIoInstallStatus.Failed, ErrorCode: "InstallerLaunchFailed");
            var exit = await process.WaitForExitAsync();
            return exit switch
            {
                0 => new(PawnIoInstallStatus.Installed, 0),
                RestartRequired => new(PawnIoInstallStatus.RebootRequired, exit),
                UserCancelled => new(PawnIoInstallStatus.Cancelled, exit, "UserCancelled"),
                _ => new(PawnIoInstallStatus.Failed, exit, "InstallerFailed")
            };
        }
        catch (Win32Exception error) when (error.NativeErrorCode == UserCancelled)
        { return new(PawnIoInstallStatus.Cancelled, UserCancelled, "UserCancelled"); }
        catch (PawnIoInstalledVersionUnknownException)
        { return new(PawnIoInstallStatus.Unavailable, ErrorCode: "InstalledVersionUnknown"); }
        catch (OperationCanceledException) { return new(PawnIoInstallStatus.Cancelled, ErrorCode: "Cancelled"); }
        catch (Win32Exception error) { return new(PawnIoInstallStatus.Failed, error.NativeErrorCode, "InstallerLaunchFailed"); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException
            or CryptographicException or InvalidOperationException or ArgumentException or NotSupportedException)
        { return new(PawnIoInstallStatus.Unavailable, ErrorCode: "InstallerUnavailable"); }
        finally { if (acquired) _gate.Release(); }
    }

    internal static ProcessStartInfo CreateStartInfo(string installer)
    {
        var start = new ProcessStartInfo(installer)
        {
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = Path.GetDirectoryName(installer)!,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        // Official 2.2.0 embedded usage documents these exact switches. Silent
        // mode reports a required Windows restart as 3010; we never reboot it.
        start.ArgumentList.Add("-install");
        start.ArgumentList.Add("-silent");
        return start;
    }
    internal static Version NormalizeVersion(Version version)
        => new(version.Major, version.Minor, Math.Max(0, version.Build), Math.Max(0, version.Revision));
    internal static async Task<string> HashAsync(Stream input, CancellationToken token)
        => Convert.ToHexString(await SHA256.HashDataAsync(input, token));
    private static Version? ReadInstalledVersion()
    {
        if (!OperatingSystem.IsWindows()) return null;
        var values = new List<string?>();
        var unavailable = false;
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var key = hive.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO", writable: false);
                if (key is not null) values.Add(key.GetValue("DisplayVersion") as string);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException) { unavailable = true; }
        }
        return ResolveInstalledVersion(values, unavailable);
    }
    internal static Version? ResolveInstalledVersion(IEnumerable<string?> values, bool metadataUnavailable = false)
    {
        Version? newest = null;
        var unknown = metadataUnavailable;
        foreach (var value in values)
        {
            if (!Version.TryParse(value, out var version)) { unknown = true; continue; }
            version = NormalizeVersion(version);
            if (newest is null || version > newest) newest = version;
        }
        // A known current/newer install is always preserved. Otherwise unreadable
        // or malformed existing metadata must not be mistaken for a missing driver.
        if (unknown && (newest is null || newest < BundledVersion)) throw new PawnIoInstalledVersionUnknownException();
        return newest;
    }
    private static IPawnIoInstallerProcess? StartInstaller(ProcessStartInfo start)
    {
        var process = Process.Start(start);
        return process is null ? null : new InstallerProcess(process);
    }
    private sealed class InstallerProcess(Process process) : IPawnIoInstallerProcess
    {
        public async Task<int> WaitForExitAsync() { await process.WaitForExitAsync(); return process.ExitCode; }
        public void Dispose() => process.Dispose();
    }
}

internal interface IPawnIoInstallerProcess : IDisposable
{
    Task<int> WaitForExitAsync();
}
internal sealed class PawnIoInstalledVersionUnknownException : Exception;
