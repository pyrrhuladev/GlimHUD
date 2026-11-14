using System.Net.NetworkInformation;
using System.Reflection;
using System.Runtime.InteropServices;
using GlimHUD.Localization;
using GlimHUD.Models;
using GlimHUD.Services;

var passed = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    passed++;
}
async Task Throws<T>(Func<Task> action, string message) where T : Exception
{
    try { await action(); }
    catch (T) { passed++; return; }
    throw new InvalidOperationException(message);
}
NetworkAdapterReading Adapter(string id, ulong? rx = 0, ulong? tx = 0, bool physical = true,
    bool up = true, bool gateway = true, uint? metric = null)
    => new(new(id, "Adapter " + id, "Test interface", up, physical, gateway, 1_000_000_000), rx, tx, metric);

var source = new FakeSource();
var clock = new FakeClock();
using (var service = new NetworkTelemetryService(source, clock))
{
    source.Readings = [Adapter("A", 1_000_000, 500_000), Adapter("VPN", 9_000_000, 8_000_000, physical: false, metric: 1)];
    var first = await service.SampleAsync();
    Check(first.Status == NetworkStatus.WarmingUp && first.DownloadBytesPerSecond is null && first.UploadBytesPerSecond is null, "First observation must warm up rather than invent a rate.");
    Check(first.AdapterId == "A" && first.AvailableAdapters.Count == 2, "Auto must select exactly one physical adapter while retaining virtual choices.");
    Check(first.SessionReceivedBytes == 0 && first.SessionSentBytes == 0, "Boot lifetime counters must never be shown as session traffic.");
    clock.Advance(2);
    source.Readings = [Adapter("A", 1_002_000, 500_500), Adapter("VPN", 9_001_000, 8_000_100, physical: false, metric: 1)];
    var second = await service.SampleAsync();
    Check(second.Status == NetworkStatus.Ready && second.DownloadBytesPerSecond == 1000 && second.UploadBytesPerSecond == 250, "Rates must divide counter deltas by actual monotonic elapsed time.");
    Check(second.SessionReceivedBytes == 2000 && second.SessionSentBytes == 500, "Session totals must include only observed per-adapter deltas.");
    Check(first.SessionReceivedBytes == 0 && first.History.Count == 1, "Published snapshots and their history must remain immutable.");
    clock.Advance(1);
    var idle = await service.SampleAsync();
    Check(idle.Status == NetworkStatus.Ready && idle.DownloadBytesPerSecond == 0 && idle.UploadBytesPerSecond == 0, "A measured idle adapter must display genuine zero rates.");
    service.PreferredAdapterId = "VPN";
    clock.Advance(1);
    source.Readings = [Adapter("A", 1_003_000, 501_000), Adapter("VPN", 9_001_100, 8_000_150, physical: false, metric: 1)];
    var selected = await service.SampleAsync();
    Check(selected.AdapterId == "VPN" && selected.DownloadBytesPerSecond == 100 && selected.SessionReceivedBytes == 1100,
        "Explicit virtual selection must use that adapter's independent session, without summing interfaces.");
    Check(selected.History.Count == 1, "Changing adapter identity must clear the previous adapter's graph.");
    service.PreferredAdapterId = "missing";
    clock.Advance(1);
    var missing = await service.SampleAsync();
    Check(missing.Status == NetworkStatus.AdapterMissing && missing.AdapterId == "missing" && missing.DownloadBytesPerSecond is null,
        "An explicitly missing adapter must stay unavailable instead of silently falling back.");
    service.PreferredAdapterId = "A";
    source.Readings = [Adapter("A", null, null, up: false), Adapter("VPN", physical: false)];
    clock.Advance(1);
    var down = await service.SampleAsync();
    Check(down.Status == NetworkStatus.AdapterDown && down.DownloadBytesPerSecond is null && down.SessionReceivedBytes == 3000,
        "Disconnection must preserve known session totals and never select another adapter for an explicit choice.");
    source.Readings = [Adapter("A", 20_000, 10_000)];
    clock.Advance(1);
    var reconnected = await service.SampleAsync();
    Check(reconnected.Status == NetworkStatus.WarmingUp && reconnected.SessionReceivedBytes == 3000, "Reconnect must establish a fresh counter baseline without a spike or boot totals.");
    source.Readings = [Adapter("A", 21_000, 10_100)];
    clock.Advance(1);
    var resumed = await service.SampleAsync();
    Check(resumed.DownloadBytesPerSecond == 1000 && resumed.SessionReceivedBytes == 4000, "Sampling after reconnect must resume the existing adapter session.");
    source.Readings = [Adapter("A", null, null)];
    clock.Advance(1);
    Check((await service.SampleAsync()).Status == NetworkStatus.ReadError, "Unavailable native counters must not masquerade as zero.");
    source.Readings = [Adapter("A", 23_000, 10_300)];
    clock.Advance(1);
    Check((await service.SampleAsync()).Status == NetworkStatus.WarmingUp, "Recovering from a counter read failure requires a new baseline.");
}

