using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace GlimHUD.Services;

/// <summary>
/// A one-use verified update handoff. Elevated callers can only delegate to an
/// existing ordinary desktop Explorer; there is no elevated launch fallback.
/// A successful handoff does not mean the installer has completed.
/// </summary>
public sealed class UpdateInstallerLauncher
{
    private readonly ProcessStartInfo _start;
    private readonly Func<bool> _verifyInstaller;
    private readonly bool _requiresDesktopBroker;
    private readonly IUnelevatedProcessLauncher? _desktopBroker;
    private readonly Func<ProcessStartInfo, bool> _startNormally;
    private readonly Func<bool> _isElevated;
    private int _attempted;
    private string? _errorCode;

    public string? ErrorCode => Volatile.Read(ref _errorCode);

    internal UpdateInstallerLauncher(ProcessStartInfo start, Func<bool> verifyInstaller,
        bool requiresDesktopBroker, IUnelevatedProcessLauncher? desktopBroker,
        Func<ProcessStartInfo, bool> startNormally, Func<bool>? isElevated = null)
    {
        _start = start;
        _verifyInstaller = verifyInstaller;
        _requiresDesktopBroker = requiresDesktopBroker;
        _desktopBroker = desktopBroker;
        _startNormally = startNormally;
        _isElevated = isElevated ?? (() => AppRestartService.IsAdministrator);
    }

    /// <summary>
    /// Called only after the user requests installation and the HUD has saved and
    /// stopped. Cancellation is intentionally not accepted once handoff starts:
    /// reporting cancellation while Explorer might still execute would be ambiguous.
    /// </summary>
    public async Task<bool> TryLaunchAsync()
    {
        if (Interlocked.Exchange(ref _attempted, 1) != 0) return Fail("InstallerAlreadyStarted");
        try
        {
            if (!await Task.Run(_verifyInstaller)) return Fail("InstallerSignature");
            if (_requiresDesktopBroker)
            {
                if (_desktopBroker is null || !_desktopBroker.IsAvailable()) return Fail("DesktopUnavailable");
                // Only the service's constant installer switches are accepted.
                // File and working directory are separate ShellExecute arguments.
                var arguments = string.Join(" ", _start.ArgumentList);
                return await _desktopBroker.TryLaunchAsync(_start.FileName, arguments, _start.WorkingDirectory)
                    || Fail("InstallerLaunchFailed");
            }
            if (_isElevated()) return Fail("ElevatedInstall");
            return await Task.Run(() => _startNormally(_start)) || Fail("InstallerLaunchFailed");
        }
        catch (Exception error) when (error is Win32Exception or COMException or IOException
            or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        { return Fail("InstallerLaunchFailed"); }
    }

    private bool Fail(string error) { Volatile.Write(ref _errorCode, error); return false; }
    internal static bool StartNormally(ProcessStartInfo start)
    {
        if (AppRestartService.IsAdministrator) return false;
        using var process = Process.Start(start);
        return process is not null;
    }
}
