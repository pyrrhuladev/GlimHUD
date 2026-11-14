using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using GlimHUD.Native;

internal static class Program
{
    private static int _passed;

    [STAThread]
    private static int Main()
    {
        try
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            CheckMenuOrdering();
            app.Shutdown();
            Console.WriteLine($"{_passed} menu checks passed; no user input or media commands were sent.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static void CheckMenuOrdering()
    {
        var panel = new Border { Background = Brushes.Black, Width = 320, Height = 64 };
        var left = SystemParameters.VirtualScreenLeft - 1400;
        var top = SystemParameters.VirtualScreenTop - 900;
        var hud = CreateFixture(left, top, panel);
        _ = new WindowInteropHelper(hud).EnsureHandle();
        var halo = CreateFixture(left - 24, top - 24, new Border { Background = Brushes.Blue });
        halo.Width = 368; halo.Height = 112;
        halo.Owner = hud;
        var menu = new ContextMenu
        {
            PlacementTarget = panel, Placement = PlacementMode.AbsolutePoint,
            HorizontalOffset = left, VerticalOffset = top, StaysOpen = true,
            // WPF can clamp popup placement to a screen; keep the fixture invisible.
            Opacity = 0, Focusable = false
        };
        menu.Items.Add(new MenuItem { Header = "Settings" });
        menu.Items.Add(new MenuItem { Header = "Lock overlay" });
        menu.Items.Add(new MenuItem { Header = "Hide" });
        var helper = new OverlayMenuInterop(menu);
        try
        {
            var foreground = GetForegroundWindow();
            var capture = GetCapture();
            Check(!helper.IsOpen && !helper.BringToFront()
                && PresentationSource.FromVisual(menu) is null,
                "A closed menu does not create or raise a native popup.");
            Check(GetForegroundWindow() == foreground && GetCapture() == capture,
                "Closed-menu handling does not activate a window or capture input.");

            hud.Show(); halo.Show(); hud.UpdateLayout(); halo.UpdateLayout();
            var hudHandle = new WindowInteropHelper(hud).Handle;
            var haloHandle = new WindowInteropHelper(halo).Handle;
            var hudBounds = Bounds(hudHandle);
            var haloBounds = Bounds(haloHandle);
            menu.IsOpen = true;
            DrainLoaded();
            Check(helper.IsOpen && menu.IsVisible,
                "Opening the context menu updates the ordering guard.");
            var menuHandle = (PresentationSource.FromVisual(menu) as HwndSource)?.Handle ?? IntPtr.Zero;
            Check(menuHandle != IntPtr.Zero && IsWindowVisible(menuHandle),
                "The helper targets the actual visible WPF popup HWND.");
            Check((GetWindowLongPtr(menuHandle, -20).ToInt64() & 0x8) != 0
                && Above(menuHandle, hudHandle) && Above(menuHandle, haloHandle),
                "The popup is topmost and above both HUD and glow HWNDs.");
            var menuBounds = Bounds(menuHandle);
            var menuStyle = GetWindowLongPtr(menuHandle, -20);
            var nativeForeground = GetForegroundWindow();
            var nativeCapture = GetCapture();
            Check(helper.BringToFront() && Bounds(menuHandle) == menuBounds
                && Bounds(hudHandle) == hudBounds && Bounds(haloHandle) == haloBounds,
                "Raising the popup preserves every native window rectangle.");
            Check(GetWindowLongPtr(menuHandle, -20) == menuStyle
                && GetForegroundWindow() == nativeForeground && GetCapture() == nativeCapture,
                "Raising does not alter menu input styles, foreground activation or capture.");

            SetWindowPos(haloHandle, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);
            Check(Above(haloHandle, menuHandle),
                "The fixture reproduces a late glow raise covering the context menu.");
            Check(helper.BringToFront() && Above(menuHandle, haloHandle),
                "An explicit raise restores menu ordering after a late companion show.");
            SetWindowPos(haloHandle, IntPtr.Zero, haloBounds.Left + 7, haloBounds.Top + 3,
                haloBounds.Right - haloBounds.Left, haloBounds.Bottom - haloBounds.Top,
                0x0004 | 0x0010 | 0x0200);
            Check(Above(menuHandle, haloHandle),
                "Preserving halo z-order during movement keeps every menu item above the HUD.");

            menu.IsOpen = false;
            Check(!helper.IsOpen && !helper.BringToFront(),
                "A closed popup is no longer raised.");
            menu.IsOpen = true;
            menu.IsOpen = false;
            DrainLoaded();
            Check(!helper.IsOpen && !menu.IsOpen && !helper.BringToFront(),
                "Deferred opening work cannot revive a menu closed before dispatch.");
            menu.IsOpen = true;
            DrainLoaded();
            Check(helper.IsOpen && helper.BringToFront(),
                "The helper supports reopening the same context menu.");
            helper.Dispose();
            DrainLoaded();
            Check(!menu.IsOpen && !helper.IsOpen && !helper.BringToFront(),
                "Disposal closes the popup and stops all deferred native work.");
            helper.Dispose();
            Check(!menu.IsOpen, "Menu interop disposal is idempotent.");
        }
        finally
        {
            helper.Dispose();
            halo.Close(); hud.Close();
        }
    }

    private static Window CreateFixture(double left, double top, FrameworkElement content)
        => new()
        {
            WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent,
            ShowActivated = false, ShowInTaskbar = false, Topmost = true, ResizeMode = ResizeMode.NoResize,
            Left = left, Top = top, Width = 320, Height = 64, Content = content
        };

    private static void DrainLoaded()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static bool Above(IntPtr first, IntPtr second)
    {
        for (var next = GetWindow(first, 2); next != IntPtr.Zero; next = GetWindow(next, 2))
            if (next == second) return true;
        return false;
    }

    private static WindowRect Bounds(IntPtr handle)
    {
        if (!GetWindowRect(handle, out var rectangle)) throw new InvalidOperationException("Missing fixture HWND bounds.");
        return rectangle;
    }

    private static void Check(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException(description);
        _passed++;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct WindowRect(int Left, int Top, int Right, int Bottom);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr GetWindow(IntPtr window, uint command);
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool GetWindowRect(IntPtr window, out WindowRect rectangle);
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter,
        int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr GetCapture();
}