source = new FakeSource { Readings = [Adapter("LAN", metric: 50), Adapter("WiFi", metric: 10), Adapter("Virtual", physical: false, metric: 0)] };
clock = new FakeClock();
using (var service = new NetworkTelemetryService(source, clock))
{
    Check((await service.SampleAsync()).AdapterId == "WiFi", "Auto must prefer the lower-cost physical IPv4 default route.");
    source.Readings = [Adapter("LAN", metric: 10), Adapter("WiFi", metric: 10)];
    clock.Advance(1);
    Check((await service.SampleAsync()).AdapterId == "WiFi", "Equally preferred usable routes should retain the adapter instead of flapping.");
    source.Readings = [Adapter("LAN", gateway: false), Adapter("WiFi", gateway: true), Adapter("Virtual", physical: false, metric: 0)];
    clock.Advance(1);
    Check((await service.SampleAsync()).AdapterId == "WiFi", "Gateway metadata must provide the IPv6-only route-preference fallback.");
    source.Readings = [Adapter("Virtual", physical: false, metric: 0)];
    clock.Advance(1);
    Check((await service.SampleAsync()).Status == NetworkStatus.NoAdapter, "Auto must not guess a virtual adapter when physical classification is unavailable.");
    service.PreferredAdapterId = "Virtual";
    Check((await service.SampleAsync()).AdapterId == "Virtual", "The visible virtual adapter must remain available through explicit opt-in.");
}

source = new FakeSource { Readings = [Adapter("A", ulong.MaxValue - 100, 100)] };
clock = new FakeClock();
using (var service = new NetworkTelemetryService(source, clock))
{
    await service.SampleAsync();
    source.Readings = [Adapter("A", 50, 20)]; clock.Advance(1);
    var reset = await service.SampleAsync();
    Check(reset.Status == NetworkStatus.WarmingUp && reset.SessionReceivedBytes == 0, "Counter rollover/reset must not become a negative or enormous rate.");
    source.Readings = [Adapter("A", 100, 30)]; clock.Advance(1);
    var after = await service.SampleAsync();
    Check(after.DownloadBytesPerSecond == 50 && after.UploadBytesPerSecond == 10 && after.SessionReceivedBytes == 50, "A reset baseline must recover on the next valid interval.");
    source.Readings = [Adapter("A", 1000, 100)]; clock.Advance(10);
    var gap = await service.SampleAsync();
    Check(gap.Status == NetworkStatus.WarmingUp && gap.DownloadBytesPerSecond is null && gap.SessionReceivedBytes == 950,
        "A long pause must retain trustworthy deltas but must not display the old average as current throughput.");
    source.Readings = [Adapter("A", 1100, 200)]; clock.Advance(1); clock.WallOffset = TimeSpan.FromDays(4);
    Check((await service.SampleAsync()).DownloadBytesPerSecond == 100, "System wall-clock changes must not alter monotonic throughput calculations.");
    source.Readings = [Adapter("A", 1200, 300)]; clock.Advance(-1);
    var backwards = await service.SampleAsync();
    Check(backwards.Status == NetworkStatus.WarmingUp && backwards.DownloadBytesPerSecond is null, "A nonpositive clock interval must not produce a rate.");
}

