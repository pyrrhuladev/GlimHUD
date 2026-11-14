using GlimHUD.Services;

var checks = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    checks++;
    Console.WriteLine("PASS " + message);
}

string Engine(int pid, int engine, string low = "1234", int physical = 0)
    => $"pid_{pid}_luid_0x00000000_0x{low}_phys_{physical}_eng_{engine}_engtype_3D";

Check(PerformanceCounterRules.ValidCpuUtility(0, 145.6) == 145.6,
    "CPU utility preserves real turbo values above100.");
Check(PerformanceCounterRules.ValidCpuUtility(1, 0) == 0,
    "New valid data and real zero are accepted.");
Check(PerformanceCounterRules.ValidCpuUtility(0xC0000BBA, 50) is null
      && PerformanceCounterRules.ValidCpuUtility(0, -1) is null
      && PerformanceCounterRules.ValidCpuUtility(0, double.NaN) is null,
    "Invalid, reset and non-finite CPU data are unavailable.");
Check(PerformanceCounterRules.TryParseEngine(Engine(100, 3), out var identity)
      && identity == new GpuEngineIdentity("00000000:00001234", 0, 3),
    "GPU instance parsing uses numeric LUID and engine identity.");
Check(!PerformanceCounterRules.TryParseEngine("_Total", out _)
      && !PerformanceCounterRules.TryParseEngine("pid_1_luid_0xFFFFFFFFF_0x1_phys_0_eng_0_engtype_3D", out _),
    "Unrelated or overflowing instance names are rejected.");
var loads = PerformanceCounterRules.AggregateGpuEngines([
    new(Engine(1, 0), 20), new(Engine(2, 0), 35), new(Engine(3, 1), 40),
    new(Engine(4, 0, "5678"), 70), new(Engine(5, 1, "5678"), 65),
    new(Engine(6, 0, "9abc"), 85), new(Engine(7, 0, "9abc"), 25),
    new(Engine(8, 0, "def0"), double.NaN), new(Engine(9, 0, "def0"), -1),
    new(Engine(10, 0, "def0"), 90, 0xC0000BBA)
]);
Check(loads["00000000:00001234"] == 55,
    "Different processes on the same engine sum before choosing the busiest engine.");
Check(loads["00000000:00005678"] == 70,
    "Concurrent engines use maximum instead of sum or average.");
Check(loads["00000000:00009abc"] == 100,
    "GPU rounding and overlapping contexts are capped at100.");
Check(!loads.ContainsKey("00000000:0000def0"),
    "Unavailable GPU counters stay unavailable instead of becoming zero.");
var separatePhysical = PerformanceCounterRules.AggregateGpuEngines([
    new(Engine(1, 0, physical: 0), 40), new(Engine(2, 0, physical: 1), 50)
]);
Check(separatePhysical["00000000:00001234"] == 50,
    "Same numbered engines on different physical nodes do not sum.");

if (OperatingSystem.IsWindows())
{
    using var counters = new WindowsPerformanceCounters();
    var first = counters.Sample();
    Check(first.CpuUtilityPercent is null, "Native rate counter has an explicit warm-up sample.");
    await Task.Delay(1050);
    var second = counters.Sample();
    Check(second.CpuUtilityPercent is null || double.IsFinite(second.CpuUtilityPercent.Value)
        && second.CpuUtilityPercent >= 0, "Native CPU sample is finite and nonnegative when available.");
    Check(second.Gpus.All(gpu => !string.IsNullOrWhiteSpace(gpu.Luid)
        && !string.IsNullOrWhiteSpace(gpu.Name)
        && (gpu.LoadPercent is null || gpu.LoadPercent is >= 0 and <= 100)),
        "Native DXGI identities and GPU readings are valid.");
    Console.WriteLine($"Native CPU utility: {second.CpuUtilityPercent?.ToString("F2") ?? "unavailable"}%");
    foreach (var gpu in second.Gpus.Where(gpu => !gpu.IsSoftware))
        Console.WriteLine($"Native GPU: {gpu.Name}; LUID {gpu.Luid}; load {gpu.LoadPercent?.ToString("F2") ?? "unavailable"}%");
    using var cpuOnly = new WindowsPerformanceCounters(includeGpu: false);
    Check(cpuOnly.Sample().Gpus.Count == 0, "CPU-only mode does not initialize GPU monitoring.");
    counters.Dispose();
    try { counters.Sample(); throw new InvalidOperationException("Disposed native counter sampler was used."); }
    catch (ObjectDisposedException) { checks++; }
}
Console.WriteLine($"{checks} performance counter checks passed.");
