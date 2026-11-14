using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace GlimHUD.Services;

/// <summary>Requests elevation only from an explicit user action, without changing Windows policy.</summary>
internal static class AppRestartService
{
    internal const string RestartArgument = "--restart-from";
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

    internal static ProcessStartInfo CreateStartInfo(string executablePath, int parentProcessId)
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
        start.ArgumentList.Add("--settings");
        return start;
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