source = new FakeSource { Readings = [Adapter("A")] };
clock = new FakeClock();
using (var service = new NetworkTelemetryService(source, clock))
{
    await service.SampleAsync();
    for (var second = 1; second <= 75; second++)
    {
        clock.Advance(1);
        source.Readings = [Adapter("A", (ulong)second * 100, (ulong)second * 10)];
        for (var extra = 0; extra < 4; extra++) await service.SampleAsync();
    }
    var history = service.Snapshot.History;
    Check(history.Count == 60 && history[^1].SampledAt - history[0].SampledAt == TimeSpan.FromSeconds(59), "Extra UI samples must coalesce without shrinking the 60-second history window.");
    Check(history.Select(point => point.SampledAt).Distinct().Count() == history.Count, "The graph must not acquire duplicate same-second samples.");
    clock.Advance(65);
    var gap = await service.SampleAsync();
    Check(gap.History.Count == 1 && gap.History[0].DownloadBytesPerSecond is null, "Old history must expire by time without zero-filling absent intervals.");
}

source = new FakeSource { Readings = [Adapter("A", 0, 0)] };
clock = new FakeClock();
using (var service = new NetworkTelemetryService(source, clock))
{
    await service.SampleAsync();
    source.Readings = [Adapter("A", ulong.MaxValue - 10, 0)]; clock.Advance(1); await service.SampleAsync();
    source.Readings = [Adapter("A", 0, 0)]; clock.Advance(1); await service.SampleAsync();
    source.Readings = [Adapter("A", 100, 0)]; clock.Advance(1);
    Check((await service.SampleAsync()).SessionReceivedBytes == ulong.MaxValue, "Accumulated totals must saturate instead of overflowing into small/negative values.");
    source.Error = new NetworkInformationException(); clock.Advance(1);
    var failed = await service.SampleAsync();
    Check(failed.Status == NetworkStatus.ReadError && failed.DownloadBytesPerSecond is null, "Global enumeration errors must be honest unavailable snapshots.");
    source.Error = null; clock.Advance(1);
    Check((await service.SampleAsync()).Status == NetworkStatus.WarmingUp, "Enumeration recovery must discard stale baselines.");
    source.Readings = Enumerable.Range(0, 257).Select(index => Adapter(index.ToString())).ToArray();
    Check((await service.SampleAsync()).Status == NetworkStatus.ReadError, "An oversized source must not grow counter state without bounds.");
}

source = new FakeSource { Readings = [Adapter("A")] };
clock = new FakeClock();
using (var service = new NetworkTelemetryService(source, clock))
{
    using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
    await Throws<OperationCanceledException>(() => service.SampleAsync(cancelled.Token), "A cancelled sample must not enumerate interfaces.");
    Check(source.ReadCount == 0, "Pre-cancellation must have no counter-read side effect.");
    await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => service.SampleAsync()));
    Check(source.MaximumConcurrentReads == 1, "Concurrent callers must not race native enumeration or baseline updates.");
    service.Dispose();
    await Throws<ObjectDisposedException>(() => service.SampleAsync(), "Disposed monitoring must reject new samples.");
    service.Dispose();
}

L10n.SetLanguage("en");
Check(NetworkValueFormatter.FormatRate(null) == "—" && NetworkValueFormatter.FormatRate(double.NaN) == "—"
    && NetworkValueFormatter.FormatRate(-1) == "—", "Unavailable/invalid rates must remain distinct from zero.");
