using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GlimHUD.Controls;
using GlimHUD.Models;

/// <summary>
/// A bounded, opt-in raster benchmark. It never opens a window or initializes
/// application services. RenderTargetBitmap results do not measure GPU usage.
/// </summary>
internal static class GlowRenderBenchmark
{
    private const int PanelWidth = 1276;
    private const int PanelHeight = 66;
    private const int Margin = 26;
    private const int WarmupFrames = 4;
    private const int MeasuredFrames = 45;

    internal static int Run(string reportPath)
    {
        var surface = new ReactiveGlowSurface
        {
            Width = PanelWidth,
            Height = PanelHeight,
            Layer = GlowLayer.Both,
            Intensity = .75
        };
        var canvas = new Canvas { Width = PanelWidth + 2 * Margin, Height = PanelHeight + 2 * Margin };
        Canvas.SetLeft(surface, Margin);
        Canvas.SetTop(surface, Margin);
        canvas.Children.Add(surface);
        canvas.Measure(new Size(canvas.Width, canvas.Height));
        canvas.Arrange(new Rect(0, 0, canvas.Width, canvas.Height));
        canvas.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)canvas.Width, (int)canvas.Height, 96, 96, PixelFormats.Pbgra32);
        var results = new List<object>();
        using var process = Process.GetCurrentProcess();

        foreach (var style in new[] { GlowStyles.RainbowWave, GlowStyles.NeonFlow })
        foreach (var pulsed in new[] { false, true })
        {
            surface.EffectStyle = style;
            for (var frame = 0; frame < WarmupFrames; frame++) Render(frame);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            var timings = new double[MeasuredFrames];
            var allocations = new long[MeasuredFrames];
            var totalAllocatedBefore = GC.GetTotalAllocatedBytes(true);
            var cpuBefore = process.TotalProcessorTime;
            var caseStart = Stopwatch.GetTimestamp();
            for (var frame = 0; frame < MeasuredFrames; frame++)
            {
                var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
                var started = Stopwatch.GetTimestamp();
                Render(frame + WarmupFrames);
                timings[frame] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                allocations[frame] = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
            }
            var elapsed = Stopwatch.GetElapsedTime(caseStart).TotalMilliseconds;
            var cpu = (process.TotalProcessorTime - cpuBefore).TotalMilliseconds;
            var totalAllocated = GC.GetTotalAllocatedBytes(true) - totalAllocatedBefore;
            Array.Sort(timings);
            Array.Sort(allocations);
            results.Add(new
            {
                style,
                signal = pulsed ? "synthetic-pulse" : "calm",
                measuredFrames = MeasuredFrames,
                renderElapsedMillisecondsP50 = Percentile(timings, .50),
                renderElapsedMillisecondsP95 = Percentile(timings, .95),
                renderElapsedMillisecondsMean = timings.Average(),
                renderElapsedMillisecondsMax = timings[^1],
                caseElapsedMilliseconds = elapsed,
                processCpuMilliseconds = cpu,
                uiThreadAllocatedBytes = allocations.Sum(),
                uiThreadAllocatedBytesPerFrameP50 = Percentile(allocations, .50),
                uiThreadAllocatedBytesPerFrameP95 = Percentile(allocations, .95),
                processAllocatedBytes = totalAllocated
            });

            void Render(int frame)
            {
                // Advance a deterministic 30 Hz animation timeline. Rendering
                // is intentionally unpaced so the result is raster throughput.
                var sinceHit = (frame % 18) / 30d;
                var pulse = pulsed ? Math.Exp(-sinceHit / .36) : 0;
                surface.Level = .72 + .28 * pulse;
                surface.Phase = frame / 30d / 8;
                surface.Pulse = pulse;
                surface.PulsePosition = pulsed ? sinceHit / .72 : 0;
                canvas.UpdateLayout();
                bitmap.Clear();
                bitmap.Render(canvas);
            }
        }

        var report = new
        {
            measurement = "Offscreen CPU/raster timing, NOT GPU measurement",
            limitations = "RenderTargetBitmap is not representative of onscreen WPF/DWM GPU utilization. "
                + "Animation advances at 30 Hz; measured renders run without frame pacing. "
                + "Elapsed timings include UI dependency updates, layout, bitmap clear and raster render. "
                + "Process CPU and allocations include any WPF worker activity during each case.",
            generatedAtUtc = DateTimeOffset.UtcNow,
            runtime = RuntimeInformation.FrameworkDescription,
            os = RuntimeInformation.OSDescription,
            processorCount = Environment.ProcessorCount,
            renderingTier = RenderCapability.Tier >> 16,
            rendererSource = typeof(ReactiveGlowSurface).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(attribute => attribute.Key == "GlowSurfaceSource")?.Value,
            rendererAssembly = typeof(ReactiveGlowSurface).Assembly.Location,
            panelWidthDip = PanelWidth,
            panelHeightDip = PanelHeight,
            haloMarginDip = Margin,
            dpi = 96,
            layer = "Both",
            intensity = .75,
            warmupFramesPerCase = WarmupFrames,
            results
        };
        var path = Path.GetFullPath(reportPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("Offscreen CPU/raster timing, NOT GPU measurement: " + path);
        return 0;
    }

    private static double Percentile(double[] sorted, double percentile)
        => sorted[(int)Math.Ceiling(sorted.Length * percentile) - 1];

    private static long Percentile(long[] sorted, double percentile)
        => sorted[(int)Math.Ceiling(sorted.Length * percentile) - 1];
}
