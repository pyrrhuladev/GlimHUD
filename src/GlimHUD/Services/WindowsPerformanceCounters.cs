using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace GlimHUD.Services;

public sealed record WindowsPerformanceSnapshot(
    double? CpuUtilityPercent,
    IReadOnlyList<WindowsGpuReading> Gpus);

public sealed record WindowsGpuReading(
    string Identifier,
    string Luid,
    string Name,
    uint VendorId,
    uint DeviceId,
    bool IsSoftware,
    double? LoadPercent);

/// <summary>
/// Reads Windows' frequency-aware CPU utility and busiest GPU engine counters.
/// Uses no hardware driver, elevation, process injection, or system configuration.
/// PDH query state is retained so rate counters measure a real interval.
/// </summary>
public sealed class WindowsPerformanceCounters : IDisposable
{
    private const uint FormatDouble = 0x00000200;
    private const uint FormatNoCap100 = 0x00008000;
    private const uint MoreData = 0x800007D2;
    private const int MaximumArrayBytes = 16 * 1024 * 1024;
    private const uint MaximumItems = 65536;
    private readonly object _gate = new();
    private readonly bool _includeGpu;
    private IntPtr _query;
    private IntPtr _cpuCounter;
    private IntPtr _gpuCounter;
    private bool _cpuCollected;
    private bool _gpuCollected;
    private bool _disposed;
    private int _collectionFailures;
    private DateTimeOffset _nextQueryAttempt;
    private DateTimeOffset _nextCpuAttempt;
    private DateTimeOffset _nextGpuAttempt;
    private DateTimeOffset _nextAdapterRefresh;
    private IReadOnlyList<WindowsGpuReading> _adapters = [];

    public WindowsPerformanceCounters(bool includeGpu = true) => _includeGpu = includeGpu;

