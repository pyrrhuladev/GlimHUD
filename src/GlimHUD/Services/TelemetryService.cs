using System.Runtime.InteropServices;
using LibreHardwareMonitor.Hardware;
using GlimHUD.Localization;

namespace GlimHUD.Services;

/// <summary>One read-only snapshot. Missing measurements are null, never simulated.</summary>
public sealed record TelemetrySnapshot(
    double? CpuLoadPercent,
    double? CpuTemperatureCelsius,
    double? GpuLoadPercent,
    double? GpuTemperatureCelsius,
    ulong? MemoryUsedBytes,
    ulong? MemoryTotalBytes,
    double? MemoryLoadPercent,
    string? CpuName,
    string? GpuName,
    string Status)
{
    public DateTimeOffset SampledAt { get; init; } = DateTimeOffset.UtcNow;
    public string? GpuIdentifier { get; init; }
    public IReadOnlyList<GpuTelemetry> AvailableGpus { get; init; } = [];
}

public sealed record GpuTelemetry(
    string Identifier,
    string Name,
    double? LoadPercent,
    double? TemperatureCelsius,
    bool IsDiscrete);

/// <summary>
/// Samples hardware on a worker thread. Only CPU and graphics monitoring are enabled;
/// the service does not change clocks, fans, voltages, or request elevation.
/// </summary>
public sealed class TelemetryService : IDisposable
{
    private readonly object _sync = new();
    private readonly bool _enableHardwareSensors;
    private Computer? _computer;
    private bool _disposed;
    private string? _preferredGpuIdentifier;
    private string? _lastGpuIdentifier;
    private DateTimeOffset _nextInitializationAttempt;
    private CpuTimes? _previousCpuTimes;

    /// <param name="enableHardwareSensors">
    /// False keeps monitoring entirely on standard Windows CPU/RAM counters, without
    /// opening hardware APIs or the monitoring library's low-level sensor access.
    /// </param>
    public TelemetryService(bool enableHardwareSensors = true)
    {
        _enableHardwareSensors = enableHardwareSensors;
    }

    /// <summary>Optional LHM adapter identifier; null selects an active discrete GPU.</summary>
    public string? PreferredGpuIdentifier
    {
        get => Volatile.Read(ref _preferredGpuIdentifier);
        set => Volatile.Write(ref _preferredGpuIdentifier,
            string.IsNullOrWhiteSpace(value) ? null : value);
    }

