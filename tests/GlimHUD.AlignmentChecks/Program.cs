using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GlimHUD.Native;

internal static class Program
{
    private static int _passed;

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            var work = new PixelBounds(0, 0, 1920, 1040);
            Check(AlignmentGridRules.Snap(new(31, 51, 231, 129), work, 1, false) == new PixelBounds(31, 51, 231, 129),
                "A release far from guides keeps the exact requested position.");
            Check(AlignmentGridRules.Snap(new(37, 53, 237, 131), work, 1, false) == new PixelBounds(40, 53, 240, 131),
                "Only an axis close to a guide receives a small release adjustment.");
            Check(AlignmentGridRules.Snap(new(35, 55, 235, 133), work, 1, false) == new PixelBounds(35, 55, 235, 133),
                "A five-pixel distance does not force the panel onto the grid.");
            Check(AlignmentGridRules.Snap(new(36, 56, 236, 134), work, 1, false) == new PixelBounds(40, 60, 240, 138),
                "The four-DIP release magnet preserves the panel dimensions.");
            Check(AlignmentGridRules.Snap(new(-1878, 43, -1678, 121), new(-1920, 0, 0, 1040), 1, false) == new PixelBounds(-1880, 40, -1680, 118),
                "Negative monitor coordinates use the destination monitor's guide origin.");
            Check(AlignmentGridRules.Snap(new(41, 79, 241, 157), work, 1.5, false) == new PixelBounds(41, 79, 241, 157),
                "High-DPI release still allows positions between grid lines.");
            Check(AlignmentGridRules.Snap(new(54, 84, 254, 162), work, 1.5, false) == new PixelBounds(60, 90, 260, 168),
                "150 percent DPI scales the cell and release magnet together.");
            Check(AlignmentGridRules.Snap(new(861, 483, 1061, 561), work, 1, false) == new PixelBounds(860, 481, 1060, 559),
                "The closest center guide can align both axes precisely.");
            Check(AlignmentGridRules.Snap(new(311, 478, 511, 556), work, 1, false).Top == 480,
                "A closer grid line wins over a more distant center guide.");
            Check(AlignmentGridRules.Snap(new(1718, 964, 1919, 1041), work, 1, false) == new PixelBounds(1719, 963, 1920, 1040),
                "Nearby right and bottom edges remain optional release targets.");
            var across = AlignmentGridRules.Snap(new(1909, 1019, 2109, 1097), work, 1, false);
            Check(across.Right > work.Right && across.Bottom > work.Bottom,
                "A release can straddle monitor boundaries without being pulled back.");
            Check(AlignmentGridRules.Snap(new(-13, 91, 187, 169), work, 1, false) == new PixelBounds(-13, 91, 187, 169),
                "Partially outside placement is not forcibly clamped by the release magnet.");
            var final = AlignmentGridRules.Snap(across, work, 1, true);
            Check(final.Right <= work.Right && final.Bottom <= work.Bottom,
                "Explicit geometry fitting remains available only when requested.");
            Check(AlignmentGridRules.Snap(new(20, 20, 2220, 98), work, 1, true).Left == 0,
                "Oversized overlays remain anchored visibly.");
            Check(AlignmentGridRules.Snap(new(10, 10, 210, 88), work, double.NaN, true) == new PixelBounds(10, 10, 210, 88),
                "Invalid DPI does not corrupt a rectangle.");
            var visualSnap = AlignmentGridRules.SnapWindow(new(32, 48, 242, 136), new(5, 5, 5, 5), work, 1, false);
            Check(visualSnap == new PixelBounds(35, 48, 245, 136),
                "A nearby visible capsule edge snaps without pulling its other axis across a gap.");
            var visualFit = AlignmentGridRules.SnapWindow(new(-7, -7, 203, 81), new(5, 5, 5, 5), work, 1, true);
            Check(visualFit.Left == -5 && visualFit.Top == -5,
                "Final fitting includes the visible surface while allowing only transparent padding outside.");

            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var guide = new AlignmentGuideWindow(Color.FromRgb(101, 221, 242));
            var handle = new WindowInteropHelper(guide).EnsureHandle();
            var styles = GetWindowLongPtrW(handle, -20).ToInt64();
            Check((styles & 0x20) != 0 && (styles & 0x80) != 0 && (styles & 0x08000000) != 0,
                "Hidden guide HWND is click-through, a tool window, and cannot activate.");
            Check(!guide.IsVisible && !guide.ShowActivated && !guide.ShowInTaskbar && !guide.IsHitTestVisible,
                "Style smoke does not display or focus a desktop grid.");
            guide.Close();
            CheckNativeMovementIsObservationOnly(app);