    public WindowsPerformanceSnapshot Sample()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!OperatingSystem.IsWindows()) return new(null, []);
            var now = DateTimeOffset.UtcNow;
            if (_includeGpu && now >= _nextAdapterRefresh)
            {
                _adapters = EnumerateAdapters();
                _nextAdapterRefresh = now.AddSeconds(_adapters.Count == 0 ? 5 : 30);
            }

            EnsureCounters(now);
            double? cpu = null;
            IReadOnlyDictionary<string, double> engines = new Dictionary<string, double>();
            if (_query != IntPtr.Zero && (_cpuCounter != IntPtr.Zero || _gpuCounter != IntPtr.Zero))
            {
                if (PdhCollectQueryData(_query) == 0)
                {
                    _collectionFailures = 0;
                    if (_cpuCounter != IntPtr.Zero)
                    {
                        if (_cpuCollected && PdhGetFormattedCounterValue(_cpuCounter,
                            FormatDouble | FormatNoCap100, out _, out var value) == 0)
                            cpu = PerformanceCounterRules.ValidCpuUtility(value.Status, value.Value);
                        _cpuCollected = true;
                    }
                    if (_gpuCounter != IntPtr.Zero)
                    {
                        if (_gpuCollected) engines = PerformanceCounterRules.AggregateGpuEngines(ReadGpuValues());
                        _gpuCollected = true;
                    }
                }
                else
                {
                    // Never retain stale readings through an unavailable/reset query.
                    _cpuCollected = _gpuCollected = false;
                    if (++_collectionFailures >= 3)
                    {
                        CloseQuery();
                        _nextQueryAttempt = now.AddSeconds(10);
                    }
                }
            }

            return new(cpu, _adapters.Select(adapter => adapter with
            {
                LoadPercent = engines.TryGetValue(adapter.Luid, out var load) ? load : null
            }).ToArray());
        }
    }

    private void EnsureCounters(DateTimeOffset now)
    {
        if (_query == IntPtr.Zero)
        {
            if (now < _nextQueryAttempt) return;
            if (PdhOpenQueryW(null, UIntPtr.Zero, out _query) != 0)
            {
                _query = IntPtr.Zero;
                _nextQueryAttempt = now.AddSeconds(10);
                return;
            }
        }
        if (_cpuCounter == IntPtr.Zero && now >= _nextCpuAttempt)
        {
            if (PdhAddEnglishCounterW(_query, @"\Processor Information(_Total)\% Processor Utility",
                UIntPtr.Zero, out _cpuCounter) != 0)
            {
                _cpuCounter = IntPtr.Zero;
                _nextCpuAttempt = now.AddSeconds(10);
            }
        }
        if (_includeGpu && _gpuCounter == IntPtr.Zero && now >= _nextGpuAttempt)
        {
            if (PdhAddEnglishCounterW(_query, @"\GPU Engine(*)\Utilization Percentage",
                UIntPtr.Zero, out _gpuCounter) != 0)
            {
                _gpuCounter = IntPtr.Zero;
                _nextGpuAttempt = now.AddSeconds(10);
            }
        }
    }

    private IReadOnlyList<GpuEngineCounterValue> ReadGpuValues()
    {
        uint size = 0;
        var status = PdhGetFormattedCounterArrayW(_gpuCounter, FormatDouble | FormatNoCap100,
            ref size, out _, IntPtr.Zero);
        if (status != MoreData || size == 0 || size > MaximumArrayBytes) return [];
        // Process instances can change between sizing and filling the array.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var allocated = size;
            var buffer = Marshal.AllocHGlobal(checked((int)allocated));
            try
            {
                status = PdhGetFormattedCounterArrayW(_gpuCounter, FormatDouble | FormatNoCap100,
                    ref size, out var count, buffer);
                if (status == MoreData)
                {
                    // Re-query required size, as the too-small call's size is not authoritative.
                    size = 0;
                    status = PdhGetFormattedCounterArrayW(_gpuCounter, FormatDouble | FormatNoCap100,
                        ref size, out _, IntPtr.Zero);
                    if (status != MoreData || size == 0 || size > MaximumArrayBytes) return [];
                    continue;
                }
                var itemSize = Marshal.SizeOf<FormattedCounterItem>();
                if (status != 0 || count > MaximumItems || count > allocated / itemSize) return [];
                var result = new List<GpuEngineCounterValue>(checked((int)count));
                var beginning = buffer.ToInt64();
                var end = beginning + allocated;
                for (var i = 0; i < count; i++)
                {
                    var item = Marshal.PtrToStructure<FormattedCounterItem>(IntPtr.Add(buffer, checked(i * itemSize)));
                    var nameAddress = item.Name.ToInt64();
                    if (nameAddress < beginning || nameAddress >= end) continue;
                    var characters = (int)Math.Min(512, (end - nameAddress) / 2);
                    if (characters == 0) continue;
                    var name = Marshal.PtrToStringUni(item.Name, characters);
                    var terminator = name?.IndexOf('\0') ?? -1;
                    if (terminator < 0) continue;
                    result.Add(new(name![..terminator], item.Value.Value, item.Value.Status));
                }
                return result;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        return [];
    }

    private static IReadOnlyList<WindowsGpuReading> EnumerateAdapters()
    {
        var result = new List<WindowsGpuReading>();
        var iid = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");
        IntPtr factory = IntPtr.Zero;
        try
        {
            if (CreateDXGIFactory1(ref iid, out factory) < 0 || factory == IntPtr.Zero) return result;
            var enumerate = GetComMethod<EnumAdapters1>(factory, 12);
            for (uint index = 0; index < 64; index++)
            {
                var status = enumerate(factory, index, out var adapter);
                if (status < 0 || adapter == IntPtr.Zero) break;
                try
                {
                    if (GetComMethod<GetDescription1>(adapter, 10)(adapter, out var description) < 0) continue;
                    var luid = PerformanceCounterRules.FormatLuid(unchecked((uint)description.Luid.High), description.Luid.Low);
                    result.Add(new("dxgi/" + luid.Replace(':', '/'), luid,
                        description.Description.TrimEnd('\0'), description.VendorId, description.DeviceId,
                        (description.Flags & 2) != 0, null));
                }
                finally { Marshal.Release(adapter); }
            }
        }
        catch (Exception error) when (error is COMException or DllNotFoundException or EntryPointNotFoundException)
        {
            // Remote/headless sessions need not expose a DXGI adapter.
        }
        finally { if (factory != IntPtr.Zero) Marshal.Release(factory); }
        return result;
    }

    private static T GetComMethod<T>(IntPtr instance, int slot) where T : Delegate
        => Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * IntPtr.Size));

    private void CloseQuery()
    {
        if (_query != IntPtr.Zero) PdhCloseQuery(_query);
        _query = _cpuCounter = _gpuCounter = IntPtr.Zero;
        _cpuCollected = _gpuCollected = false;
        _collectionFailures = 0;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            CloseQuery();
            _adapters = [];
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FormattedCounterValue { public uint Status; public double Value; }
    [StructLayout(LayoutKind.Sequential)]
    private struct FormattedCounterItem { public IntPtr Name; public FormattedCounterValue Value; }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeLuid { public uint Low; public int High; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct AdapterDescription1
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        public uint VendorId, DeviceId, SubsystemId, Revision;
        public nuint DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
        public NativeLuid Luid;
        public uint Flags;
    }
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int EnumAdapters1(IntPtr factory, uint index, out IntPtr adapter);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetDescription1(IntPtr adapter, out AdapterDescription1 description);
    [DllImport("dxgi.dll", ExactSpelling = true)]
    private static extern int CreateDXGIFactory1(ref Guid iid, out IntPtr factory);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint PdhOpenQueryW(string? dataSource, UIntPtr userData, out IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint PdhAddEnglishCounterW(IntPtr query, string path, UIntPtr userData, out IntPtr counter);
    [DllImport("pdh.dll", ExactSpelling = true)]
    private static extern uint PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll", ExactSpelling = true)]
    private static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format, out uint type, out FormattedCounterValue value);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint bufferSize, out uint count, IntPtr buffer);
    [DllImport("pdh.dll", ExactSpelling = true)]
    private static extern uint PdhCloseQuery(IntPtr query);
}

