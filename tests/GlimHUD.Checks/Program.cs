using System.Diagnostics;
using GlimHUD.Services;
using GlimHUD.Services.Fps;
using GlimHUD.Localization;

namespace GlimHUD.Checks;

public static class Program
{
    public static int Main(string[] args)
    {
        // End-to-end fixture: the service starts this test apphost using the real
        // PresentMon arguments and receives a multiline stderr failure.
        if (args.Contains("--terminate_existing_session")) return 0;
        if (args.Contains("--output_stdout"))
        {
            Console.Error.WriteLine("error: failed to start trace session (access denied).");
            Console.Error.WriteLine("Run with the appropriate user group membership.");
            Console.Error.WriteLine("See README.md for more details.");
            return 1;
        }
        try
        {
            CheckCsv();
            CheckFrameWindows();
            CheckUnavailableTool();
            CheckMultilinePermissionError();
            Console.WriteLine("PASS: CSV parsing, source-time FPS, swap-chain/PID isolation, stale expiry, stream limits, unavailable tool, multiline ETW permission error.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"FAIL: {exception.Message}");
            return 1;
        }
    }

    private static void CheckCsv()
    {
        var parser = new PresentMonCsvParser();
        Check(!parser.TryParse("\uFEFFApplication,ProcessID,SwapChainAddress,TimeInSeconds,MsBetweenPresents", out _), "CSV header must not become a frame.");
        Check(parser.TryParse("\"Game, \"\"Edition\"\".exe\",42,0x1,1.000000,10.000", out var sample), "Quoted application names must be handled.");
        Check(sample.ProcessId == 42 && sample.TimeMilliseconds == 1000 && sample.FrameMilliseconds == 10 && sample.SwapChain == "0x1", "v1 seconds must convert to milliseconds.");
        foreach (var line in new[] { "", "broken,line", "game,42,0x1,1.0,NaN", "game,42,0x1,Infinity,10", "game,42,0x1,1.0,0", "game,42,0x1,1.0,-10", "game,42,0x1,1.0,NA" })
            Check(!parser.TryParse(line, out _), $"Invalid frame accepted: {line}");

        // Header order is not assumed; the current console schema can have extra fields.
        Check(!parser.TryParse("Application,MsBetweenPresents,Unused,CPUStartTime,SwapChainAddress,ProcessID", out _), "Second header must reset schema.");
        Check(parser.TryParse("game.exe,16.666667,value,2000,0x2,100", out var reordered), "Reordered schema must work.");
        Check(reordered.TimeMilliseconds == 2000 && reordered.ProcessId == 100, "Modern CPUStartTime already uses milliseconds.");

        // Failing closed is essential: unrelated performance columns are not frame intervals.
        parser.TryParse("Application,ProcessID,CPUStartTime,MsGPUTime", out _);
        Check(!parser.TryParse("game.exe,100,2000,5", out _), "An unsupported schema must not fabricate FPS.");
    }

    private static void CheckFrameWindows()
    {
        var accumulator = new PresentMonFrameAccumulator();
        var now = Stopwatch.GetTimestamp();

        // ETW batches can deliver a whole second's data simultaneously. The result
        // must use source intervals, not CPU speed or number of ReadLine calls.
        AddFrames(accumulator, 42, "scene", 100, 10, now);
        AddFrames(accumulator, 42, "auxiliary", 20, 50, now);
        AddFrames(accumulator, 43, "scene", 60, 1000.0 / 60, now);
        Near(accumulator.GetFramesPerSecond(42, now), 100, "Swap chains were summed or frame times averaged incorrectly.");
        Near(accumulator.GetFramesPerSecond(43, now), 60, "Process measurements leaked into each other.");
        Check(accumulator.GetFramesPerSecond(99, now) is null, "Unknown process must have no FPS.");
        Check(accumulator.GetFramesPerSecond(42, now + Stopwatch.Frequency * 3) is null, "Old FPS must expire.");

        // Old slow frames must age out of the one-second source-time window.
        for (var i = 100; i < 300; i++) accumulator.Add(new(42, "scene", 1000 + (i - 100) * 5, 5), now);
        Near(accumulator.GetFramesPerSecond(42, now), 200, "FPS did not follow the new source-time window.", tolerance: 1);
        accumulator.Clear();
        Check(accumulator.GetFramesPerSecond(42, now) is null, "Clear retained old FPS.");

        // Only one present is insufficient for a stable measurement.
        accumulator.Add(new(42, "scene", 1, 10), now);
        Check(accumulator.GetFramesPerSecond(42, now) is null, "A single frame must not become stable FPS.");

        // Enforce bounded memory even when processes create many temporary swap chains.
        for (var process = 1000; process < 1400; process++)
            AddFrames(accumulator, process, "transient", 2, 10, now + process);
        Check(accumulator.GetFramesPerSecond(1000, now + 2000) is null, "Old stream was not evicted at the stream limit.");
        Near(accumulator.GetFramesPerSecond(1399, now + 2000), 100, "Recent stream was evicted unexpectedly.");
    }

    private static void CheckUnavailableTool()
    {
        using var service = new PresentMonService(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "PresentMon.exe"));
        service.Start();
        var snapshot = service.GetSnapshot();
        Check(!snapshot.IsAvailable && !snapshot.IsRunning && snapshot.FramesPerSecond is null, "Missing dependency must leave FPS unavailable.");
        Check(!string.IsNullOrWhiteSpace(snapshot.Status), "Missing dependency must have a diagnostic.");
        Check(snapshot.Status.StartsWith("PresentMon is not installed", StringComparison.Ordinal), "Missing dependency status defaults to English.");
        try
        {
            L10n.SetLanguage("ru");
            Check(service.GetSnapshot().Status.StartsWith("PresentMon не установлен", StringComparison.Ordinal), "A stopped collector refreshes its diagnostic after switching to Russian.");
        }
        finally { L10n.SetLanguage("en"); }
        Check(service.GetSnapshot().Status == snapshot.Status, "Switching back to English restores the existing diagnostic.");
        try
        {
            service.SetTargetProcess(Environment.ProcessId);
            throw new Exception("The HUD accepted itself as the game FPS source.");
        }
        catch (ArgumentException) { }
    }

