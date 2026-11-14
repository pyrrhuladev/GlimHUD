using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace GlimHUD.Services;

internal enum StartupUnavailableReason { None, UnsupportedExecutable, Packaged, ReadFailed }

internal sealed record StartupState(bool IsEnabled, bool UsesCurrentExecutable,
    bool CanChange, StartupUnavailableReason UnavailableReason = StartupUnavailableReason.None);

/// <summary>
/// Per-user sign-in registration. Construction and reads never create keys or change
/// Windows settings; only an explicit SetEnabled call may write our named value.
/// </summary>
internal sealed class StartupService
{
    internal const string ValueName = "GlimHUD";
    internal const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private readonly IStartupRegistrationStore _store;
    private readonly string? _executablePath;
    private readonly bool _packaged;

    internal StartupService() : this(new WindowsStartupRegistrationStore(), Environment.ProcessPath, IsPackaged()) { }

    internal StartupService(IStartupRegistrationStore store, string? executablePath, bool packaged = false)
    {
        _store = store;
        _executablePath = executablePath;
        _packaged = packaged;
    }

    internal StartupState ReadState()
    {
        if (_packaged) return new(false, false, false, StartupUnavailableReason.Packaged);
        if (!IsSupportedExecutable(_executablePath))
            return new(false, false, false, StartupUnavailableReason.UnsupportedExecutable);
        try
        {
            var command = _store.ReadCommand();
            return new(!string.IsNullOrWhiteSpace(command),
                string.Equals(command?.Trim(), BuildCommand(_executablePath!), StringComparison.OrdinalIgnoreCase), true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return new(false, false, false, StartupUnavailableReason.ReadFailed);
        }
    }

    internal StartupState SetEnabled(bool enabled)
    {
        if (_packaged || !IsSupportedExecutable(_executablePath))
            throw new InvalidOperationException("Autostart requires a deployed, unpackaged application executable.");
        var existing = _store.ReadCommand();
        if (enabled)
        {
            var command = BuildCommand(_executablePath!);
            if (!string.Equals(existing, command, StringComparison.OrdinalIgnoreCase)) _store.WriteCommand(command);
        }
        else if (existing is not null) _store.DeleteCommand();

        var state = ReadState();
        if (!state.CanChange || state.IsEnabled != enabled || enabled && !state.UsesCurrentExecutable)
            throw new IOException("Windows did not retain the requested autostart registration.");
        return state;
    }

    internal static bool IsSupportedExecutable(string? path) =>
        !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path)
        && string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(Path.GetFileName(path), "dotnet.exe", StringComparison.OrdinalIgnoreCase)
        && !path.Contains('"') && !path.Contains('\r') && !path.Contains('\n');

    internal static string BuildCommand(string executablePath)
    {
        if (!IsSupportedExecutable(executablePath)) throw new ArgumentException("A full application executable path is required.", nameof(executablePath));
        // No restart/elevation arguments are registered. Normal sign-in never requests UAC.
        return "\"" + Path.GetFullPath(executablePath) + "\"";
    }

    private static bool IsPackaged()
    {
        if (!OperatingSystem.IsWindows()) return false;
        uint length = 0;
        return GetCurrentPackageFullName(ref length, IntPtr.Zero) != 15700; // APPMODEL_ERROR_NO_PACKAGE
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetCurrentPackageFullName(ref uint packageFullNameLength, IntPtr packageFullName);
}

internal interface IStartupRegistrationStore
{
    string? ReadCommand();
    void WriteCommand(string command);
    void DeleteCommand();
}

internal sealed class WindowsStartupRegistrationStore : IStartupRegistrationStore
{
    public string? ReadCommand()
    {
        using var key = Registry.CurrentUser.OpenSubKey(StartupService.RunKeyPath, writable: false);
        return key?.GetValue(StartupService.ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
    }

    public void WriteCommand(string command)
    {
        using var key = Registry.CurrentUser.CreateSubKey(StartupService.RunKeyPath, writable: true)
            ?? throw new IOException("The current user's startup registration key could not be opened.");
        key.SetValue(StartupService.ValueName, command, RegistryValueKind.String);
    }

    public void DeleteCommand()
    {
        using var key = Registry.CurrentUser.OpenSubKey(StartupService.RunKeyPath, writable: true);
        key?.DeleteValue(StartupService.ValueName, throwOnMissingValue: false);
    }
}