internal readonly record struct GpuEngineCounterValue(string InstanceName, double Value, uint Status = 0);
internal readonly record struct GpuEngineIdentity(string Luid, uint PhysicalAdapter, uint Engine);

internal static class PerformanceCounterRules
{
    private static readonly Regex EngineName = new(
        @"^pid_[0-9]+_luid_0x(?<high>[0-9a-f]{1,8})_0x(?<low>[0-9a-f]{1,8})_phys_(?<physical>[0-9]+)_eng_(?<engine>[0-9]+)_engtype_.+$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(20));

    internal static string FormatLuid(uint high, uint low) => $"{high:x8}:{low:x8}";

    internal static double? ValidCpuUtility(uint status, double value)
        => status is 0 or 1 && double.IsFinite(value) && value >= 0 ? value : null;

    internal static bool TryParseEngine(string name, out GpuEngineIdentity identity)
    {
        identity = default;
        if (name.Length > 512) return false;
        var match = EngineName.Match(name);
        if (!match.Success || !uint.TryParse(match.Groups["high"].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var high)
            || !uint.TryParse(match.Groups["low"].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var low)
            || !uint.TryParse(match.Groups["physical"].Value, CultureInfo.InvariantCulture, out var physical)
            || !uint.TryParse(match.Groups["engine"].Value, CultureInfo.InvariantCulture, out var engine)) return false;
        identity = new(FormatLuid(high, low), physical, engine);
        return true;
    }

    internal static IReadOnlyDictionary<string, double> AggregateGpuEngines(IEnumerable<GpuEngineCounterValue> values)
    {
        var engines = new Dictionary<GpuEngineIdentity, double>();
        foreach (var item in values)
        {
            if (item.Status is not (0 or 1) || !double.IsFinite(item.Value) || item.Value < 0
                || !TryParseEngine(item.InstanceName, out var identity)) continue;
            // Add all process contexts of the same physical engine. Different
            // engines may run concurrently, so averaging or summing them is wrong.
            engines.TryGetValue(identity, out var previous);
            engines[identity] = Math.Min(100, previous + item.Value);
        }
        return engines.GroupBy(pair => pair.Key.Luid).ToDictionary(group => group.Key,
            group => group.Max(pair => pair.Value), StringComparer.OrdinalIgnoreCase);
    }
}