            if (args.Length > 0)
            {
                var output = Path.GetFullPath(args[0]);
                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                var visual = new AlignmentGuideVisual(Color.FromRgb(101, 221, 242))
                    { OverlayBounds = new Rect(260, 140, 440, 78) };
                visual.Measure(new Size(960, 540));
                visual.Arrange(new Rect(0, 0, 960, 540));
                visual.UpdateLayout();
                var bitmap = new RenderTargetBitmap(960, 540, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(visual);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var file = File.Create(output);
                encoder.Save(file);
                Console.WriteLine("Guide render: " + output);
            }
            app.Shutdown();
            Console.WriteLine($"{_passed} alignment checks passed.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException(name);
        _passed++;
    }

    private static void CheckNativeMovementIsObservationOnly(Application app)
    {
        var owner = new Window
        {
            Width = 300, Height = 80, Left = -5000, Top = -5000,
            WindowStyle = WindowStyle.None, ShowActivated = false, ShowInTaskbar = false
        };
        try
        {
            var handle = new WindowInteropHelper(owner).EnsureHandle();
            Check(OverlayAlignmentGuide.BeginDrag(owner, false) is null,
                "Disabled alignment creates no guide window or drag hook.");
            Check(GetWindowRect(handle, out var startingBounds), "The hidden fixture has native bounds.");
            var startingCapture = GetCapture();
            var startingForeground = GetForegroundWindow();
            var originalWindowCount = app.Windows.Count;
            using (var alignment = OverlayAlignmentGuide.BeginDrag(owner, true)
                ?? throw new InvalidOperationException("The enabled guide scope was not created."))
            {
                Check(app.Windows.Cast<Window>().All(window => !window.IsVisible),
                    "Beginning a drag does not display a desktop guide before native movement begins.");
                var proposed = new PixelBounds(37, 53, 337, 133);
                var movingRect = Marshal.AllocHGlobal(Marshal.SizeOf<PixelBounds>());
                try
                {
                    Marshal.StructureToPtr(proposed, movingRect, false);
                    SendMessageW(handle, 0x0216, IntPtr.Zero, movingRect); // WM_MOVING, own hidden HWND only.
                    Check(Marshal.PtrToStructure<PixelBounds>(movingRect) == proposed,
                        "WM_MOVING leaves the native requested rectangle byte-for-byte unchanged.");
                    Check(GetWindowRect(handle, out var actualBounds) && actualBounds == startingBounds,
                        "Observing a native move notification does not move the window itself.");
                    Check(app.Windows.Cast<Window>().All(window => !window.IsVisible),
                        "Synthetic movement of a hidden fixture never displays a desktop grid.");
                    Check(GetCapture() == startingCapture && GetForegroundWindow() == startingForeground,
                        "The guide does not change native mouse capture or foreground focus.");
                    SendMessageW(handle, 0x0232, IntPtr.Zero, IntPtr.Zero); // WM_EXITSIZEMOVE.
                    alignment.CompleteDrag();
                    Check(GetWindowRect(handle, out actualBounds) && actualBounds == startingBounds,
                        "Completing a hidden or unchanged drag preserves its original position.");
                }
                finally { Marshal.FreeHGlobal(movingRect); }
            }
            Check(app.Windows.Count == originalWindowCount && app.Windows.Cast<Window>().All(window => !window.IsVisible),
                "Disposing the drag scope closes its guide without showing or leaving an extra window.");
            SendMessageW(handle, 0x0232, IntPtr.Zero, IntPtr.Zero);
            Check(GetWindowRect(handle, out var afterDispose) && afterDispose == startingBounds,
                "Move notifications remain harmless after scope cleanup.");
        }
        finally { owner.Close(); }
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr GetWindowLongPtrW(IntPtr window, int index);
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr SendMessageW(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool GetWindowRect(IntPtr window, out PixelBounds bounds);
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr GetCapture();
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr GetForegroundWindow();
}
