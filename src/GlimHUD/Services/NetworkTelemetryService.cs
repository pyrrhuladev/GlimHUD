using GlimHUD.Models;

namespace GlimHUD.Services;

/// <summary>
/// Reads local interface counters only. The caller supplies a roughly one-second
/// sampling cadence; no sockets, probes, packet capture or elevation are used.
/// Auto selects one physical Ethernet/Wi-Fi adapter; virtual adapters are opt-in.
/// </summary>
public sealed class NetworkTelemetryService : IDisposable
{
    private const int AdapterLimit = 256;
    private const int HistoryLimit = 60;
    private const double MaximumRateIntervalSeconds = 5;
    private readonly object _sync = new();
    private readonly INetworkCounterSource _source;
    private readonly TimeProvider _clock;
    private readonly Dictionary<string, AdapterSession> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(long Bucket, long Timestamp, NetworkHistorySample Sample)> _history = [];
    private readonly CancellationTokenSource _shutdown = new();
    private string? _preferredAdapterId;
    private string? _historyAdapterId;
    private long _historyEpoch;
    private string? _lastAutomaticAdapterId;
    private NetworkSnapshot _snapshot;
    private bool _disposed;

    public NetworkTelemetryService() : this(new NativeNetworkCounterSource(), TimeProvider.System) { }
    internal NetworkTelemetryService(INetworkCounterSource source, TimeProvider clock)
    {
        _source = source;
        _clock = clock;
        _snapshot = new(NetworkStatus.NoAdapter, null, null, null, null, null, null, null, [], [], clock.GetUtcNow());
    }

    public NetworkSnapshot Snapshot => Volatile.Read(ref _snapshot);
    /// <summary>Null selects Auto. An explicit missing/down adapter never falls back to another one.</summary>
    public string? PreferredAdapterId
    {
        get => Volatile.Read(ref _preferredAdapterId);
        set => Volatile.Write(ref _preferredAdapterId, string.IsNullOrWhiteSpace(value) ? null : value);
    }

