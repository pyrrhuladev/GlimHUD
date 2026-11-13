using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace GlimHUD.Native;

internal sealed class OverlayInterop : IDisposable
{
    private const int GwlExStyle = -20;
    private const long WsExLayered = 0x00080000;
    private const long WsExTransparent = 0x00000020;
    private const long WsExToolWindow = 0x00000080;
    private const long WsExNoActivate = 0x08000000;
    private readonly IntPtr _handle;
    private readonly HwndSource _source;
    private readonly Action<int> _hotkey;
    public List<int> FailedHotkeys { get; } = [];

    internal OverlayInterop(System.Windows.Window window, Action<int> hotkey)
    {
        _handle = new WindowInteropHelper(window).Handle;
        _source = HwndSource.FromHwnd(_handle);
        _hotkey = hotkey;
        _source.AddHook(WindowProcedure);
        Register(1, 0x47); // Ctrl+Alt+G
        Register(2, 0x48); // Ctrl+Alt+H
        Register(3, 0x53); // Ctrl+Alt+S
    }

    private void Register(int id, uint virtualKey)
    {
        if (!RegisterHotKey(_handle, id, 0x0001 | 0x0002 | 0x4000, virtualKey)) FailedHotkeys.Add(id);
    }

    internal void SetClickThrough(bool enabled)
    {
        var styles = GetWindowLongPtr(_handle, GwlExStyle).ToInt64();
        styles |= WsExLayered | WsExToolWindow | WsExNoActivate;
        styles = enabled ? styles | WsExTransparent : styles & ~WsExTransparent;
        SetWindowLongPtr(_handle, GwlExStyle, new IntPtr(styles));
        SetWindowPos(_handle, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010 | 0x0020);
    }

    private IntPtr WindowProcedure(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0x0312)
        {
            _hotkey(wParam.ToInt32());
            handled = true;
        }
        if (message == 0x0021) // WM_MOUSEACTIVATE: buttons must not take keyboard focus from the game.
        {
            handled = true;
            return new IntPtr(3); // MA_NOACTIVATE
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        for (var i = 1; i <= 3; i++) UnregisterHotKey(_handle, i);
        _source.RemoveHook(WindowProcedure);
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
}
