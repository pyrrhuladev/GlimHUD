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
    private readonly IHotkeyRegistration _registration;
    private readonly HashSet<int> _registeredHotkeys = [];
    private bool _disposed;
    public List<int> FailedHotkeys { get; } = [];

    internal OverlayInterop(System.Windows.Window window, Action<int> hotkey, IHotkeyRegistration? registration = null,
        bool registerHotkeys = true)
    {
        _handle = new WindowInteropHelper(window).Handle;
        _source = HwndSource.FromHwnd(_handle)
            ?? throw new InvalidOperationException("The overlay has no native window source.");
        _hotkey = hotkey;
        _registration = registration ?? new WindowsHotkeyRegistration();
        _source.AddHook(WindowProcedure);
        if (!registerHotkeys) return;
        foreach (var binding in HotkeyBindings.All)
        {
            if (_registration.Register(_handle, binding)) _registeredHotkeys.Add(binding.Id);
            else FailedHotkeys.Add(binding.Id);
        }
    }

    internal void SetClickThrough(bool enabled)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var styles = GetWindowLongPtr(_handle, GwlExStyle).ToInt64();
        styles |= WsExLayered | WsExToolWindow | WsExNoActivate;
        styles = enabled ? styles | WsExTransparent : styles & ~WsExTransparent;
        SetWindowLongPtr(_handle, GwlExStyle, new IntPtr(styles));
        SetWindowPos(_handle, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010 | 0x0020);
    }

    private IntPtr WindowProcedure(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_disposed) return IntPtr.Zero;
        if (message == 0x0312)
        {
            var id = wParam.ToInt64();
            if (id is >= int.MinValue and <= int.MaxValue && _registeredHotkeys.Contains((int)id))
            {
                _hotkey((int)id);
                handled = true;
            }
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
        if (_disposed) return;
        _disposed = true;
        foreach (var id in _registeredHotkeys) _registration.Unregister(_handle, id);
        _registeredHotkeys.Clear();
        try { _source.RemoveHook(WindowProcedure); }
        catch (ObjectDisposedException) { }
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
}

internal interface IHotkeyRegistration
{
    bool Register(IntPtr window, HotkeyBinding binding);
    void Unregister(IntPtr window, int id);
}

internal sealed class WindowsHotkeyRegistration : IHotkeyRegistration
{
    public bool Register(IntPtr window, HotkeyBinding binding)
        => RegisterHotKey(window, binding.Id, binding.Modifiers, binding.VirtualKey);

    public void Unregister(IntPtr window, int id) => UnregisterHotKey(window, id);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
}