    public Task<NetworkSnapshot> SampleAsync(CancellationToken cancellationToken = default)
        => Task.Run(() =>
        {
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
                linked.Token.ThrowIfCancellationRequested();
                return SampleCore(linked.Token);
            }
        }, cancellationToken);

    private NetworkSnapshot SampleCore(CancellationToken token)
    {
        IReadOnlyList<NetworkAdapterReading> readings;
        try { readings = _source.Read(token); }
        catch (OperationCanceledException) { throw; }
        catch (PlatformNotSupportedException) { return Unavailable(NetworkStatus.Unsupported); }
        catch (Exception error) when (error is System.Net.NetworkInformation.NetworkInformationException
            or System.ComponentModel.Win32Exception or InvalidOperationException or UnauthorizedAccessException
            or System.Security.SecurityException)
        { return Unavailable(NetworkStatus.ReadError); }
        token.ThrowIfCancellationRequested();
        // A broken/custom source cannot grow state without limit or double-count an ID.
        if (readings.Count > AdapterLimit) return Unavailable(NetworkStatus.ReadError);
        var unique = readings.Where(reading => !string.IsNullOrWhiteSpace(reading.Adapter.Id))
            .GroupBy(reading => reading.Adapter.Id, StringComparer.OrdinalIgnoreCase).Select(group => group.First()).ToArray();
        var now = _clock.GetTimestamp();
        var sampledAt = _clock.GetUtcNow();
        var seen = new HashSet<string>(unique.Select(reading => reading.Adapter.Id), StringComparer.OrdinalIgnoreCase);
        foreach (var old in _sessions.Where(pair => !seen.Contains(pair.Key))) old.Value.Invalidate();
        foreach (var reading in unique)
        {
            token.ThrowIfCancellationRequested();
            if (!_sessions.TryGetValue(reading.Adapter.Id, out var session))
            {
                if (_sessions.Count >= AdapterLimit)
                {
                    var expired = _sessions.FirstOrDefault(pair => !seen.Contains(pair.Key));
                    if (expired.Key is null) continue;
                    _sessions.Remove(expired.Key);
                }
                _sessions.Add(reading.Adapter.Id, session = new AdapterSession());
            }
            session.Observe(reading, now, _clock);
        }
        var preferred = PreferredAdapterId;
        var selected = preferred is not null
            ? unique.FirstOrDefault(reading => string.Equals(reading.Adapter.Id, preferred, StringComparison.OrdinalIgnoreCase))
            : unique.Where(reading => reading.Adapter.IsOperational && reading.Adapter.IsPhysical)
                .OrderByDescending(reading => reading.DefaultRouteMetric.HasValue)
                .ThenBy(reading => reading.DefaultRouteMetric ?? uint.MaxValue)
                .ThenByDescending(reading => reading.Adapter.HasDefaultGateway)
                .ThenByDescending(reading => reading.Adapter.Id == _lastAutomaticAdapterId)
                .ThenBy(reading => reading.Adapter.Id, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
        if (preferred is null) _lastAutomaticAdapterId = selected?.Adapter.Id;
        var id = selected?.Adapter.Id ?? preferred;
        _sessions.TryGetValue(id ?? "", out var selectedSession);
        var status = selected is null ? preferred is null ? NetworkStatus.NoAdapter : NetworkStatus.AdapterMissing
            : !selected.Adapter.IsOperational ? NetworkStatus.AdapterDown
            : selected.ReceivedBytes is null || selected.SentBytes is null ? NetworkStatus.ReadError
            : selectedSession?.DownloadRate is null ? NetworkStatus.WarmingUp : NetworkStatus.Ready;
        var adapters = unique.Select(reading => reading.Adapter).OrderBy(adapter => adapter.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        return Publish(status, id, selected?.Adapter.Name, selected?.Adapter.LinkSpeedBitsPerSecond,
            status == NetworkStatus.Ready ? selectedSession?.DownloadRate : null,
            status == NetworkStatus.Ready ? selectedSession?.UploadRate : null,
            selectedSession?.Received, selectedSession?.Sent, Array.AsReadOnly(adapters), sampledAt);
    }

    private NetworkSnapshot Unavailable(NetworkStatus status)
    {
        foreach (var session in _sessions.Values) session.Invalidate();
        var id = PreferredAdapterId ?? Snapshot.AdapterId;
        _sessions.TryGetValue(id ?? "", out var previous);
        return Publish(status, id, id == Snapshot.AdapterId ? Snapshot.AdapterName : null, null,
            null, null, previous?.Received, previous?.Sent, [], _clock.GetUtcNow());
    }

    private NetworkSnapshot Publish(NetworkStatus status, string? id, string? name, long? linkSpeed,
        double? down, double? up, ulong? received, ulong? sent, IReadOnlyList<NetworkAdapterInfo> adapters, DateTimeOffset sampledAt)
    {
        var timestamp = _clock.GetTimestamp();
        if (!string.Equals(_historyAdapterId, id, StringComparison.OrdinalIgnoreCase)
            || _history.Count == 0 || _clock.GetElapsedTime(_historyEpoch, timestamp) < TimeSpan.Zero)
        { _history.Clear(); _historyAdapterId = id; _historyEpoch = timestamp; }
        // One actual observation per second bucket. Extra UI-triggered reads must
        // not turn a 60-second chart into a 60-click chart; absent seconds stay absent.
        var bucket = (long)Math.Floor(_clock.GetElapsedTime(_historyEpoch, timestamp).TotalSeconds);
        var point = new NetworkHistorySample(sampledAt, down, up);
        if (_history.Count > 0 && _history[^1].Bucket == bucket) _history[^1] = (bucket, timestamp, point);
        else _history.Add((bucket, timestamp, point));
        _history.RemoveAll(entry => _clock.GetElapsedTime(entry.Timestamp, timestamp).TotalSeconds >= 60);
        if (_history.Count > HistoryLimit) _history.RemoveRange(0, _history.Count - HistoryLimit);
        var snapshot = new NetworkSnapshot(status, id, name, linkSpeed, down, up, received, sent,
            adapters, Array.AsReadOnly(_history.Select(entry => entry.Sample).ToArray()), sampledAt);
        Volatile.Write(ref _snapshot, snapshot);
        return snapshot;
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
        }
        // Keep the cancellation source alive for already-queued sample operations.
    }

    private sealed class AdapterSession
    {
        public ulong Received { get; private set; }
        public ulong Sent { get; private set; }
        public double? DownloadRate { get; private set; }
        public double? UploadRate { get; private set; }
        private ulong? _lastReceived;
        private ulong? _lastSent;
        private long _lastTimestamp;

        public void Observe(NetworkAdapterReading reading, long timestamp, TimeProvider clock)
        {
            if (reading.Adapter.IsOperational && _lastReceived is not null && timestamp == _lastTimestamp
                && _lastReceived == reading.ReceivedBytes && _lastSent == reading.SentBytes) return;
            DownloadRate = UploadRate = null;
            if (!reading.Adapter.IsOperational || reading.ReceivedBytes is null || reading.SentBytes is null)
            { Invalidate(); return; }
            var received = reading.ReceivedBytes.Value;
            var sent = reading.SentBytes.Value;
            if (_lastReceived is { } beforeReceived && _lastSent is { } beforeSent
                && received >= beforeReceived && sent >= beforeSent)
            {
                var elapsed = clock.GetElapsedTime(_lastTimestamp, timestamp).TotalSeconds;
                if (elapsed > 0)
                {
                    var deltaReceived = received - beforeReceived;
                    var deltaSent = sent - beforeSent;
                    Received = SaturatingAdd(Received, deltaReceived);
                    Sent = SaturatingAdd(Sent, deltaSent);
                    // Long gaps/suspend still count valid counter deltas in session
                    // totals, but an old average must not masquerade as a live rate.
                    if (elapsed <= MaximumRateIntervalSeconds)
                    { DownloadRate = deltaReceived / elapsed; UploadRate = deltaSent / elapsed; }
                }
            }
            _lastReceived = received;
            _lastSent = sent;
            _lastTimestamp = timestamp;
        }
        public void Invalidate() { _lastReceived = _lastSent = null; DownloadRate = UploadRate = null; }
        private static ulong SaturatingAdd(ulong total, ulong delta) => ulong.MaxValue - total < delta ? ulong.MaxValue : total + delta;
    }
}