Check(NetworkValueFormatter.FormatRate(0) == "0 B/s" && NetworkValueFormatter.FormatRate(1500) == "1.5 kB/s", "English byte rates must use explicit decimal SI units.");
Check(NetworkValueFormatter.FormatRate(125_000, useBits: true) == "1 Mb/s", "Bit-rate conversion must multiply byte throughput by eight exactly once.");
Check(NetworkValueFormatter.FormatBytes(1_500_000) == "1.5 MB" && NetworkValueFormatter.FormatLinkSpeed(1_000_000_000) == "1 Gb/s", "Session volume and reported link rate need different units.");
Check(NetworkValueFormatter.FormatLinkSpeed(0) == "—" && NetworkValueFormatter.FormatBytes(null) == "—", "Unknown link rate or session volume must remain unavailable.");
L10n.SetLanguage("ru");
Check(NetworkValueFormatter.FormatRate(1500) == "1,5 кБ/с" && NetworkValueFormatter.FormatBytes(1_500_000) == "1,5 МБ", "Russian formatting must switch both decimal separator and byte units immediately.");
Check(NetworkValueFormatter.FormatLinkSpeed(1_500_000_000) == "1,5 Гбит/с" && NetworkValueFormatter.FormatRate(125_000, useBits: true) == "1 Мбит/с", "Russian link/throughput bit units must be localized.");
L10n.SetLanguage("en");

var nativeRow = typeof(NativeNetworkCounterSource).GetNestedType("MibIfRow2", BindingFlags.NonPublic)!;
Check(Marshal.SizeOf(nativeRow) == 1352 && Marshal.OffsetOf(nativeRow, "InterfaceIndex").ToInt32() == 8
    && Marshal.OffsetOf(nativeRow, "Flags").ToInt32() == 1152, "MIB_IF_ROW2 hardware classification must preserve the documented Windows SDK ABI.");
if (OperatingSystem.IsWindows())
{
    using var native = new NetworkTelemetryService();
    var sample = await native.SampleAsync().WaitAsync(TimeSpan.FromSeconds(10));
    Check(sample.AvailableAdapters.Count <= 256 && sample.DownloadBytesPerSecond is null && sample.UploadBytesPerSecond is null,
        "Native read-only smoke must remain bounded and warm up honestly.");
    Check(sample.AvailableAdapters.All(adapter => adapter.LinkSpeedBitsPerSecond is null or > 0), "Native link rates must be positive or explicitly unavailable.");
    if (sample.AvailableAdapters.Any(adapter => adapter.IsPhysical && adapter.IsOperational))
        Check(sample.Status is NetworkStatus.WarmingUp or NetworkStatus.ReadError
            && sample.AvailableAdapters.Any(adapter => adapter.Id == sample.AdapterId && adapter.IsPhysical),
            "The native Auto smoke must select an actual operational hardware adapter when one exists.");
    Console.WriteLine($"Native read-only smoke: {sample.AvailableAdapters.Count} adapters; {sample.AvailableAdapters.Count(adapter => adapter.IsPhysical)} physical; {sample.Status}.");
}
Console.WriteLine($"Passed {passed} network checks (fake counters/time plus one local native read; no network requests).");

sealed class FakeClock : TimeProvider
{
    private long _timestamp;
    public TimeSpan WallOffset { get; set; }
    public override long TimestampFrequency => 1000;
    public override long GetTimestamp() => _timestamp;
    public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch + TimeSpan.FromMilliseconds(_timestamp) + WallOffset;
    public void Advance(double seconds) => _timestamp += (long)(seconds * TimestampFrequency);
}
sealed class FakeSource : INetworkCounterSource
{
    public IReadOnlyList<NetworkAdapterReading> Readings { get; set; } = [];
    public Exception? Error { get; set; }
    public int ReadCount { get; private set; }
    public int MaximumConcurrentReads { get; private set; }
    private int _inRead;
    public IReadOnlyList<NetworkAdapterReading> Read(CancellationToken token)
    {
        var active = Interlocked.Increment(ref _inRead);
        MaximumConcurrentReads = Math.Max(MaximumConcurrentReads, active);
        try
        {
            token.ThrowIfCancellationRequested();
            ReadCount++;
            if (Error is not null) throw Error;
            return Readings;
        }
        finally { Interlocked.Decrement(ref _inRead); }
    }
}
