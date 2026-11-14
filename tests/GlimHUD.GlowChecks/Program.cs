using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GlimHUD.Controls;
using GlimHUD.Services;
using GlimHUD.Models;

internal static class Program
{
    private static int _passed;
    private static readonly TimeSpan Frame = TimeSpan.FromSeconds(1d / 30);

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length > 0 && args[0] == "--benchmark-render")
            {
                if (args.Length != 2) throw new ArgumentException("Usage: --benchmark-render <report.json>");
                return GlowRenderBenchmark.Run(args[1]);
            }
            var envelope = new GlowEnvelope();
            Check(envelope.Update(0, true, Frame) == 0, "Silence does not generate a synthetic beat.");
            var attack = envelope.Update(1, true, Frame);
            Check(attack > 0.8 && attack <= 1, "A musical transient receives a fast bounded attack.");
            var decay = envelope.Update(0, true, Frame);
            Check(decay > 0 && decay < attack, "The glow releases smoothly instead of flashing off.");
            for (var i = 0; i < 90; i++) decay = envelope.Update(0, false, Frame);
            Check(decay == 0, "A missing or silent source fades completely.");
            Check(envelope.Update(double.NaN, true, Frame) == 0
                && envelope.Update(double.PositiveInfinity, true, Frame) == 0,
                "Invalid metering cannot light the glow.");
            envelope.Reset();
            Check(envelope.Level == 0 && envelope.Update(1, true, TimeSpan.Zero) == 0
                && envelope.Update(1, true, TimeSpan.FromSeconds(-1)) == 0,
                "Reset and invalid elapsed time cannot manufacture a frame.");

            envelope.Update(1, true, Frame);
            var previous = envelope.Level;
            var monotonic = true;
            for (var i = 0; i < 60; i++)
            {
                var next = envelope.Update(1, true, Frame);
                monotonic &= next <= previous + 0.000001;
                previous = next;
            }
            Check(monotonic && Math.Abs(previous - 0.6) < 0.001,
                "A sustained tone settles without repeated invented pulses.");
            for (var i = 0; i < 12; i++) envelope.Update(0, true, Frame);
            Check(envelope.Update(0.8, true, Frame) > previous,
                "A new onset after a quiet interval receives a fresh accent.");
            Check(double.IsFinite(envelope.Update(20, true, TimeSpan.MaxValue)),
                "Out-of-range levels and long scheduling gaps remain finite.");

            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            CheckFrameAllocationBudget();
            var surface = new ReactiveGlowSurface();
            Check(!surface.IsHitTestVisible && !surface.Focusable && !surface.ClipToBounds,
                "The surface is decorative, non-focusable and permits its outward halo.");
            surface.Level = double.NaN;
            surface.Intensity = double.PositiveInfinity;
            Check(surface.Level == 0 && surface.Intensity == 0, "Invalid dependency values fail dark.");
            surface.Level = 2;
            surface.Intensity = -1;
            Check(surface.Level == 1 && surface.Intensity == 0, "Visual values are bounded.");

            var quiet = Render(0, 1);
            var soft = Render(0.2, 1);
            var medium = Render(0.6, 1);
            var loud = Render(1, 1);
            Check(AlphaSum(quiet) == 0, "Zero music emits no edge or halo.");
            Check(AlphaSum(soft) > 0 && AlphaSum(soft) < AlphaSum(medium)
                && AlphaSum(medium) < AlphaSum(loud), "Audio intensity visibly changes the rendered halo.");
            Check(AlphaSum(Render(1, 0)) == 0, "Zero intensity is fully disabled.");
            Check(Pixel(loud, 186, 58).A == 0, "The center remains transparent for sharp panel contents.");
            Check(Pixel(loud, 20, 58).A > 0 && Pixel(loud, 1, 58).A == 0,
                "The halo extends outside the capsule and stays inside its documented margin.");
            Check(HasFullSpectrum(loud), "The full rainbow is present around the perimeter at the same instant.");
            surface.Phase = 1.25;
            Check(surface.Phase == .25, "Animation phase wraps into the normalized range.");
            surface.Phase = double.NaN;
            Check(surface.Phase == 0, "Invalid animation phases return to the start safely.");
            Check(Difference(loud, Render(1, 1, phase: .4)) > .005,
                "Changing phase moves the colors around the entire perimeter.");
            Check(Difference(loud, Render(1, 1, phase: 1)) == 0
                && Difference(loud, Render(1, 1, phase: .99999)) < .002,
                "The RGB wave loops without a visible phase seam.");
            var inner = Render(1, 1, layer: GlowLayer.Inner);
            var both = Render(1, 1, layer: GlowLayer.Both);
            Check(Pixel(inner, 186, 58).A > 40, "The inner layer includes a visible, continuous center wash.");
            Check(Pixel(inner, 20, 58).A == 0 && Pixel(inner, 26, 26).A == 0,
                "The inner layer is clipped to the capsule and emits no outward pixels.");
            Check(Pixel(inner, 186, 29).A > Pixel(inner, 186, 58).A,
                "The interior color glow strengthens smoothly toward the perimeter.");
            Check(AlphaSum(both) > AlphaSum(loud) && Pixel(both, 186, 58).A > 0,
                "The diagnostic combined layer contains both interior wash and exterior emission.");
            Check(AlphaSum(Render(0, 1, layer: GlowLayer.Both)) == 0,
                "Zero level extinguishes the complete RGB effect, including the inner wash.");
            surface.Phase = -1e-20;
            Check(surface.Phase == 0, "Tiny negative phases wrap safely instead of indexing beyond the palette.");
            surface.EffectStyle = "unknown-future-style";
            Check(surface.EffectStyle == GlowStyles.RainbowWave, "Unknown effect identifiers fall back to Rainbow Wave.");
            surface.EffectStyle = " NEON-FLOW ";
            Check(surface.EffectStyle == GlowStyles.NeonFlow, "Effect identifiers use the shared normalized catalog.");
            surface.Pulse = 2;
            surface.PulsePosition = -.25;
            Check(surface.Pulse == 1 && surface.PulsePosition == .75, "Pulse strength is bounded and travel position wraps.");
            surface.Pulse = double.NaN;
            Check(surface.Pulse == 0, "Invalid onset values cannot create a white flash.");
            var neon = Render(1, 1, effectStyle: GlowStyles.NeonFlow);
            Check(Difference(neon, loud) > .005 && !HasFullSpectrum(neon) && !HasGreenOrYellowDominance(neon),
                "Neon Flow is distinct cyan/pink/violet light with no green or yellow rainbow lobes.");
            Check(HasWhiteCore(neon), "The slim white neon core is continuous around the complete capsule.");
            Check(Difference(neon, Render(1, 1, phase: 1, effectStyle: GlowStyles.NeonFlow)) == 0
                && Difference(neon, Render(1, 1, phase: .99999, effectStyle: GlowStyles.NeonFlow)) < .002,
                "Neon color flow also wraps without a phase seam.");
            var neonInner = Render(1, 1, layer: GlowLayer.Inner, effectStyle: GlowStyles.NeonFlow);
            Check(Pixel(neonInner, 186, 58).A > 40 && Pixel(neonInner, 20, 58).A == 0,
                "Neon retains a visible inner wash clipped under the panel.");
            const double headPosition = .181;
            var neonPulse = Render(1, 1, effectStyle: GlowStyles.NeonFlow, pulse: 1, pulsePosition: headPosition);
            var neonMoved = Render(1, 1, effectStyle: GlowStyles.NeonFlow, pulse: 1, pulsePosition: headPosition + .25);
            Check(TopBloomAlpha(neonPulse) > TopBloomAlpha(neon),
                "A real onset expands the neon bloom beyond its resting radius.");
            Check(Brightness(neonPulse, 186, 18) > Brightness(neonMoved, 186, 18) + 8
                && Brightness(neonPulse, 186, 98) > Brightness(neonMoved, 186, 98) + 8,
                "Two opposite comet highlights travel along the perimeter rather than flashing uniformly.");
            Check(Difference(neonPulse, neonMoved) > .001 && BorderAlpha(neonPulse) == 0,
                "Travel changes the local bloom while all expanded light stays within the fixed 22 DIP padding.");
            Check(Difference(neon, Render(1, 1, effectStyle: GlowStyles.NeonFlow, pulsePosition: .4)) == 0,
                "No comet animation is invented when the onset pulse is zero.");
            Check(AlphaSum(Render(1, 0, layer: GlowLayer.Both, effectStyle: GlowStyles.NeonFlow, pulse: 1)) == 0
                && AlphaSum(Render(0, 1, layer: GlowLayer.Both, effectStyle: GlowStyles.NeonFlow, pulse: 1)) == 0,
                "Disabled intensity or level extinguishes the white core, wash, bloom and pulse.");
            Check(Difference(loud, Render(1, 1, pulse: 1, pulsePosition: .181)) > .001,
                "Rainbow Wave receives the same real onset comet accents.");
            foreach (var style in new[] { GlowStyles.RainbowWave, GlowStyles.NeonFlow })
            {
                var resting = Render(.72, .75, effectStyle: style);
                var moderate = Render(.72, .75, effectStyle: style, pulse: .5, pulsePosition: headPosition);
                var traveling = Render(.72, .75, effectStyle: style, pulse: .5, pulsePosition: headPosition + .25);
                Check(Brightness(moderate, 186, 18) > Brightness(resting, 186, 18) + 20
                    && Brightness(moderate, 186, 98) > Brightness(resting, 186, 98) + 20,
                    $"A moderate detected hit visibly lights both opposing bloom heads in {style}.");
                Check(Brightness(moderate, 186, 18) > Brightness(traveling, 186, 18) + 10
                    && Brightness(moderate, 186, 98) > Brightness(traveling, 186, 98) + 10,
                    $"Moderate onset highlights move locally instead of only brightening the whole {style} perimeter.");
                Check(Difference(resting, moderate) > .001 && Difference(moderate, traveling) > .0005,
                    $"A half-strength pulse changes the rendered {style} halo measurably at saved default intensity.");
                Check(TopBloomAlpha(moderate) > TopBloomAlpha(resting) && BorderAlpha(moderate) == 0,
                    $"A half-strength {style} hit expands its visible halo without exceeding the fixed padding.");
            }

            if (args.Length > 0)
            {
                var folder = Path.GetFullPath(args[0]);
                Directory.CreateDirectory(folder);
                Save(Composite([quiet, soft, medium, loud], false), Path.Combine(folder, "glow-dark.png"));
                Save(Composite([quiet, soft, medium, loud], true), Path.Combine(folder, "glow-light.png"));
                Save(loud, Path.Combine(folder, "glow-alpha.png"));
                Save(PanelPreview(.72, .65, 0, false), Path.Combine(folder, "rgb-wave-panel.png"));
                Save(PanelPreview(.72, .65, .35, true), Path.Combine(folder, "rgb-wave-light.png"));
                Save(inner, Path.Combine(folder, "rgb-wave-inner-alpha.png"));
                Save(PanelPreview(.72, .75, 0, false, GlowStyles.NeonFlow), Path.Combine(folder, "neon-flow-panel.png"));
                Save(PanelPreview(.95, .75, .12, false, GlowStyles.NeonFlow, 1, headPosition), Path.Combine(folder, "neon-flow-pulse.png"));
                Save(PanelPreview(.72, .75, .35, true, GlowStyles.NeonFlow), Path.Combine(folder, "neon-flow-light.png"));
                Save(neonPulse, Path.Combine(folder, "neon-flow-alpha.png"));
                SavePulseComparison(Path.Combine(folder, "pulse-half-neon.png"), GlowStyles.NeonFlow);
                SavePulseComparison(Path.Combine(folder, "pulse-half-rainbow.png"), GlowStyles.RainbowWave);
                var gif = new GifBitmapEncoder();
                var pulse = new GlowEnvelope();
                for (var i = 0; i < 36; i++)
                {
                    var level = .72 + .28 * pulse.Update(i is 0 or 15 ? 0.8 : 0.025, true, Frame);
                    var frame = BitmapFrame.Create(PanelPreview(level, .8, i / 36d, false));
                    var metadata = new BitmapMetadata("gif");
                    metadata.SetQuery("/grctlext/Delay", (ushort)3);
                    metadata.SetQuery("/grctlext/Disposal", (byte)2);
                    gif.Frames.Add(BitmapFrame.Create(frame, null, metadata, null));
                }
                using var stream = File.Create(Path.Combine(folder, "glow-beat.gif"));
                gif.Save(stream);
                SaveNeonPulseGif(Path.Combine(folder, "neon-flow-pulse.gif"));
                Console.WriteLine("Glow previews: " + folder);
            }
            CheckCompanionWindow();
            app.Shutdown();
            Console.WriteLine($"{_passed} glow checks passed.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static void CheckFrameAllocationBudget()
    {
        // A steady animated frame must not rebuild a vector scene or allocate
        // pixel buffers. Leave generous room for WPF/runtime bookkeeping.
        var surface = new ReactiveGlowSurface { Width = 1276, Height = 66, Layer = GlowLayer.Both, Level = .85, Intensity = .75 };
        var canvas = new Canvas { Width = 1328, Height = 118 };
        Canvas.SetLeft(surface, 26); Canvas.SetTop(surface, 26); canvas.Children.Add(surface);
        canvas.Measure(new Size(canvas.Width, canvas.Height));
        canvas.Arrange(new Rect(0, 0, canvas.Width, canvas.Height));
        var target = new RenderTargetBitmap(1328, 118, 96, 96, PixelFormats.Pbgra32);
        foreach (var style in new[] { GlowStyles.RainbowWave, GlowStyles.NeonFlow })
        {
            surface.EffectStyle = style;
            for (var frame = 0; frame < 8; frame++) Draw(frame);
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var frame = 8; frame < 24; frame++) Draw(frame);
            var perFrame = (GC.GetAllocatedBytesForCurrentThread() - before) / 16;
            Check(perFrame < 64 * 1024, $"{style} steady animated frames stay within the UI allocation budget ({perFrame} bytes/frame).");
        }
        void Draw(int frame)
        {
            surface.Phase = frame / 240d;
            surface.Pulse = .65;
            surface.PulsePosition = frame / 48d;
            canvas.UpdateLayout(); target.Clear(); target.Render(canvas);
        }
    }

    private static void CheckCompanionWindow()
    {
        var canvas = new Canvas();
        var panel = new Border { Width = 320, Height = 64, Background = Brushes.Black, CornerRadius = new CornerRadius(32) };
        Canvas.SetLeft(panel, 20);
        Canvas.SetTop(panel, 20);
        canvas.Children.Add(panel);
        // Our own windows stay outside the virtual desktop and never activate.
        var owner = new Window
        {
            WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent,
            ResizeMode = ResizeMode.NoResize, ShowActivated = false, ShowInTaskbar = false,
            Width = 600, Height = 180, Left = SystemParameters.VirtualScreenLeft - 1400,
            Top = SystemParameters.VirtualScreenTop - 900, Content = canvas
        };
        ReactiveGlowWindow? glow = null;
        try
        {
            owner.Show();
            owner.UpdateLayout();
            var ownerHandle = new WindowInteropHelper(owner).Handle;
            glow = new ReactiveGlowWindow(owner);
            Check(!glow.IsVisible && glow.Owner == owner, "The native halo is owned and initially hidden.");
            var before = NativeRect(ownerHandle);
            glow.UpdateBounds(panel, 1);
            glow.UpdateLayout();
            Check(glow.IsVisible && NativeRect(ownerHandle) == before,
                "Showing the halo preserves the interactive panel's native anchor.");
            var handle = new WindowInteropHelper(glow).Handle;
            var styles = GetWindowLongPtr(handle, -20).ToInt64();
            const long required = 0x00080000 | 0x00000020 | 0x00000080 | 0x08000000;
            Check((styles & required) == required && !glow.ShowActivated && !glow.ShowInTaskbar && glow.Topmost,
                "The halo HWND is permanently layered, transparent, nonactivating and absent from the taskbar.");
            foreach (var scale in new[] { .65, 1d, 1.5 })
            {
                panel.LayoutTransform = new ScaleTransform(scale, scale);
                owner.Left += 23;
                owner.Top += 7;
                owner.UpdateLayout();
                var anchor = NativeRect(ownerHandle);
                var panelStart = panel.PointToScreen(new Point());
                var panelEnd = panel.PointToScreen(new Point(panel.ActualWidth, panel.ActualHeight));
                glow.UpdateBounds(panel, scale);
                glow.UpdateLayout();
                var bounds = NativeRect(handle);
                var surfaceStart = glow.Surface.PointToScreen(new Point());
                var surfaceEnd = glow.Surface.PointToScreen(new Point(glow.Surface.ActualWidth, glow.Surface.ActualHeight));
                Check((surfaceStart - panelStart).Length < 1.1 && (surfaceEnd - panelEnd).Length < 1.1
                    && NativeRect(ownerHandle) == anchor,
                    $"The halo follows the exact panel rectangle at {scale:0.##} scale without moving its owner.");
                var dpi = VisualTreeHelper.GetDpi(panel);
                Check(panelStart.X - bounds.Left >= 23 * scale * dpi.DpiScaleX
                    && bounds.Right - panelEnd.X >= 23 * scale * dpi.DpiScaleX
                    && panelStart.Y - bounds.Top >= 23 * scale * dpi.DpiScaleY
                    && bounds.Bottom - panelEnd.Y >= 23 * scale * dpi.DpiScaleY,
                    $"The native surface preserves outward halo padding at {scale:0.##} scale.");
                var nativeDpi = GetDpiForWindow(handle) / 96d;
                Check(Math.Abs(glow.Width * nativeDpi - (bounds.Right - bounds.Left)) < 1.1
                    && Math.Abs(glow.Height * nativeDpi - (bounds.Bottom - bounds.Top)) < 1.1,
                    "The companion's WPF dimensions agree with its physical HWND bounds.");
            }
            glow.Hide();
            Check(!glow.IsVisible, "The halo can be hidden without closing its owner.");
            glow.UpdateBounds(panel, 1.5);
            Check(glow.IsVisible && owner.IsVisible, "A hidden halo can be shown again by valid bounds.");
            owner.Hide();
            glow.UpdateBounds(panel, 1.5);
            Check(!glow.IsVisible, "A hidden owner never leaves a visible halo behind.");
            glow.Close();
            glow.UpdateBounds(panel, 1.5);
            Check(!glow.IsVisible, "Bounds updates safely ignore a closed companion.");
            glow = null;
            owner.Show();
            owner.UpdateLayout();
            var owned = new ReactiveGlowWindow(owner);
            owned.UpdateBounds(panel, 1.5);
            var closedWithOwner = false;
            owned.Closed += (_, _) => closedWithOwner = true;
            owner.Close();
            Check(closedWithOwner && !owned.IsVisible, "Closing the owner closes its companion HWND.");
        }
        finally
        {
            glow?.Close();
            owner.Close();
        }
    }

    private static WindowRect NativeRect(IntPtr handle)
    {
        if (!GetWindowRect(handle, out var rectangle)) throw new InvalidOperationException("Could not read fixture HWND bounds.");
        return rectangle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct WindowRect(int Left, int Top, int Right, int Bottom);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr handle, int index);
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool GetWindowRect(IntPtr handle, out WindowRect rectangle);
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern uint GetDpiForWindow(IntPtr handle);

    private static RenderTargetBitmap Render(double level, double intensity, double phase = 0, GlowLayer layer = GlowLayer.Outer,
        string effectStyle = GlowStyles.RainbowWave, double pulse = 0, double pulsePosition = 0)
    {
        var root = new Canvas { Width = 372, Height = 116 };
        var surface = new ReactiveGlowSurface
        {
            Width = 320, Height = 64, Level = level, Intensity = intensity,
            Phase = phase, Layer = layer, EffectStyle = effectStyle, Pulse = pulse, PulsePosition = pulsePosition
        };
        Canvas.SetLeft(surface, 26);
        Canvas.SetTop(surface, 26);
        root.Children.Add(surface);
        root.Measure(new Size(372, 116));
        root.Arrange(new Rect(0, 0, 372, 116));
        root.UpdateLayout();
        var bitmap = new RenderTargetBitmap(372, 116, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);
        return bitmap;
    }

    private static RenderTargetBitmap PanelPreview(double level, double intensity, double phase, bool light,
        string effectStyle = GlowStyles.RainbowWave, double pulse = 0, double pulsePosition = 0)
    {
        var outer = Render(level, intensity, phase, GlowLayer.Outer, effectStyle, pulse, pulsePosition);
        var inner = Render(level, intensity, phase, GlowLayer.Inner, effectStyle, pulse, pulsePosition);
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(new SolidColorBrush(light ? Color.FromRgb(210, 218, 228) : Color.FromRgb(9, 12, 18)),
                null, new Rect(0, 0, 372, 116));
            drawing.DrawImage(inner, new Rect(0, 0, 372, 116));
            // Match the real layer order: inner RGB, 30% matte black, crisp text,
            // and the independent outward-only companion emission.
            drawing.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(77, 10, 13, 18)),
                null, new Rect(26, 26, 320, 64), 32, 32);
            var text = new FormattedText("144 FPS   CPU 32%   GPU 78%", System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, new Typeface("Segoe UI Semibold"), 14, Brushes.White, 1);
            drawing.DrawText(text, new Point(59, 47));
            drawing.DrawImage(outer, new Rect(0, 0, 372, 116));
        }
        var bitmap = new RenderTargetBitmap(372, 116, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        return bitmap;
    }

    private static void SaveNeonPulseGif(string path)
    {
        var gif = new GifBitmapEncoder();
        for (var index = 0; index < 36; index++)
        {
            // An explicit deterministic onset fixture, not runtime beat synthesis.
            var elapsed = index / 30d;
            var pulse = index < 3 ? index / 2d : Math.Exp(-(index - 2) / 7d);
            var bitmap = PanelPreview(.72 + .23 * pulse, .85, elapsed * .11,
                false, GlowStyles.NeonFlow, pulse, elapsed * .48);
            var metadata = new BitmapMetadata("gif");
            metadata.SetQuery("/grctlext/Delay", (ushort)3);
            metadata.SetQuery("/grctlext/Disposal", (byte)2);
            gif.Frames.Add(BitmapFrame.Create(bitmap, null, metadata, null));
        }
        using var stream = File.Create(path);
        gif.Save(stream);
    }

    private static void SavePulseComparison(string path, string style)
    {
        var resting = PanelPreview(.72, .75, 0, false, style);
        var moderate = PanelPreview(.72, .75, 0, false, style, .5, .181);
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(new SolidColorBrush(Color.FromRgb(9, 12, 18)), null, new Rect(0, 0, 744, 140));
            drawing.DrawImage(resting, new Rect(0, 24, 372, 116));
            drawing.DrawImage(moderate, new Rect(372, 24, 372, 116));
            var typeface = new Typeface("Segoe UI");
            drawing.DrawText(new FormattedText("Resting", System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, typeface, 12, Brushes.White, 1), new Point(26, 8));
            drawing.DrawText(new FormattedText("Detected pulse 0.5", System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, typeface, 12, Brushes.White, 1), new Point(398, 8));
        }
        var bitmap = new RenderTargetBitmap(744, 140, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        Save(bitmap, path);
    }

    private static bool HasWhiteCore(BitmapSource bitmap)
    {
        (int X, int Y)[] samples = [(90, 25), (186, 25), (280, 25),
            (90, 90), (186, 90), (280, 90), (25, 58), (346, 58)];
        return samples.All(sample =>
        {
            var color = Pixel(bitmap, sample.X, sample.Y);
            var minimum = Math.Min(color.R, Math.Min(color.G, color.B));
            var maximum = Math.Max(color.R, Math.Max(color.G, color.B));
            return minimum > 90 && maximum - minimum < 45;
        });
    }

    private static bool HasGreenOrYellowDominance(BitmapSource bitmap)
    {
        var bytes = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(bytes, bitmap.PixelWidth * 4, 0);
        for (var index = 0; index < bytes.Length; index += 4)
        {
            var blue = bytes[index];
            var green = bytes[index + 1];
            var red = bytes[index + 2];
            if (bytes[index + 3] < 50) continue;
            if (green > red * 1.4 && green > blue * 1.2
                || Math.Min(red, green) > blue * 1.25) return true;
        }
        return false;
    }

    private static long TopBloomAlpha(BitmapSource bitmap)
    {
        var bytes = new byte[272 * 8 * 4];
        bitmap.CopyPixels(new Int32Rect(60, 5, 272, 8), bytes, 272 * 4, 0);
        long sum = 0;
        for (var index = 3; index < bytes.Length; index += 4) sum += bytes[index];
        return sum;
    }

    private static int Brightness(BitmapSource bitmap, int x, int y)
    {
        var pixel = Pixel(bitmap, x, y);
        return pixel.R + pixel.G + pixel.B;
    }

    private static long BorderAlpha(BitmapSource bitmap)
    {
        var bytes = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(bytes, bitmap.PixelWidth * 4, 0);
        long sum = 0;
        for (var y = 0; y < bitmap.PixelHeight; y++)
            for (var x = 0; x < bitmap.PixelWidth; x++)
                if (x < 4 || y < 4 || x >= bitmap.PixelWidth - 4 || y >= bitmap.PixelHeight - 4)
                    sum += bytes[(y * bitmap.PixelWidth + x) * 4 + 3];
        return sum;
    }

    private static bool HasFullSpectrum(BitmapSource bitmap)
    {
        var buckets = new bool[6];
        var bytes = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(bytes, bitmap.PixelWidth * 4, 0);
        for (var i = 0; i < bytes.Length; i += 4)
        {
            var red = (double)bytes[i + 2];
            var green = (double)bytes[i + 1];
            var blue = (double)bytes[i];
            var maximum = Math.Max(red, Math.Max(green, blue));
            var minimum = Math.Min(red, Math.Min(green, blue));
            if (bytes[i + 3] < 100 || maximum - minimum < 60) continue;
            var hue = maximum == red ? (green - blue) / (maximum - minimum)
                : maximum == green ? 2 + (blue - red) / (maximum - minimum)
                : 4 + (red - green) / (maximum - minimum);
            hue = (hue + 6) % 6;
            buckets[(int)hue] = true;
        }
        return buckets.All(found => found);
    }

    private static double Difference(BitmapSource first, BitmapSource second)
    {
        var left = new byte[first.PixelWidth * first.PixelHeight * 4];
        var right = new byte[left.Length];
        first.CopyPixels(left, first.PixelWidth * 4, 0);
        second.CopyPixels(right, second.PixelWidth * 4, 0);
        long difference = 0;
        for (var index = 0; index < left.Length; index++) difference += Math.Abs(left[index] - right[index]);
        return difference / (left.Length * 255d);
    }

    private static RenderTargetBitmap Composite(IReadOnlyList<BitmapSource> frames, bool light)
    {
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(new SolidColorBrush(light ? Color.FromRgb(220, 226, 233) : Color.FromRgb(9, 12, 18)),
                null, new Rect(0, 0, 372, frames.Count * 116));
            for (var i = 0; i < frames.Count; i++)
            {
                drawing.DrawRoundedRectangle(new SolidColorBrush(light ? Color.FromRgb(242, 245, 248) : Color.FromRgb(18, 24, 32)),
                    null, new Rect(26, i * 116 + 26, 320, 64), 32, 32);
                drawing.DrawImage(frames[i], new Rect(0, i * 116, 372, 116));
                var text = new FormattedText("144 FPS   CPU 32%   GPU 78%", System.Globalization.CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, new Typeface("Segoe UI"), 14, light ? Brushes.Black : Brushes.White, 1);
                drawing.DrawText(text, new Point(59, i * 116 + 47));
            }
        }
        var bitmap = new RenderTargetBitmap(372, frames.Count * 116, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        return bitmap;
    }

    private static (byte R, byte G, byte B, byte A) Pixel(BitmapSource bitmap, int x, int y)
    {
        var bytes = new byte[4];
        bitmap.CopyPixels(new Int32Rect(x, y, 1, 1), bytes, 4, 0);
        return (bytes[2], bytes[1], bytes[0], bytes[3]);
    }

    private static long AlphaSum(BitmapSource bitmap)
    {
        var bytes = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(bytes, bitmap.PixelWidth * 4, 0);
        long sum = 0;
        for (var i = 3; i < bytes.Length; i += 4) sum += bytes[i];
        return sum;
    }

    private static void Save(BitmapSource bitmap, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException(name);
        _passed++;
    }
}


