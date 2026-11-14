using System.Diagnostics;
using GlimHUD.Services;
using GlimHUD.Services.Fps;
using GlimHUD.Localization;

namespace GlimHUD.Checks;

public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Contains("--fixture-wait")) { Thread.Sleep(8000); return 0; }
        // End-to-end fixture: the service starts this test apphost using the real
        // PresentMon arguments and receives a multiline stderr failure.
        if (args.Contains("--terminate_existing_session")) return 0;
        if (args.Contains("--output_stdout"))
        {
            var fixtureMode = Environment.GetEnvironmentVariable("GLIMHUD_FPS_CHECK_MODE");
            if (fixtureMode == "success") return 0;
            if (fixtureMode == "exit5") return 5;
            if (fixtureMode is "vulkan" or "unsupported-schema")
            {
                // Native Vulkan has no DXGI present events. The fixture deliberately
                // has no output if the required display/kernel tracking was disabled.
                if (args.Contains("--no_track_display")) { Thread.Sleep(2000); return 0; }
                var target = Environment.GetEnvironmentVariable("GLIMHUD_FPS_FIXTURE_PID");
                if (fixtureMode == "unsupported-schema")
                {
                    Console.WriteLine("Application,ProcessID,CPUStartTime,MsGPUTime");
                    Console.WriteLine($"enshrouded.exe,{target},2000,5");
                }
                else
                {
                    Console.WriteLine("Application,ProcessID,SwapChainAddress,PresentRuntime,TimeInSeconds,MsBetweenPresents,MsInPresentAPI");
                    Console.WriteLine($"enshrouded.exe,{target},0,Other,0.0,0.0,0.0");
                    for (var frame = 1; frame <= 60; frame++)
                        Console.WriteLine(FormattableString.Invariant($"enshrouded.exe,{target},0,Other,{frame / 60d:0.000000},{1000d / 60:0.000000},0.0"));
                }
                Thread.Sleep(4000);
                return 0;
            }
            Console.Error.WriteLine("error: failed to start trace session (access denied).");
            Console.Error.WriteLine("Run with the appropriate user group membership.");
            Console.Error.WriteLine("See README.md for more details.");
            return 1;
        }
        try
        {
            CheckCsv();
            CheckFrameWindows();
            CheckCaptureArguments();
            CheckVulkanCaptureFixture();
            CheckUnavailableTool();
            CheckMultilinePermissionError();
            Console.WriteLine("PASS: DXGI/Vulkan CSV, kernel capture flags, source-time FPS, PID/swap-chain isolation, schema diagnostics, staleness, ETW recovery and retry.");
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
        Check(parser.HasHeader && !parser.SupportsSchema, "Unsupported headers must be distinguishable from waiting for a first frame.");

        parser.TryParse("Application,ProcessID,SwapChainAddress,PresentRuntime,TimeInSeconds,MsBetweenPresents,MsInPresentAPI", out _);
        Check(parser.SupportsSchema && parser.LastLineWasHeader, "A supported Vulkan header must recover after a schema change.");
        Check(parser.TryParse("enshrouded.exe,7240,0,Other,12.500000,16.666667,0.000000", out var vulkan),
            "A non-DXGI runtime with zero swap-chain address and zero API duration is a valid present.");
        Check(vulkan.ProcessId == 7240 && vulkan.SwapChain == "0" && vulkan.TimeMilliseconds == 12500 && !parser.LastLineWasHeader,
            "Native Vulkan identity/time must not be discarded or confused with GPU timing.");
    }

    private static void CheckCaptureArguments()
    {
        var arguments = PresentMonService.GetCaptureArguments("GlimHUD-fixture");
        Check(!arguments.Contains("--no_track_display"), "Kernel/display present events are required for non-DXGI renderers.");
        Check(arguments.Contains("--no_track_gpu") && arguments.Contains("--no_track_input"),
            "Extra GPU timing/input collection remains unnecessary for the FPS counter.");
        Check(arguments.Contains("--v1_metrics") && arguments.Contains("--output_stdout"),
            "Capture output must retain the supported CSV protocol.");
        Check(!arguments.Contains("--restart_as_admin"), "The collector must not launch an unrequested permission dialog.");
    }

    private static void CheckVulkanCaptureFixture()
    {
        var executable = Path.Combine(AppContext.BaseDirectory, "GlimHUD.Checks.exe");
        var sleeperStart = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
        sleeperStart.ArgumentList.Add("--fixture-wait");
        using var target = Process.Start(sleeperStart) ?? throw new InvalidOperationException("Could not start the owned FPS target fixture.");
        var previousMode = Environment.GetEnvironmentVariable("GLIMHUD_FPS_CHECK_MODE");
        var previousPid = Environment.GetEnvironmentVariable("GLIMHUD_FPS_FIXTURE_PID");
        try
        {
            Environment.SetEnvironmentVariable("GLIMHUD_FPS_FIXTURE_PID", target.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Environment.SetEnvironmentVariable("GLIMHUD_FPS_CHECK_MODE", "vulkan");
            using (var service = new PresentMonService(executable))
            {
                service.SetTargetProcess(target.Id);
                service.Start();
                var deadline = Stopwatch.StartNew();
                FpsSnapshot snapshot;
                do
                {
                    Thread.Sleep(20);
                    snapshot = service.GetSnapshot();
                } while (snapshot.FramesPerSecond is null && deadline.Elapsed < TimeSpan.FromSeconds(3));
                Near(snapshot.FramesPerSecond, 60, "Native Vulkan presents did not reach the real service stream.", 0.01);
                Check(snapshot.IsRunning && snapshot.IsPinned && snapshot.ProcessId == target.Id && !snapshot.RequiresElevation,
                    "A pinned Vulkan frame stream must remain its exact source without an unrelated elevation warning.");
                Check(snapshot.ParsedFrameCount == 60 && snapshot.ObservedProcessCount == 1 && snapshot.RejectedFrameRowCount == 1,
                    "Capture diagnostics must count valid frames and the unavailable first interval separately.");
            }

            Environment.SetEnvironmentVariable("GLIMHUD_FPS_CHECK_MODE", "unsupported-schema");
            using (var service = new PresentMonService(executable))
            {
                service.SetTargetProcess(target.Id);
                service.Start();
                var deadline = Stopwatch.StartNew();
                FpsSnapshot snapshot;
                do
                {
                    Thread.Sleep(20);
                    snapshot = service.GetSnapshot();
                } while (!snapshot.Status.Contains("unsupported CSV", StringComparison.Ordinal) && deadline.Elapsed < TimeSpan.FromSeconds(3));
                Check(snapshot.FramesPerSecond is null && snapshot.Status.Contains("unsupported CSV", StringComparison.Ordinal),
                    "An unsupported collector schema must show an actionable error, not wait forever.");
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("GLIMHUD_FPS_CHECK_MODE", previousMode);
            Environment.SetEnvironmentVariable("GLIMHUD_FPS_FIXTURE_PID", previousPid);
            if (!target.HasExited) target.Kill();
            target.WaitForExit(1500);
        }
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
        Check(!snapshot.RequiresElevation, "A missing dependency must not suggest an administrator restart.");
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
        } while ((snapshot.IsRunning || !snapshot.RequiresElevation) && deadline.Elapsed < TimeSpan.FromSeconds(8));
        Check(snapshot.FramesPerSecond is null && !snapshot.IsRunning, "Permission failure must stop capture and leave FPS unavailable.");
        Check(snapshot.RequiresElevation && snapshot.Status.Contains("administrator", StringComparison.OrdinalIgnoreCase),
            $"Trailing README help erased the ETW permission error: {snapshot.Status}");
        service.SetTargetProcess(int.MaxValue);
        Check(service.GetSnapshot().RequiresElevation && service.GetSnapshot().Status == snapshot.Status,
            "An exited selected game must not hide the recoverable capture permission error.");

        var previousMode = Environment.GetEnvironmentVariable("GLIMHUD_FPS_CHECK_MODE");
        try
        {
            Environment.SetEnvironmentVariable("GLIMHUD_FPS_CHECK_MODE", "success");
            deadline.Restart();
            do
            {
                service.Start();
                Thread.Sleep(25);
                snapshot = service.GetSnapshot();
            } while ((snapshot.IsRunning || snapshot.RequiresElevation || snapshot.Status != "Frame capture stopped")
                && deadline.Elapsed < TimeSpan.FromSeconds(8));
            Check(!snapshot.RequiresElevation && !snapshot.IsRunning && snapshot.Status == "Frame capture stopped",
                "Retry must clear the previous permission error after a successful capture attempt.");

            Environment.SetEnvironmentVariable("GLIMHUD_FPS_CHECK_MODE", "exit5");
            deadline.Restart();
            do
            {
                service.Start();
                Thread.Sleep(25);
                snapshot = service.GetSnapshot();
            } while ((snapshot.IsRunning || !snapshot.RequiresElevation) && deadline.Elapsed < TimeSpan.FromSeconds(8));
            Check(snapshot.RequiresElevation && !snapshot.IsRunning,
                "Native access-denied exit code 5 must expose the same recovery action without stderr text.");
        }
        finally { Environment.SetEnvironmentVariable("GLIMHUD_FPS_CHECK_MODE", previousMode); }
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
