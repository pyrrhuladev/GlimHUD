using System.Diagnostics;

namespace GlimHUD.Services.Fps;

/// <summary>
/// A one-second source-time window, separately for every process/swap chain.
/// Uses ETW timestamps rather than stdout arrival times, which can arrive in batches.
/// </summary>
internal sealed class PresentMonFrameAccumulator
{
    private const int MaximumStreams = 256;
    private const int MaximumSamplesPerStream = 8192;
    private readonly Dictionary<(int ProcessId, string SwapChain), FrameWindow> _streams = new();
    private long _lastPrune;

    public void Add(PresentMonSample sample, long receivedTimestamp)
    {
        var key = (sample.ProcessId, sample.SwapChain);
        if (!_streams.TryGetValue(key, out var window))
        {
            if (_streams.Count >= MaximumStreams)
            {
                var oldest = _streams.MinBy(pair => pair.Value.LastReceivedTimestamp).Key;
                _streams.Remove(oldest);
            }
            _streams[key] = window = new FrameWindow();
        }
        if (window.Samples.TryPeek(out _) && sample.TimeMilliseconds < window.LastTimeMilliseconds)
            window.Samples.Clear(); // PID/swap-chain reuse or capture timestamp reset.
        window.LastTimeMilliseconds = sample.TimeMilliseconds;
        window.LastReceivedTimestamp = receivedTimestamp;
        window.Samples.Enqueue(sample);
        while (window.Samples.Count > MaximumSamplesPerStream ||
               (window.Samples.TryPeek(out var first) && first.TimeMilliseconds < sample.TimeMilliseconds - 1000))
            window.Samples.Dequeue();

        if (ElapsedSeconds(_lastPrune, receivedTimestamp) < 10) return;
        foreach (var expired in _streams.Where(pair => ElapsedSeconds(pair.Value.LastReceivedTimestamp, receivedTimestamp) > 10).Select(pair => pair.Key).ToArray())
            _streams.Remove(expired);
        _lastPrune = receivedTimestamp;
    }

    public double? GetFramesPerSecond(int processId, long now)
    {
        // Pick the dominant active swap chain; summing swap chains double-counts
        // games that separately present their scene, launcher, or auxiliary window.
        var stream = _streams
            .Where(pair => pair.Key.ProcessId == processId && ElapsedSeconds(pair.Value.LastReceivedTimestamp, now) <= 2.5)
            .Select(pair => pair.Value)
            .Where(window => window.Samples.Count >= 2)
            .OrderByDescending(window => window.Samples.Count)
            .FirstOrDefault();
        if (stream is null) return null;
        var duration = stream.Samples.Sum(sample => sample.FrameMilliseconds);
        return duration > 0 ? 1000 * stream.Samples.Count / duration : null;
    }

    public void Clear() => _streams.Clear();

    private static double ElapsedSeconds(long from, long to) => (double)(to - from) / Stopwatch.Frequency;

    private sealed class FrameWindow
    {
        public Queue<PresentMonSample> Samples { get; } = new();
        public double LastTimeMilliseconds { get; set; }
        public long LastReceivedTimestamp { get; set; }
    }
}
