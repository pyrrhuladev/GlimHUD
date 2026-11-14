using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace GlimHUD.Native;

/// <summary>A drag-scoped guide. It observes only its own overlay's move messages.</summary>
internal sealed class OverlayAlignmentGuide : IDisposable
{
    private readonly Window _owner;
    private readonly IntPtr _ownerHandle;
    private readonly HwndSource _source;
    private readonly AlignmentGuideWindow _guide;
    private readonly FrameworkElement? _alignmentSurface;
    private readonly PixelBounds _startBounds;
    private readonly PixelInsets _insets;
    private IntPtr _monitor;
    private PixelBounds _workArea;
    private double _scale = 1;
    private bool _disposed;
    private bool _hasMoved;
    private bool _cancelled;

    private OverlayAlignmentGuide(Window owner, FrameworkElement? alignmentSurface)
    {
        _owner = owner;
        _alignmentSurface = alignmentSurface;
        _ownerHandle = new WindowInteropHelper(owner).EnsureHandle();
        _source = HwndSource.FromHwnd(_ownerHandle)
            ?? throw new InvalidOperationException("The overlay has no native window source.");
        var accent = Application.Current?.TryFindResource("AccentBrush") is SolidColorBrush brush
            ? brush.Color : Color.FromRgb(101, 221, 242);
        _guide = new AlignmentGuideWindow(accent);
        if (GetWindowRect(_ownerHandle, out var rectangle))
        {
            _startBounds = rectangle;
            _insets = ReadInsets(rectangle);
        }
        _source.AddHook(WindowProcedure);
        _owner.Closed += OwnerClosed;
    }

    internal static OverlayAlignmentGuide? BeginDrag(Window owner, bool enabled, FrameworkElement? alignmentSurface = null)
    {
        if (!enabled) return null;
        return new OverlayAlignmentGuide(owner, alignmentSurface);
    }