    private static void CheckMultilinePermissionError()
    {
        var executable = Path.Combine(AppContext.BaseDirectory, "GlimHUD.Checks.exe");
        Check(File.Exists(executable), "The ETW error fixture requires the generated test apphost.");
        using var service = new PresentMonService(executable);
        service.Start();
        var deadline = Stopwatch.StartNew();
        FpsSnapshot snapshot;
        do
        {
            Thread.Sleep(25);
            snapshot = service.GetSnapshot();
        } while ((snapshot.IsRunning || !snapshot.Status.Contains("ETW", StringComparison.OrdinalIgnoreCase)) && deadline.Elapsed < TimeSpan.FromSeconds(8));
        Check(snapshot.FramesPerSecond is null && !snapshot.IsRunning, "Permission failure must stop capture and leave FPS unavailable.");
        Check(snapshot.Status.Contains("ETW", StringComparison.OrdinalIgnoreCase) && snapshot.Status.Contains("administrator", StringComparison.OrdinalIgnoreCase),
            $"Trailing README help erased the ETW permission error: {snapshot.Status}");
    }

    private static void AddFrames(PresentMonFrameAccumulator accumulator, int processId, string swapChain, int count, double frameTime, long now)
    {
        for (var frame = 0; frame < count; frame++)
            accumulator.Add(new(processId, swapChain, frame * frameTime, frameTime), now);
    }

    private static void Near(double? actual, double expected, string message, double tolerance = 0.001)
        => Check(actual.HasValue && Math.Abs(actual.Value - expected) <= tolerance, $"{message} Expected {expected}, actual {actual}.");

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
