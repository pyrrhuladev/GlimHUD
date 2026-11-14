using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace GlimHUD.Controls;

/// <summary>
/// A separate, always click-through native surface for the outward halo. Keeping
/// its pixels out of the interactive overlay HWND lets unlocked controls coexist
/// with games in another process without intercepting input around the capsule.
/// The owner supplies bounds and animation; this window owns no timer.
/// </summary>
public sealed class ReactiveGlowWindow : Window
{
    private const int GwlExStyle = -20;
    private const long HaloWindowStyles = 0x00080000 | 0x00000020 | 0x00000080 | 0x08000000;
    private const uint SwpNoActivate = 0x0010;
    private static readonly IntPtr HwndTopmost = new(-1);
    private readonly Window _owner;
    private readonly Canvas _canvas;
    private readonly ScaleTransform _surfaceScale = new();
    private HwndSource? _source;
    private bool _closed;

    public ReactiveGlowSurface Surface { get; } = new();

    public ReactiveGlowWindow(Window owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        _owner = owner;
        Owner = owner;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.Manual;
        WindowStartupLocation = WindowStartupLocation.Manual;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Focusable = false;
        IsHitTestVisible = false;
        ClipToBounds = false;
        Width = Height = 1;
        Surface.LayoutTransform = _surfaceScale;
        _canvas = new Canvas { IsHitTestVisible = false, ClipToBounds = false };
        _canvas.Children.Add(Surface);
        Content = _canvas;
        SourceInitialized += InitializeNativeSurface;
    }

    /// <summary>
    /// Follow the panel's actual physical screen rectangle, including its current
    /// layout scale. Native positioning avoids absolute-DIP ambiguity between
    /// monitors with different DPI. A valid call shows the companion lazily.
    /// </summary>
    public void UpdateBounds(FrameworkElement panel, double scale)
    {
        ArgumentNullException.ThrowIfNull(panel);
        if (_closed) return;
        if (!_owner.IsVisible || !panel.IsVisible || panel.ActualWidth <= 0 || panel.ActualHeight <= 0
            || !double.IsFinite(scale) || scale <= 0)
        {
            Hide();
            return;
        }

        Point topLeft;
        Point bottomRight;
        try
        {
            topLeft = panel.PointToScreen(new Point());
            bottomRight = panel.PointToScreen(new Point(panel.ActualWidth, panel.ActualHeight));
        }
        catch (InvalidOperationException)
        {
            Hide();
            return;
        }
        if (!double.IsFinite(topLeft.X) || !double.IsFinite(topLeft.Y)
            || !double.IsFinite(bottomRight.X) || !double.IsFinite(bottomRight.Y)
            || bottomRight.X <= topLeft.X || bottomRight.Y <= topLeft.Y) return;

        // Padding is measured in the panel's physical pixels, rather than the
        // companion's previous monitor DPI. The halo needs 23 local DIPs.
        var panelDpi = VisualTreeHelper.GetDpi(panel);
        var padX = Math.Ceiling(26 * scale * panelDpi.DpiScaleX);
        var padY = Math.Ceiling(26 * scale * panelDpi.DpiScaleY);
        var left = (int)Math.Floor(topLeft.X - padX);
        var top = (int)Math.Floor(topLeft.Y - padY);
        var right = (int)Math.Ceiling(bottomRight.X + padX);
        var bottom = (int)Math.Ceiling(bottomRight.Y + padY);
        var pixelWidth = right - left;
        var pixelHeight = bottom - top;
        if (pixelWidth <= 0 || pixelHeight <= 0) return;

        var handle = new WindowInteropHelper(this).EnsureHandle();
        // Move before Show so no frame appears at a default screen position.
        // The move also gives WPF the destination monitor's DPI before layout.
        SetWindowPos(handle, HwndTopmost, left, top, pixelWidth, pixelHeight, SwpNoActivate);
        var nativeDpi = GetDpiForWindow(handle);
        var dpi = nativeDpi > 0 ? nativeDpi / 96d : 1d;
        Width = pixelWidth / dpi;
        Height = pixelHeight / dpi;
        Surface.Width = panel.ActualWidth;
        Surface.Height = panel.ActualHeight;
        // Normally both factors equal scale. Deriving them from the measured
        // rectangle also covers a brief owner/companion DPI mismatch in a drag.
        _surfaceScale.ScaleX = (bottomRight.X - topLeft.X) / (panel.ActualWidth * dpi);
        _surfaceScale.ScaleY = (bottomRight.Y - topLeft.Y) / (panel.ActualHeight * dpi);
        Canvas.SetLeft(Surface, (topLeft.X - left) / dpi);
        Canvas.SetTop(Surface, (topLeft.Y - top) / dpi);
        if (!IsVisible) Show();
        // Width/Height and Show may apply WPF's own native bounds; keep the final
        // rectangle exact without activating or changing the owner position.
        SetWindowPos(handle, HwndTopmost, left, top, pixelWidth, pixelHeight, SwpNoActivate);
    }

    private void InitializeNativeSurface(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        var styles = GetWindowLongPtr(handle, GwlExStyle).ToInt64();
        SetWindowLongPtr(handle, GwlExStyle, new IntPtr(styles | HaloWindowStyles));
        _source = HwndSource.FromHwnd(handle);
        _source?.AddHook(WindowProcedure);
    }

    private static IntPtr WindowProcedure(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0x0021) // WM_MOUSEACTIVATE; do not take focus even through synthetic messages.
        {
            handled = true;
            return new IntPtr(3); // MA_NOACTIVATE
        }
        return IntPtr.Zero;
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        if (_source is not null)
        {
            try { _source.RemoveHook(WindowProcedure); }
            catch (ObjectDisposedException) { }
            _source = null;
        }
        base.OnClosed(e);
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
}
