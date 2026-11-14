using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;

namespace GlimHUD.Native;

/// <summary>
/// Keeps the right-click popup above the non-activating HUD and its decorative
/// companion. WPF may leave this popup unowned when the HUD is not foreground.
/// The caller must also preserve HUD/halo z-order while <see cref="IsOpen"/>.
/// This helper never activates a window, changes popup input styles or polls.
/// </summary>
internal sealed class OverlayMenuInterop : IDisposable
{
    private const uint RaiseFlags = 0x0001 | 0x0002 | 0x0010 | 0x0200;
    private static readonly IntPtr HwndTopmost = new(-1);
    private readonly ContextMenu _menu;
    private DispatcherOperation? _pendingRaise;
    private bool _disposed;

    internal OverlayMenuInterop(ContextMenu menu)
    {
        ArgumentNullException.ThrowIfNull(menu);
        _menu = menu;
        _menu.Opened += MenuOpened;
        _menu.Closed += MenuClosed;
        if (_menu.IsOpen) MenuOpened(_menu, new RoutedEventArgs());
    }

    internal bool IsOpen => !_disposed && _menu.IsOpen;

    /// <summary>
    /// Raises only the popup HWND, preserving its bounds, owner ordering, focus,
    /// and input behavior. Safe before opening, after closing or after disposal.
    /// </summary>
    internal bool BringToFront()
    {
        if (!IsOpen || PresentationSource.FromVisual(_menu) is not HwndSource { Handle: var handle }
            || handle == IntPtr.Zero) return false;
        return BringNativeMenuToFront(handle);
    }

    internal static bool BringNativeMenuToFront(IntPtr handle)
        => handle != IntPtr.Zero && SetWindowPos(handle, HwndTopmost, 0, 0, 0, 0, RaiseFlags);

    private void MenuOpened(object sender, RoutedEventArgs e)
    {
        if (_disposed) return;
        BringToFront();
        _pendingRaise?.Abort();
        // WPF may finish arranging/showing the native popup after Opened.
        // This one deferred raise is guarded so a closed menu is never revived.
        _pendingRaise = _menu.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            _pendingRaise = null;
            BringToFront();
        }));
    }

    private void MenuClosed(object sender, RoutedEventArgs e)
    {
        _pendingRaise?.Abort();
        _pendingRaise = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _pendingRaise?.Abort();
        _pendingRaise = null;
        _menu.Opened -= MenuOpened;
        _menu.Closed -= MenuClosed;
        if (_menu.IsOpen) _menu.IsOpen = false;
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter,
        int x, int y, int width, int height, uint flags);
}