    private IntPtr WindowProcedure(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_disposed) return IntPtr.Zero;
        if (message == 0x0216 && lParam != IntPtr.Zero) // WM_MOVING
        {
            var proposed = Marshal.PtrToStructure<PixelBounds>(lParam);
            _hasMoved |= proposed != _startBounds;
            // Observe the native drag without quantizing its RECT. Rewriting every
            // mouse movement fights the system's cursor offset and makes it sticky.
            UpdateGuide(AlignmentGridRules.VisibleBounds(proposed, _insets));
        }
        else if (message == 0x0232) // WM_EXITSIZEMOVE
        {
            _cancelled |= Keyboard.IsKeyDown(Key.Escape);
            _guide.Hide();
        }
        else if (message == 0x001F) _cancelled = true; // WM_CANCELMODE
        else if (message == 0x007E) _monitor = IntPtr.Zero; // WM_DISPLAYCHANGE
        return IntPtr.Zero;
    }

    /// <summary>Apply a small release-only magnet after the native drag has finished.</summary>
    internal void CompleteDrag()
    {
        if (_disposed || _cancelled || !_hasMoved || !_owner.IsVisible
            || (Keyboard.Modifiers & ModifierKeys.Shift) != 0 || Keyboard.IsKeyDown(Key.Escape)
            || !GetWindowRect(_ownerHandle, out var rectangle) || rectangle == _startBounds) return;
        UpdateGuide(AlignmentGridRules.VisibleBounds(rectangle, _insets), showGuide: false);
        var snapped = AlignmentGridRules.SnapWindow(rectangle, _insets, _workArea, _scale, clampToWorkArea: false);
        if (snapped == rectangle) return;
        SetWindowPos(_ownerHandle, IntPtr.Zero, snapped.Left, snapped.Top, 0, 0,
            0x0001 | 0x0004 | 0x0010); // NOSIZE | NOZORDER | NOACTIVATE
    }

    private void UpdateGuide(PixelBounds overlay, bool showGuide = true)
    {
        var monitor = MonitorFromRect(ref overlay, 2); // MONITOR_DEFAULTTONEAREST
        if (monitor == IntPtr.Zero) return;
        if (_monitor != monitor)
        {
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (!GetMonitorInfoW(monitor, ref info)) return;
            _monitor = monitor;
            _workArea = info.WorkArea;
            var dpi = GetDpiForWindow(_ownerHandle);
            _scale = dpi > 0 ? dpi / 96d : 1;
            // Showing a top-level surface before DragMove has entered its native
            // loop can disturb capture. The first real move displays it lazily.
            if (showGuide && _owner.IsVisible)
            {
                if (!_guide.IsVisible) _guide.Show();
                // Native pixels avoid ambiguous absolute WPF coordinates on mixed-DPI desktops.
                var handle = new WindowInteropHelper(_guide).Handle;
                SetWindowPos(handle, _ownerHandle, _workArea.Left, _workArea.Top,
                    _workArea.Width, _workArea.Height, 0x0010 | 0x0040); // NOACTIVATE | SHOWWINDOW
                dpi = GetDpiForWindow(handle);
                _scale = dpi > 0 ? dpi / 96d : 1;
            }
        }
        _guide.SetOverlayBounds(overlay, _workArea, _scale);
    }

    private void OwnerClosed(object? sender, EventArgs e) => Dispose();

    private PixelInsets ReadInsets(PixelBounds window)
    {
        if (_alignmentSurface is not { ActualWidth: > 0, ActualHeight: > 0 } surface) return default;
        try
        {
            var topLeft = surface.PointToScreen(new Point(0, 0));
            var bottomRight = surface.PointToScreen(new Point(surface.ActualWidth, surface.ActualHeight));
            var insets = new PixelInsets(Math.Max(0, (int)Math.Round(topLeft.X - window.Left)),
                Math.Max(0, (int)Math.Round(topLeft.Y - window.Top)),
                Math.Max(0, (int)Math.Round(window.Right - bottomRight.X)),
                Math.Max(0, (int)Math.Round(window.Bottom - bottomRight.Y)));
            return insets.Left + insets.Right < window.Width && insets.Top + insets.Bottom < window.Height ? insets : default;
        }
        catch (InvalidOperationException) { return default; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _owner.Closed -= OwnerClosed;
        try { _source.RemoveHook(WindowProcedure); }
        catch (ObjectDisposedException) { }
        _guide.Close();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public PixelBounds Monitor;
        public PixelBounds WorkArea;
        public uint Flags;
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr MonitorFromRect(ref PixelBounds rectangle, uint flags);
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool GetMonitorInfoW(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool GetWindowRect(IntPtr window, out PixelBounds rectangle);
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
}

[StructLayout(LayoutKind.Sequential)]
internal readonly record struct PixelBounds(int Left, int Top, int Right, int Bottom)
{
    internal int Width => Right - Left;
    internal int Height => Bottom - Top;
}

internal readonly record struct PixelInsets(int Left, int Top, int Right, int Bottom);

internal static class AlignmentGridRules
{
    internal const double CellSize = 20;

    internal static PixelBounds VisibleBounds(PixelBounds window, PixelInsets inset)
        => new(window.Left + inset.Left, window.Top + inset.Top, window.Right - inset.Right, window.Bottom - inset.Bottom);

    internal static PixelBounds SnapWindow(PixelBounds window, PixelInsets inset, PixelBounds workArea,
        double scale, bool clampToWorkArea)
    {
        var visible = VisibleBounds(window, inset);
        var snapped = Snap(visible, workArea, scale, clampToWorkArea);
        var dx = snapped.Left - visible.Left;
        var dy = snapped.Top - visible.Top;
        return new(window.Left + dx, window.Top + dy, window.Right + dx, window.Bottom + dy);
    }

    internal static PixelBounds Snap(PixelBounds bounds, PixelBounds workArea, double scale, bool clampToWorkArea)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0 || workArea.Width <= 0 || workArea.Height <= 0
            || !double.IsFinite(scale) || scale <= 0) return bounds;
        var cell = Math.Max(1, Math.Round(CellSize * scale));
        var magnet = Math.Max(1, 4 * scale);
        var left = SnapAxis(bounds.Left, bounds.Width, workArea.Left, workArea.Right, cell, magnet);
        var top = SnapAxis(bounds.Top, bounds.Height, workArea.Top, workArea.Bottom, cell, magnet);
        if (clampToWorkArea)
        {
            left = Math.Clamp(left, workArea.Left, Math.Max(workArea.Left, workArea.Right - bounds.Width));
            top = Math.Clamp(top, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - bounds.Height));
        }
        return new(left, top, left + bounds.Width, top + bounds.Height);
    }

    private static int SnapAxis(int position, int length, int origin, int end, double cell, double magnet)
    {
        var grid = origin + Math.Round((position - origin) / cell, MidpointRounding.AwayFromZero) * cell;
        var anchors = new[] { (double)origin, end - (double)length, (origin + (double)end - length) / 2, grid };
        var nearest = anchors.OrderBy(anchor => Math.Abs(position - anchor)).First();
        if (Math.Abs(position - nearest) <= magnet) return (int)Math.Round(nearest, MidpointRounding.AwayFromZero);
        return position;
    }
}

