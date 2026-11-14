using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace GlimHUD.Services;

internal sealed record DesktopProcessIdentity(int ProcessId, int SessionId, string ExecutablePath, string UserSid, bool IsElevated, int IntegrityLevel = 0x2000);

internal interface IUnelevatedProcessLauncher
{
    bool IsAvailable();
    Task<bool> TryLaunchAsync(string executablePath, string arguments, string workingDirectory);
}

/// <summary>
/// Asks the existing, verified ordinary-user Explorer desktop to launch an executable.
/// Never falls back to launching with our elevated token or changes Windows policy.
/// The caller is responsible for authenticating the installer and supplying fixed arguments.
/// </summary>
internal sealed class UnelevatedProcessLauncher : IUnelevatedProcessLauncher
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;
    private const int TokenUser = 1;
    private const int TokenElevation = 20;
    private static readonly Guid TopLevelBrowser = new("4C96BE40-915C-11CF-99D3-00AA004AE837");
    private static readonly Guid DispatchId = new("00020400-0000-0000-C000-000000000046");

    internal static bool IsTrustedDesktop(DesktopProcessIdentity desktop, int currentSession, string currentSid, string explorerPath)
    {
        try
        {
            return desktop is not null && desktop.ProcessId > 0 && desktop.SessionId >= 0 && currentSession >= 0
                && desktop.SessionId == currentSession && !desktop.IsElevated
                && desktop.IntegrityLevel >= 0x2000 && desktop.IntegrityLevel < 0x3000
                && !string.IsNullOrWhiteSpace(desktop.UserSid) && !string.IsNullOrWhiteSpace(currentSid)
                && string.Equals(desktop.UserSid, currentSid, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(desktop.ExecutablePath) && !string.IsNullOrWhiteSpace(explorerPath)
                && Path.IsPathFullyQualified(desktop.ExecutablePath) && Path.IsPathFullyQualified(explorerPath)
                && string.Equals(Path.GetFullPath(desktop.ExecutablePath), Path.GetFullPath(explorerPath), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception error) when (IsRecoverable(error)) { return false; }
    }

    public bool IsAvailable() => TryGetDesktop(out _);

    public Task<bool> TryLaunchAsync(string executablePath, string arguments, string workingDirectory)
    {
        if (!OperatingSystem.IsWindows() || !ValidArguments(executablePath, arguments, workingDirectory))
            return Task.FromResult(false);
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var thread = new Thread(() =>
            {
                try { completion.TrySetResult(LaunchFromDesktop(executablePath, arguments, workingDirectory)); }
                catch (Exception error) when (IsRecoverable(error)) { completion.TrySetResult(false); }
            }) { IsBackground = true, Name = "GlimHUD ordinary-user installer launch" };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }
        catch (Exception error) when (IsRecoverable(error)) { completion.TrySetResult(false); }
        return completion.Task;
    }

    private static bool ValidArguments(string executable, string arguments, string directory)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(executable) && !string.IsNullOrWhiteSpace(directory)
                && arguments is not null && arguments.Length < 32768 && !arguments.Contains('\0')
                && !executable.Contains('\0') && !directory.Contains('\0')
                && Path.IsPathFullyQualified(executable) && Path.IsPathFullyQualified(directory)
                && string.Equals(Path.GetExtension(executable), ".exe", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception error) when (IsRecoverable(error)) { return false; }
    }

    private static bool TryGetDesktop(out Desktop desktop)
    {
        desktop = default;
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            var window = GetShellWindow();
            if (window == IntPtr.Zero || GetWindowThreadProcessId(window, out var processId) == 0 || processId == 0 || processId > int.MaxValue
                || !ProcessIdToSessionId(processId, out var shellSession)
                || !ProcessIdToSessionId((uint)Environment.ProcessId, out var currentSession) || shellSession > int.MaxValue || currentSession > int.MaxValue)
                return false;
            using var process = OpenProcess(ProcessQueryLimitedInformation, false, processId);
            if (process.IsInvalid) return false;
            var image = new StringBuilder(32768);
            var size = image.Capacity;
            var expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
            if (!QueryFullProcessImageName(process, 0, image, ref size)) return false;
            if (!OpenProcessToken(process.DangerousGetHandle(), TokenQuery, out var shellToken)) return false;
            using (shellToken)
            {
                if (!GetTokenElevation(shellToken, TokenElevation, out var elevated, sizeof(int), out var returned)
                    || returned < sizeof(int)) return false;
                if (!OpenProcessToken(GetCurrentProcess(), TokenQuery, out var currentToken)) return false;
                using (currentToken)
                {
                    var shellSid = ReadUserSid(shellToken);
                    var integrity = ReadIntegrityLevel(shellToken);
                    var currentSid = ReadUserSid(currentToken);
                    if (shellSid is null || currentSid is null || integrity is null || !IsTrustedDesktop(
                        new((int)processId, (int)shellSession, image.ToString(), shellSid.Value, elevated != 0, integrity.Value),
                        (int)currentSession, currentSid.Value, expected)) return false;
                }
            }
            // Recheck ownership after the process/token reads to reject a desktop replacement.
            if (GetShellWindow() != window || GetWindowThreadProcessId(window, out var owner) == 0 || owner != processId) return false;
            desktop = new(window, processId);
            return true;
        }
        catch (Exception error) when (IsRecoverable(error)) { return false; }
    }

    private static SecurityIdentifier? ReadUserSid(SafeAccessTokenHandle token)
    {
        GetTokenInformation(token, TokenUser, IntPtr.Zero, 0, out var required);
        if (required <= 0 || required > 65536) return null;
        var buffer = Marshal.AllocHGlobal(required);
        try
        {
            if (!GetTokenInformation(token, TokenUser, buffer, required, out _)) return null;
            var sid = Marshal.ReadIntPtr(buffer);
            return sid == IntPtr.Zero ? null : new SecurityIdentifier(sid);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static int? ReadIntegrityLevel(SafeAccessTokenHandle token)
    {
        GetTokenInformation(token, 25, IntPtr.Zero, 0, out var required); // TokenIntegrityLevel
        if (required <= 0 || required > 65536) return null;
        var buffer = Marshal.AllocHGlobal(required);
        try
        {
            if (!GetTokenInformation(token, 25, buffer, required, out _)) return null;
            var sid = Marshal.ReadIntPtr(buffer); // TOKEN_MANDATORY_LABEL.Label.Sid
            if (sid == IntPtr.Zero) return null;
            var countPointer = GetSidSubAuthorityCount(sid);
            if (countPointer == IntPtr.Zero) return null;
            var count = Marshal.ReadByte(countPointer);
            if (count is 0 or > 15) return null;
            var levelPointer = GetSidSubAuthority(sid, (uint)(count - 1));
            return levelPointer == IntPtr.Zero ? null : Marshal.ReadInt32(levelPointer);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static bool LaunchFromDesktop(string executable, string arguments, string directory)
    {
        if (!TryGetDesktop(out var expected)) return false;
        // Official Microsoft desktop-broker chain; use the existing desktop, not
        // a new Shell.Application instance created with this process's token.
        // https://github.com/microsoft/nodejstools/blob/main/Nodejs/Product/Nodejs/SharedProject/SystemUtilities.cs
        object? windows = null, desktop = null, browser = null, view = null, folder = null, application = null;
        try
        {
            windows = new ShellWindowsClass();
            object location = 0; // CSIDL_DESKTOP
            object root = null!; // VT_EMPTY
            desktop = ((IShellWindows)windows).FindWindowSW(ref location, ref root, 8, out var desktopWindow, 1);
            if (desktop is null || desktopWindow == 0) return false;
            var found = new IntPtr(desktopWindow);
            if (GetWindowThreadProcessId(found, out var processId) == 0 || processId != expected.ProcessId) return false;
            var service = TopLevelBrowser;
            var browserId = typeof(IShellBrowser).GUID;
            browser = ((IComServiceProvider)desktop).QueryService(ref service, ref browserId);
            if (browser is not IShellBrowser shellBrowser) return false;
            view = shellBrowser.QueryActiveShellView();
            if (view is not IShellView shellView) return false;
            var dispatchId = DispatchId;
            folder = shellView.GetItemObject(0, ref dispatchId); // SVGIO_BACKGROUND
            if (folder is not IShellFolderViewDual folderView) return false;
            application = folderView.Application;
            if (application is not IShellDispatch2 shellDispatch) return false;
            if (!TryGetDesktop(out var current) || current != expected) return false;
            shellDispatch.ShellExecute(executable, arguments, directory, "open", 1);
            return true;
        }
        finally
        {
            // Objects never escape this dedicated STA. Avoid releasing aliased RCWs twice.
            var released = new HashSet<object>(ReferenceEqualityComparer.Instance);
            foreach (var item in new[] { application, folder, view, browser, desktop, windows })
            {
                if (item is not null && released.Add(item) && Marshal.IsComObject(item))
                {
                    try { Marshal.FinalReleaseComObject(item); }
                    catch (Exception error) when (IsRecoverable(error)) { }
                }
            }
        }
    }

    private static bool IsRecoverable(Exception error) => error is COMException or InvalidComObjectException or InvalidCastException
        or InvalidOperationException or System.ComponentModel.Win32Exception or ArgumentException
        or IOException or UnauthorizedAccessException or System.Security.SecurityException
        or NotSupportedException or ThreadStateException;
    private readonly record struct Desktop(IntPtr Window, uint ProcessId);

    [ComImport, Guid("9BA05972-F6A8-11CF-A442-00A0C90A8F39"), ClassInterface(ClassInterfaceType.None)]
    private class ShellWindowsClass { }
    [ComImport, Guid("85CB6900-4D95-11CF-960C-0080C7F4EE85"), InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
    private interface IShellWindows
    {
        [return: MarshalAs(UnmanagedType.IDispatch)]
        object FindWindowSW([MarshalAs(UnmanagedType.Struct)] ref object location,
            [MarshalAs(UnmanagedType.Struct)] ref object root, int windowClass, out int window, int options);
    }
    [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IComServiceProvider
    {
        [return: MarshalAs(UnmanagedType.Interface)] object QueryService(ref Guid service, ref Guid requestedInterface);
    }
    [ComImport, Guid("000214E2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellBrowser
    {
        void Gap01(); void Gap02(); void Gap03(); void Gap04(); void Gap05(); void Gap06();
        void Gap07(); void Gap08(); void Gap09(); void Gap10(); void Gap11(); void Gap12();
        [return: MarshalAs(UnmanagedType.Interface)] IShellView QueryActiveShellView();
    }
    [ComImport, Guid("000214E3-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellView
    {
        void Gap01(); void Gap02(); void Gap03(); void Gap04(); void Gap05(); void Gap06();
        void Gap07(); void Gap08(); void Gap09(); void Gap10(); void Gap11(); void Gap12();
        [return: MarshalAs(UnmanagedType.Interface)] object GetItemObject(uint aspect, ref Guid requestedInterface);
    }
    [ComImport, Guid("E7A1AF80-4D96-11CF-960C-0080C7F4EE85"), InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
    private interface IShellFolderViewDual
    {
        object Application { [return: MarshalAs(UnmanagedType.IDispatch)] get; }
    }
    [ComImport, Guid("A4C6892C-3BA9-11D2-9DEA-00C04FB16162"), InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
    private interface IShellDispatch2
    {
        void ShellExecute([MarshalAs(UnmanagedType.BStr)] string executable,
            [MarshalAs(UnmanagedType.Struct)] object arguments, [MarshalAs(UnmanagedType.Struct)] object directory,
            [MarshalAs(UnmanagedType.Struct)] object operation, [MarshalAs(UnmanagedType.Struct)] object show);
    }

    [DllImport("user32.dll")] private static extern IntPtr GetShellWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ProcessIdToSessionId(uint processId, out uint sessionId);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "QueryFullProcessImageNameW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder image, ref int size);
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", ExactSpelling = true)]
    private static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);
    [DllImport("advapi32.dll", ExactSpelling = true)]
    private static extern IntPtr GetSidSubAuthority(IntPtr sid, uint subAuthority);
    [DllImport("advapi32.dll", EntryPoint = "GetTokenInformation", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenElevation(SafeAccessTokenHandle token, int informationClass, out int elevation, int size, out int returned);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int informationClass, IntPtr buffer, int size, out int returned);
}




