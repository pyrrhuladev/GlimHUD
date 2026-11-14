using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace GlimHUD.Services;

/// <summary>Requests ordinary Windows consent for startup preference or manual restart; never changes Windows policy.</summary>
internal static class AppRestartService
{
    internal const string RestartArgument = "--restart-from";
    internal const string SkipAdministratorRequestArgument = "--skip-admin-request";
    private const int UserCancelled = 1223;
    private const int NoPackageIdentity = 15700;

    internal static bool IsAdministrator
    {
        get
        {
            if (!OperatingSystem.IsWindows()) return false;
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    internal static bool CanRequestElevation => !IsAdministrator && !IsPackaged
        && IsRestartableExecutable(Environment.ProcessPath);

    private static bool IsPackaged
    {
        get
        {
            if (!OperatingSystem.IsWindows()) return false;
            uint length = 0;
            return GetCurrentPackageFullName(ref length, IntPtr.Zero) != NoPackageIdentity;
        }
    }

    internal static bool IsRestartableExecutable(string? path) =>
        !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path)
        && string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(Path.GetFileName(path), "dotnet.exe", StringComparison.OrdinalIgnoreCase);

    internal static ProcessStartInfo CreateStartInfo(string executablePath, int parentProcessId, bool openSettings = true)
    {
        if (!IsRestartableExecutable(executablePath))
            throw new ArgumentException("Restart requires the deployed application executable.", nameof(executablePath));
        if (parentProcessId <= 0) throw new ArgumentOutOfRangeException(nameof(parentProcessId));
        var fullPath = Path.GetFullPath(executablePath);
        var start = new ProcessStartInfo(fullPath)
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Path.GetDirectoryName(fullPath)!
        };
        start.ArgumentList.Add(RestartArgument);
        start.ArgumentList.Add(parentProcessId.ToString(CultureInfo.InvariantCulture));
        start.ArgumentList.Add(SkipAdministratorRequestArgument);
        if (openSettings) start.ArgumentList.Add("--settings");
        return start;
    }

    internal static bool ShouldRequestStartupElevation(bool enabled, IReadOnlyList<string> arguments,
        string? executablePath, bool isAdministrator, bool isPackaged)
        => enabled && !isAdministrator && !isPackaged && IsRestartableExecutable(executablePath)
            && !arguments.Any(argument => string.Equals(argument, SkipAdministratorRequestArgument, StringComparison.OrdinalIgnoreCase)
                || string.Equals(argument, RestartArgument, StringComparison.OrdinalIgnoreCase)
                || string.Equals(argument, "--render-preview", StringComparison.OrdinalIgnoreCase)
                || string.Equals(argument, "--smoke-test", StringComparison.OrdinalIgnoreCase));

    internal static StartupElevationOutcome RequestStartupElevation(bool enabled, IReadOnlyList<string> arguments,
        Action<Exception>? reportFailure = null)
        => RequestStartupElevation(enabled, arguments, Environment.ProcessPath, Environment.ProcessId,
            IsAdministrator, IsPackaged, start => Process.Start(start), reportFailure);

    // Runtime state and the launcher are injected so checks never open a UAC prompt.
    internal static StartupElevationOutcome RequestStartupElevation(bool enabled, IReadOnlyList<string> arguments,
        string? executablePath, int parentProcessId, bool isAdministrator, bool isPackaged,
        Func<ProcessStartInfo, Process?> launcher, Action<Exception>? reportFailure = null)
    {
        if (!ShouldRequestStartupElevation(enabled, arguments, executablePath, isAdministrator, isPackaged))
            return StartupElevationOutcome.NotRequested;
        try
        {
            var openSettings = arguments.Contains("--settings", StringComparer.OrdinalIgnoreCase);
            using var process = launcher(CreateStartInfo(executablePath!, parentProcessId, openSettings));
            if (process is null) throw new InvalidOperationException("Windows did not start the application.");
            return StartupElevationOutcome.RestartStarted;
        }
        catch (Win32Exception error) when (error.NativeErrorCode == UserCancelled)
        {
            return StartupElevationOutcome.Cancelled;
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException or IOException
            or UnauthorizedAccessException or NotSupportedException or System.Security.SecurityException or ArgumentException)
        {
            reportFailure?.Invoke(error);
            return StartupElevationOutcome.Failed;
        }
    }

    internal static bool RequestElevation() => RequestElevation(
        Environment.ProcessPath!, Environment.ProcessId, start => Process.Start(start));

    // The injected launcher lets checks cover decline without ever opening UAC.
    internal static bool RequestElevation(string executablePath, int parentProcessId,
        Func<ProcessStartInfo, Process?> launcher)
    {
        try
        {
            using var process = launcher(CreateStartInfo(executablePath, parentProcessId));
            if (process is null) throw new InvalidOperationException("Windows did not start the application.");
            return true;
        }
        catch (Win32Exception error) when (error.NativeErrorCode == UserCancelled)
        {
            return false;
        }
    }

    internal static int? GetRestartParent(IReadOnlyList<string> arguments, int currentProcessId)
    {
        var index = -1;
        for (var i = 0; i < arguments.Count; i++)
        {
            if (!string.Equals(arguments[i], RestartArgument, StringComparison.Ordinal)) continue;
            if (index >= 0) throw new ArgumentException("Only one restart parent can be supplied.");
            index = i;
        }
        if (index < 0) return null;
        if (index + 1 >= arguments.Count
            || !int.TryParse(arguments[index + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var processId)
            || processId <= 0 || processId == currentProcessId)
            throw new ArgumentException("The restart parent process is invalid.");
        return processId;
    }

    internal static async Task<bool> WaitForRestartParentAsync(IReadOnlyList<string> arguments,
        TimeSpan? timeout = null)
    {
        var parentId = GetRestartParent(arguments, Environment.ProcessId);
        if (!parentId.HasValue) return true;
        try
        {
            using var parent = Process.GetProcessById(parentId.Value);
            using var deadline = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(15));
            await parent.WaitForExitAsync(deadline.Token);
            return true;
        }
        catch (ArgumentException) { return true; }
        catch (OperationCanceledException) { return false; }
        catch (InvalidOperationException) { return true; }
        catch (Win32Exception) { return false; }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref uint packageFullNameLength, IntPtr packageFullName);
}

internal enum StartupElevationOutcome { NotRequested, RestartStarted, Cancelled, Failed }