/// <summary>Separate top-level surface; never receives input or activates.</summary>
internal sealed class AlignmentGuideWindow : Window
{
    private readonly AlignmentGuideVisual _visual;
    private HwndSource? _source;

    internal AlignmentGuideWindow(Color accent)
    {
        Width = Height = 1;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowActivated = false;
        ShowInTaskbar = false;
        Topmost = true;
        Focusable = false;
        IsHitTestVisible = false;
        Content = _visual = new AlignmentGuideVisual(accent);
        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            var styles = GetWindowLongPtrW(handle, -20).ToInt64();
            SetWindowLongPtrW(handle, -20, new IntPtr(styles | 0x20 | 0x80 | 0x08000000));
            _source = HwndSource.FromHwnd(handle);
            _source?.AddHook(DoNotActivate);
        };
        Closed += (_, _) =>
        {
            try { _source?.RemoveHook(DoNotActivate); } catch (ObjectDisposedException) { }
            _source = null;
        };
    }

    internal void SetOverlayBounds(PixelBounds overlay, PixelBounds workArea, double scale)
    {
        _visual.DeviceScale = scale;
        _visual.OverlayBounds = new Rect((overlay.Left - workArea.Left) / scale, (overlay.Top - workArea.Top) / scale,
            overlay.Width / scale, overlay.Height / scale);
        _visual.InvalidateVisual();
    }

    private static IntPtr DoNotActivate(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0x0021) { handled = true; return new IntPtr(3); } // MA_NOACTIVATE
        if (message == 0x0084) { handled = true; return new IntPtr(-1); } // HTTRANSPARENT
        return IntPtr.Zero;
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr GetWindowLongPtrW(IntPtr window, int index);
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr SetWindowLongPtrW(IntPtr window, int index, IntPtr value);
}

internal sealed class AlignmentGuideVisual(Color accent) : FrameworkElement
{
    internal Rect OverlayBounds { get; set; }
    internal double DeviceScale { get; set; } = 1;

    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        var scale = double.IsFinite(DeviceScale) && DeviceScale > 0 ? DeviceScale : 1;
        var thickness = 1 / scale;
        var grid = new Pen(new SolidColorBrush(Color.FromArgb(38, accent.R, accent.G, accent.B)), thickness);
        var axis = new Pen(new SolidColorBrush(Color.FromArgb(115, accent.R, accent.G, accent.B)), thickness);
        var outline = new Pen(new SolidColorBrush(Color.FromArgb(190, accent.R, accent.G, accent.B)), thickness);
        // A single drawing surface avoids hundreds of layout elements while dragging.
        for (var x = 0d; x <= ActualWidth; x += AlignmentGridRules.CellSize)
            drawing.DrawLine(grid, new Point(x, 0), new Point(x, ActualHeight));
        for (var y = 0d; y <= ActualHeight; y += AlignmentGridRules.CellSize)
            drawing.DrawLine(grid, new Point(0, y), new Point(ActualWidth, y));
        drawing.DrawLine(axis, new Point(ActualWidth / 2, 0), new Point(ActualWidth / 2, ActualHeight));
        drawing.DrawLine(axis, new Point(0, ActualHeight / 2), new Point(ActualWidth, ActualHeight / 2));
        if (!OverlayBounds.IsEmpty && OverlayBounds.Width > 0 && OverlayBounds.Height > 0)
        {
            drawing.DrawRoundedRectangle(null, outline, OverlayBounds, OverlayBounds.Height / 2, OverlayBounds.Height / 2);
            drawing.DrawLine(axis, new Point(OverlayBounds.Left + OverlayBounds.Width / 2, 0),
                new Point(OverlayBounds.Left + OverlayBounds.Width / 2, ActualHeight));
            drawing.DrawLine(axis, new Point(0, OverlayBounds.Top + OverlayBounds.Height / 2),
                new Point(ActualWidth, OverlayBounds.Top + OverlayBounds.Height / 2));
        }
    }
}
