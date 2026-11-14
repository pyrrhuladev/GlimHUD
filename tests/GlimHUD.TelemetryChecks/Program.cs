using GlimHUD.Services;
using GlimHUD.Localization;
using LibreHardwareMonitor.Hardware;

namespace GlimHUD.TelemetryChecks;

public static class Program
{
    public static async Task<int> Main()
    {
        try
        {
            CheckGpuMetricSelection();
            CheckLocalizedCpuSource();
            await CheckWindowsOnlyFallback();
            await CheckHardwareSampling();
            await CheckLifetime();
            Console.WriteLine("PASS: native CPU/RAM, optional hardware sensors, GPU selection, concurrency, cancellation, disposal.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"FAIL: {exception}");
            return 1;
        }
    }

    private static void CheckLocalizedCpuSource()
    {
        var busy = new TelemetrySnapshot(null, null, null, null, null, null, null, null, null, "");
        var utility = busy with { CpuLoadUsesUtility = true };
        L10n.SetLanguage("en");
        Check(busy.CpuLoadSource == "Windows CPU busy time" && utility.CpuLoadSource == "Windows Processor Utility",
            "CPU diagnostic source names must remain precise in English.");
        L10n.SetLanguage("ru");
        Check(busy.CpuLoadSource.Contains("занятости", StringComparison.Ordinal)
            && utility.CpuLoadSource.Contains("частоты", StringComparison.Ordinal),
            "Existing telemetry snapshots must localize source labels without another hardware read.");
        L10n.SetLanguage("en");
        Check(busy.CpuLoadSource == "Windows CPU busy time" && utility.CpuLoadUsesUtility,
            "Changing the language must preserve the selected CPU metric.");
    }

    private static async Task CheckWindowsOnlyFallback()
    {
        using var service = new TelemetryService(enableHardwareSensors: false);
        var initial = await Sample(service);
        CheckMemory(initial);
        await Task.Delay(400);
        var sample = await Sample(service);
        CheckMemory(sample);
        Check(sample.CpuLoadPercent is not null, "Windows CPU counter did not produce a second-sample load.");
        CheckCpuLoad(sample.CpuLoadPercent);
        Check(sample.CpuTemperatureCelsius is null && sample.GpuLoadPercent is null &&
              sample.GpuTemperatureCelsius is null && sample.AvailableGpus.Count == 0,
            "Windows-only fallback must not invent hardware measurements.");
        Check(!string.IsNullOrWhiteSpace(sample.Status), "Missing hardware must have a status.");
        Console.WriteLine($"Native fallback: CPU {sample.CpuLoadPercent:F1}%; RAM {sample.MemoryUsedBytes / 1073741824.0:F2}/{sample.MemoryTotalBytes / 1073741824.0:F2} GiB.");
    }

    private static async Task CheckHardwareSampling()
    {
        using var service = new TelemetryService();
        var initial = await Sample(service);
        CheckMemory(initial);
        await Task.Delay(400);
        var sample = await Sample(service);
        CheckMemory(sample);
        CheckCpuLoad(sample.CpuLoadPercent);
        CheckTemperature(sample.CpuTemperatureCelsius, "CPU temperature");
        CheckPercent(sample.GpuLoadPercent, "GPU load");
        CheckTemperature(sample.GpuTemperatureCelsius, "GPU temperature");
        Check(sample.CpuLoadPercent is not null, "CPU load must survive unavailable temperature sensors.");
        foreach (var gpu in sample.AvailableGpus)
        {
            Check(!string.IsNullOrWhiteSpace(gpu.Identifier) && !string.IsNullOrWhiteSpace(gpu.Name),
                "Detected adapters need an identity.");
            CheckPercent(gpu.LoadPercent, "Adapter load");
            CheckTemperature(gpu.TemperatureCelsius, "Adapter temperature");
        }

        service.PreferredGpuIdentifier = $"/absent-adapter/{Guid.NewGuid():N}";
        var absent = await Sample(service);
        Check(absent.GpuIdentifier is null && absent.GpuName is null &&
              absent.GpuLoadPercent is null && absent.GpuTemperatureCelsius is null,
            "An explicitly absent GPU must not silently switch to another adapter.");
        CheckMemory(absent);

        service.PreferredGpuIdentifier = null;
        var automatic = await Sample(service);
        if (automatic.AvailableGpus.Count > 0)
            Check(automatic.AvailableGpus.Any(item => item.Identifier == automatic.GpuIdentifier),
                "Automatic selection must refer to a detected adapter.");

        var concurrent = await Task.WhenAll(Sample(service), Sample(service));
        foreach (var reading in concurrent)
            CheckMemory(reading);
        Console.WriteLine($"Hardware mode: {sample.AvailableGpus.Count} GPU(s); CPU temperature {(sample.CpuTemperatureCelsius?.ToString("F1") ?? "unavailable")}; {sample.Status}");
    }

    private static async Task CheckLifetime()
    {
        var service = new TelemetryService(enableHardwareSensors: false);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try
        {
            await service.SampleAsync(cancellation.Token);
            throw new InvalidOperationException("A canceled sample unexpectedly completed.");
        }
        catch (OperationCanceledException) { }

        // Cancellation must not corrupt the next sample or leave a lock held.
        CheckMemory(await Sample(service));
        service.Dispose();
        service.Dispose();
        try
        {
            await service.SampleAsync();
            throw new InvalidOperationException("Disposed service unexpectedly completed a sample.");
        }
        catch (ObjectDisposedException) { }
    }

    private static Task<TelemetrySnapshot> Sample(TelemetryService service) =>
        service.SampleAsync().WaitAsync(TimeSpan.FromSeconds(30));

    private static void CheckMemory(TelemetrySnapshot sample)
    {
        Check(sample.MemoryTotalBytes is > 0, "Physical RAM total is unavailable.");
        Check(sample.MemoryUsedBytes is ulong used && used <= sample.MemoryTotalBytes!.Value,
            "Used physical RAM is outside the physical total.");
        Check(sample.MemoryLoadPercent is not null, "Physical RAM percentage is unavailable.");
        CheckPercent(sample.MemoryLoadPercent, "RAM percentage");
        var expected = sample.MemoryUsedBytes!.Value * 100.0 / sample.MemoryTotalBytes!.Value;
        Check(Math.Abs(expected - sample.MemoryLoadPercent!.Value) < 0.00001,
            "RAM percentage and byte measurements disagree.");
    }

    private static void CheckPercent(double? value, string name) =>
        Check(value is null || double.IsFinite(value.Value) && value is >= 0 and <= 100,
            $"{name} is not a finite percentage: {value}.");

    private static void CheckCpuLoad(double? value) =>
        Check(value is null || double.IsFinite(value.Value) && value >= 0,
            "CPU utility must be finite and nonnegative; boost can legitimately exceed 100%.");

    private static void CheckGpuMetricSelection()
    {
        Check(TelemetryLoadRules.SelectGpuLoad([
            ("GPU Core", SensorType.Load, 45f), ("D3D 3D", SensorType.Load, 82f),
            ("GPU Memory Controller", SensorType.Load, 98f), ("GPU Power", SensorType.Load, 99f)]) == 82,
            "The busiest engine must not be hidden by a lower core reading or confused with memory/power.");
        Check(TelemetryLoadRules.SelectGpuLoad([
            ("GPU Core", SensorType.Load, 89f), ("GPU D3D Copy", SensorType.Load, 31f)]) == 89,
            "Higher vendor core readings must remain available.");
        Check(TelemetryLoadRules.SelectGpuLoad([
            ("D3D Video Decode", SensorType.Load, 77f), ("GPU Core", SensorType.Load, 24f)]) == 77,
            "Busiest non-3D engines are valid GPU work.");
        Check(TelemetryLoadRules.SelectGpuLoad([
            ("GPU Core", SensorType.Load, float.NaN), ("D3D 3D", SensorType.Load, -1f)]) is null,
            "Invalid measurements must not turn into zero load.");
        Check(TelemetryLoadRules.SelectGpuLoad([("GPU Core", SensorType.Load, 0f)]) == 0,
            "A real idle reading remains zero.");
        var native = new WindowsGpuReading("dxgi/example", "0:1", "Example GPU", 0x10DE, 1, false, 81);
        Check(TelemetryLoadRules.UniqueNativeLoad("Example GPU", ["Example GPU"], [native]) == 81,
            "A unique native adapter can supplement its matching hardware sensor.");
        Check(TelemetryLoadRules.UniqueNativeLoad("Example GPU", ["Example GPU", "Example GPU"], [native]) is null,
            "One native adapter must not be copied to two same-model hardware cards.");
        Check(TelemetryLoadRules.UniqueNativeLoad("Example GPU", ["Example GPU"], [native, native with { Luid = "0:2" }]) is null,
            "Two same-model native cards must not be merged into one hardware card.");
    }

    private static void CheckTemperature(double? value, string name) =>
        Check(value is null || double.IsFinite(value.Value) && value is > -20 and < 150,
            $"{name} is not a plausible Celsius measurement: {value}.");

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