    public Task<TelemetrySnapshot> SampleAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                cancellationToken.ThrowIfCancellationRequested();
                return SampleCore(cancellationToken);
            }
        }, cancellationToken);
    }

    private TelemetrySnapshot SampleCore(CancellationToken cancellationToken)
    {
        // Sample Windows counters even when hardware-library initialization fails.
        var nativeCpuLoad = ReadSystemCpuLoad();
        var memory = ReadPhysicalMemory();
        var notes = new List<string>();
        EnsureComputer(notes);
        cancellationToken.ThrowIfCancellationRequested();

        var hardware = new List<IHardware>();
        if (_computer is not null)
        {
            foreach (var root in _computer.Hardware)
                UpdateHardware(root, hardware, notes, cancellationToken);
        }

        var cpus = hardware.Where(item => item.HardwareType == HardwareType.Cpu).ToArray();
        var cpuName = cpus.Length > 0
            ? string.Join(" / ", cpus.Select(item => item.Name).Distinct())
            : Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER");

        var cpuLoad = AverageOrNull(cpus.Select(ReadCpuLoad)) ?? nativeCpuLoad;
        var cpuTemperature = MaxOrNull(cpus.Select(ReadCpuTemperature));

        var gpus = hardware.Where(IsGpu).Select(item => new GpuTelemetry(
            item.Identifier.ToString(), item.Name, ReadGpuLoad(item),
            ReadGpuTemperature(item), IsDiscreteGpu(item))).ToArray();
        var selectedGpu = SelectGpu(gpus);

        if (cpuTemperature is null)
            notes.Add(L10n.Text("CPU temperature unavailable: sensor or access permissions", "Температура CPU недоступна: датчик или права доступа"));
        if (selectedGpu is null)
            notes.Add(PreferredGpuIdentifier is null ? L10n.Text("No GPU detected", "GPU не обнаружен") : L10n.Text("The selected GPU is unavailable", "Выбранный GPU недоступен"));
        else if (selectedGpu.LoadPercent is null || selectedGpu.TemperatureCelsius is null)
            notes.Add(L10n.Text("Some GPU sensors are unavailable", "Часть датчиков GPU недоступна"));
        if (memory.Total is null)
            notes.Add(L10n.Text("Unable to read physical memory", "Не удалось прочитать физическую память"));

        return new TelemetrySnapshot(
            cpuLoad, cpuTemperature,
            selectedGpu?.LoadPercent, selectedGpu?.TemperatureCelsius,
            memory.Used, memory.Total, memory.Percent,
            cpuName, selectedGpu?.Name,
            notes.Count == 0 ? L10n.Text("Sensors connected", "Датчики подключены") : string.Join(". ", notes.Distinct()))
        {
            GpuIdentifier = selectedGpu?.Identifier,
            AvailableGpus = gpus
        };
    }

    private void EnsureComputer(List<string> notes)
    {
        if (!_enableHardwareSensors)
        {
            notes.Add(L10n.Text("Hardware sensors are disabled", "Аппаратные датчики отключены"));
            return;
        }
        if (_computer is not null || DateTimeOffset.UtcNow < _nextInitializationAttempt)
            return;

        var computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsMemoryEnabled = false,
            IsMotherboardEnabled = false,
            IsStorageEnabled = false,
            IsNetworkEnabled = false,
            IsControllerEnabled = false
        };

        try
        {
            computer.Open();
            _computer = computer;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            try { computer.Close(); } catch { /* Best effort after incomplete initialization. */ }
            _nextInitializationAttempt = DateTimeOffset.UtcNow.AddSeconds(30);
            notes.Add(L10n.Text("Hardware sensors are temporarily unavailable", "Аппаратные датчики временно недоступны"));
        }
    }

    private static void UpdateHardware(IHardware hardware, List<IHardware> updated,
        List<string> notes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            hardware.Update();
            updated.Add(hardware);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Do not publish stale values when an adapter disappears or its API fails.
            notes.Add(L10n.Text("Unable to update a hardware sensor", "Не удалось обновить один из аппаратных датчиков"));
        }

        foreach (var child in hardware.SubHardware)
            UpdateHardware(child, updated, notes, cancellationToken);
    }

    private GpuTelemetry? SelectGpu(IReadOnlyList<GpuTelemetry> gpus)
    {
        var preferred = PreferredGpuIdentifier;
        if (preferred is not null)
        {
            // An explicitly selected absent adapter must not silently become another GPU.
            return gpus.FirstOrDefault(item => item.Identifier == preferred);
        }

        var discrete = gpus.Where(item => item.IsDiscrete).ToArray();
        IEnumerable<GpuTelemetry> candidates = discrete.Length > 0 ? discrete : gpus;
        var best = candidates.OrderByDescending(item => item.LoadPercent ?? -1).FirstOrDefault();
        if (best is null)
            return null;

        // A little hysteresis prevents labels flickering between equally busy adapters.
        var previous = candidates.FirstOrDefault(item => item.Identifier == _lastGpuIdentifier);
        if (previous is not null && previous.LoadPercent is double previousLoad &&
            best.LoadPercent is double bestLoad && bestLoad - previousLoad <= 5)
            best = previous;

        _lastGpuIdentifier = best.Identifier;
        return best;
    }

    private static bool IsGpu(IHardware hardware) => hardware.HardwareType is
        HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel;

    private static bool IsDiscreteGpu(IHardware hardware)
    {
        if (hardware.HardwareType == HardwareType.GpuNvidia)
            return true;
        var name = hardware.Name;
        // AMD and Intel have both integrated and discrete devices. Prefer only models
        // that can be identified confidently; the adapter selector remains authoritative.
        return new[] { "Radeon RX", "Radeon Pro", "FirePro", "Radeon R9", "Radeon R7",
                "Radeon HD", "Arc A", "Arc B", "Arc Pro" }
            .Any(part => name.Contains(part, StringComparison.OrdinalIgnoreCase));
    }

    private static double? ReadCpuLoad(IHardware hardware) => ReadNamedSensor(hardware,
        SensorType.Load, IsPercent, "CPU Total");

    private static double? ReadCpuTemperature(IHardware hardware)
    {
        var package = ReadNamedSensor(hardware, SensorType.Temperature, IsTemperature,
            "CPU Package", "Core (Tctl/Tdie)", "Core (Tdie)", "CPU (Tctl/Tdie)",
            "CPU (Tdie)", "CPU Die (average)", "Core Max", "Core Average");
        if (package is not null)
            return package;

        // "Distance to TjMax" also has Temperature type but is not a temperature.
        return MaxOrNull(hardware.Sensors
            .Where(sensor => sensor.SensorType == SensorType.Temperature &&
                (sensor.Name.StartsWith("CPU Core", StringComparison.OrdinalIgnoreCase) ||
                 sensor.Name.StartsWith("Core #", StringComparison.OrdinalIgnoreCase) ||
                 sensor.Name.StartsWith("CCD", StringComparison.OrdinalIgnoreCase)) &&
                !sensor.Name.Contains("Distance", StringComparison.OrdinalIgnoreCase))
            .Select(sensor => ValidValue(sensor.Value, IsTemperature)));
    }

    private static double? ReadGpuLoad(IHardware hardware)
    {
        var core = ReadNamedSensor(hardware, SensorType.Load, IsPercent, "GPU Core");
        return core ?? MaxOrNull(hardware.Sensors
            .Where(sensor => sensor.SensorType == SensorType.Load &&
                (sensor.Name.Equals("D3D 3D", StringComparison.OrdinalIgnoreCase) ||
                 sensor.Name.Equals("GPU D3D 3D", StringComparison.OrdinalIgnoreCase)))
            .Select(sensor => ValidValue(sensor.Value, IsPercent)));
    }

    private static double? ReadGpuTemperature(IHardware hardware) => ReadNamedSensor(
        hardware, SensorType.Temperature, IsTemperature, "GPU Core", "GPU Temperature");

    private static double? ReadNamedSensor(IHardware hardware, SensorType type,
        Func<double, bool> isValid, params string[] names)
    {
        foreach (var name in names)
        {
            var value = hardware.Sensors.Where(sensor => sensor.SensorType == type &&
                    sensor.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                .Select(sensor => ValidValue(sensor.Value, isValid))
                .FirstOrDefault(value => value is not null);
            if (value is not null)
                return value;
        }
        return null;
    }

    private static double? ValidValue(float? value, Func<double, bool> isValid) =>
        value is float number && float.IsFinite(number) && isValid(number) ? number : null;

    private static bool IsPercent(double value) => value is >= 0 and <= 100;
    private static bool IsTemperature(double value) => value is > -20 and < 150;

    private static double? MaxOrNull(IEnumerable<double?> values)
    {
        var measured = values.OfType<double>().ToArray();
        return measured.Length == 0 ? null : measured.Max();
    }

    private static double? AverageOrNull(IEnumerable<double?> values)
    {
        var measured = values.OfType<double>().ToArray();
        return measured.Length == 0 ? null : measured.Average();
    }

    private double? ReadSystemCpuLoad()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user))
            return null;

        var current = new CpuTimes(idle.Value, kernel.Value, user.Value);
        var previous = _previousCpuTimes;
        _previousCpuTimes = current;
        if (previous is null || current.Idle < previous.Idle ||
            current.Kernel < previous.Kernel || current.User < previous.User)
            return null;

        // Kernel time includes idle time; adding idle a second time understates CPU load.
        var totalDelta = (double)(current.Kernel - previous.Kernel) +
                         (current.User - previous.User);
        var idleDelta = current.Idle - previous.Idle;
        return totalDelta > 0
            ? Math.Clamp((totalDelta - idleDelta) * 100 / totalDelta, 0, 100)
            : null;
    }

    private static (ulong? Used, ulong? Total, double? Percent) ReadPhysicalMemory()
    {
        var status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        if (!GlobalMemoryStatusEx(ref status) || status.TotalPhysical == 0 ||
            status.AvailablePhysical > status.TotalPhysical)
            return (null, null, null);
        var used = status.TotalPhysical - status.AvailablePhysical;
        return (used, status.TotalPhysical, used * 100.0 / status.TotalPhysical);
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
                return;
            _disposed = true;
            try { _computer?.Close(); } catch { /* Shutdown must not fail on a driver error. */ }
            _computer = null;
        }
    }

    private sealed record CpuTimes(ulong Idle, ulong Kernel, ulong User);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeFileTime
    {
        public uint Low;
        public uint High;
        public readonly ulong Value => ((ulong)High << 32) | Low;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out NativeFileTime idle,
        out NativeFileTime kernel, out NativeFileTime user);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
}
