using System.Text;
using System.Text.Json;
using GlimHUD.Services;

var passed = 0;
void Check(bool value, string message)
{
    if (!value) throw new InvalidOperationException(message);
    passed++;
}
static string Group(params string[] ids) => "GlimHUD.AudioGroup.v1:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(ids)));
static async Task Until(Func<bool> condition)
{
    var deadline = DateTime.UtcNow.AddSeconds(3);
    while (!condition())
    {
        if (DateTime.UtcNow >= deadline) throw new TimeoutException("Selected meter check exceeded its bound.");
        await Task.Delay(10);
    }
}

const string a = "endpoint-A\u001fsession-A";
const string b = "endpoint-B\u001fsession-B";
var single = AudioLevelRules.DecodeTargets(a);
Check(single is { Count: 1 } && single[0].DeviceId == "endpoint-A" && single[0].InstanceId == "session-A",
    "Meter targets preserve the exact endpoint and session instance.");
Check(AudioLevelRules.DecodeTargets(Group(a, b)) is { Count: 2 }, "Verified multi-stream target IDs are accepted.");
foreach (var bad in new[] { "", "master", "default", "endpoint\u001f", "\u001fsession", "GlimHUD.AudioGroup.v1:???", Group(a, a), Group(a, "master"), Group(a) })
    Check(AudioLevelRules.DecodeTargets(bad) is null, "Malformed targets cannot fall back to an endpoint meter.");
Check(AudioLevelRules.DecodeTargets(Group(Enumerable.Range(0, 65).Select(i => $"device\u001f{i}").ToArray())) is null,
    "Selected groups are bounded to 64 session instances.");
Check(AudioLevelRules.AudiblePeak(.8f, true, 1, true) == 0 && AudioLevelRules.AudiblePeak(.8f, false, 0, true) == 0
    && AudioLevelRules.AudiblePeak(.8f, false, 1, false) == 0, "Mute, zero volume and inactive sessions are silent.");
Check(AudioLevelRules.ValidPeak(float.NaN) == 0 && AudioLevelRules.ValidPeak(-1) == 0 && AudioLevelRules.ValidPeak(2) == 1,
    "Invalid peaks cannot create bright or unbounded output.");

var factory = new MemoryFactory();
var callerThread = Environment.CurrentManagedThreadId;
using (var monitor = new AudioLevelMonitor(factory))
{
    Check(factory.OpenCount == 0 && !monitor.Snapshot.IsAvailable, "Disabled construction opens no audio API or worker.");
    monitor.Configure(true, "master");
    await Task.Delay(70);
    Check(factory.OpenCount == 0 && monitor.Snapshot.Peak == 0, "An invalid target never opens a reader.");
    monitor.Configure(true, a);
    await Until(() => monitor.Snapshot.IsAvailable && factory.ReadCount >= 3);
    Check(monitor.Snapshot is { SessionId: a, Peak: .25f }, "The configured session supplies the signal.");
    Check(factory.OpenCount == 1, "Fast peak polling reuses selected meter handles instead of enumerating each tick.");
    Check(factory.WorkerThreads.Distinct().Count() == 1 && factory.WorkerThreads.All(id => id != callerThread),
        "Opening and reading selected audio handles stay on one dedicated worker.");
    monitor.Configure(true, b);
    Check(monitor.Snapshot.Peak == 0 && monitor.Snapshot.SessionId == b, "Switching sources clears the prior peak immediately.");
    await Until(() => monitor.Snapshot is { IsAvailable: true, SessionId: b });
    Check(monitor.Snapshot.Peak == .75f && factory.LastTargets?.Single().Id == b, "Source changes resolve only the new exact target.");
    monitor.Configure(false, b);
    Check(monitor.Snapshot.Peak == 0 && !monitor.Snapshot.IsAvailable, "Disabling clears the signal immediately.");
    await Until(() => factory.DisposeCount == 2);
    var reads = factory.ReadCount;
    await Task.Delay(80);
    Check(factory.ReadCount == reads, "Disabled metering does not continue polling.");
    Check(factory.WorkerThreads.Distinct().Count() == 1, "Selected handle cleanup occurs on the same worker as acquisition.");
    monitor.Dispose();
    monitor.Dispose();
    monitor.Configure(true, a);
    Check(!monitor.Snapshot.IsAvailable && monitor.Snapshot.Peak == 0, "Disposed monitors cannot restart or publish stale signal.");
}

var blocked = new BlockingFactory();
using (var monitor = new AudioLevelMonitor(blocked))
{
    monitor.Configure(true, a);
    await Until(() => blocked.Started.IsSet);
    monitor.Configure(false, a);
    blocked.Continue.Set();
    await Until(() => blocked.Disposed);
    Check(monitor.Snapshot.Peak == 0 && !monitor.Snapshot.IsAvailable, "A read already in flight cannot republish after disable.");
    Check(monitor.ReadFrame() is { Peak: 0, IsAvailable: false }, "A disabled frame cannot consume a read already in flight.");
}

var controlled = new ControlledFactory();
var clock = new ManualClock();
using (var monitor = new AudioLevelMonitor(controlled, clock))
{
    monitor.Configure(true, a);
    await Until(() => monitor.Snapshot.IsAvailable);

    async Task Pulse(float peak)
    {
        controlled.Set(peak);
        await Until(() => monitor.Snapshot.Peak == peak);
        clock.Advance(TimeSpan.FromMilliseconds(10));
        controlled.Set(0);
        await Until(() => monitor.Snapshot is { IsAvailable: true, Peak: 0 } && monitor.Snapshot.SampledAt == clock.GetUtcNow());
    }

    await Pulse(.9f);
    var frame = monitor.ReadFrame();
    Check(frame is { IsAvailable: true, SessionId: a, Peak: .9f } && frame.SampledAt < monitor.Snapshot.SampledAt,
        "A short attack survives a newer quiet poll and keeps the attack timestamp.");
    Check(monitor.Snapshot.Peak == 0, "Consuming a UI frame does not replace the latest settings-meter snapshot.");
    Check(monitor.ReadFrame().Peak == 0, "A consumed attack is not replayed on the next frame.");

    controlled.Set(.4f);
    await Until(() => monitor.Snapshot.Peak == .4f);
    Check(monitor.ReadFrame().Peak == .4f && monitor.ReadFrame().Peak == .4f,
        "With no new samples, a frame falls back to the latest ongoing signal.");
    await Pulse(.8f);
    clock.Advance(TimeSpan.FromMilliseconds(121));
    Check(monitor.ReadFrame().Peak == 0, "An unconsumed attack expires rather than replaying after a delayed UI frame.");

    await Pulse(.75f);
    monitor.Configure(true, b);
    Check(monitor.ReadFrame() is { SessionId: b, Peak: 0 }, "A source change clears an unconsumed peak immediately.");
    await Until(() => monitor.Snapshot is { SessionId: b, IsAvailable: true });
    await Pulse(.65f);
    monitor.Configure(false, b);
    Check(monitor.ReadFrame() is { IsAvailable: false, Peak: 0 }, "Disabling clears an unconsumed peak immediately.");

    monitor.Configure(true, a);
    await Until(() => monitor.Snapshot.IsAvailable);
    await Pulse(.95f);
    controlled.Set(.95f, audibleSessions: 0);
    clock.Advance(TimeSpan.FromMilliseconds(1));
    await Until(() => monitor.Snapshot.Peak == 0 && monitor.Snapshot.SampledAt == clock.GetUtcNow());
    Check(monitor.ReadFrame().Peak == 0, "A mute, zero volume or inactive session clears its earlier audible peak.");

    controlled.Set(.85f);
    await Until(() => monitor.Snapshot.Peak == .85f);
    controlled.Set(0, audibleSessions: 2);
    clock.Advance(TimeSpan.FromMilliseconds(1));
    await Until(() => monitor.Snapshot.Peak == 0 && monitor.Snapshot.SampledAt == clock.GetUtcNow());
    Check(monitor.ReadFrame().Peak == 0, "Changing the audible members of a selected group clears peaks from its earlier stream state.");

    await Pulse(.7f);
    controlled.Set(0, available: false);
    await Until(() => !monitor.Snapshot.IsAvailable);
    Check(monitor.ReadFrame() is { IsAvailable: false, Peak: 0 }, "An unavailable selected session cannot replay a retained attack.");
    Check(controlled.OpenCount == 3, "Faster transient sampling retains cached readers across audio frames.");
}
Console.WriteLine($"{passed} selected audio meter checks passed; no real application audio was read or changed.");

sealed class MemoryFactory : IAudioLevelReaderFactory
{
    public int OpenCount, ReadCount, DisposeCount;
    public IReadOnlyList<AudioLevelTarget>? LastTargets;
    public readonly System.Collections.Concurrent.ConcurrentBag<int> WorkerThreads = [];
    public IAudioLevelReader Open(IReadOnlyList<AudioLevelTarget> targets)
    {
        Interlocked.Increment(ref OpenCount);
        LastTargets = targets;
        WorkerThreads.Add(Environment.CurrentManagedThreadId);
        return new Reader(this, targets[0].DeviceId == "endpoint-A" ? .25f : .75f);
    }
    private sealed class Reader(MemoryFactory owner, float value) : IAudioLevelReader
    {
        public bool TryRead(out AudioLevelReading reading)
        {
            owner.WorkerThreads.Add(Environment.CurrentManagedThreadId);
            Interlocked.Increment(ref owner.ReadCount);
            reading = new(value, 1);
            return true;
        }
        public void Dispose()
        {
            owner.WorkerThreads.Add(Environment.CurrentManagedThreadId);
            Interlocked.Increment(ref owner.DisposeCount);
        }
    }
}

sealed class BlockingFactory : IAudioLevelReaderFactory
{
    public ManualResetEventSlim Started = new(false), Continue = new(false);
    public volatile bool Disposed;
    public IAudioLevelReader Open(IReadOnlyList<AudioLevelTarget> targets) => new Reader(this);
    private sealed class Reader(BlockingFactory owner) : IAudioLevelReader
    {
        public bool TryRead(out AudioLevelReading reading)
        {
            owner.Started.Set();
            if (!owner.Continue.Wait(1500)) throw new TimeoutException();
            reading = new(1, 1);
            return true;
        }
        public void Dispose() => owner.Disposed = true;
    }
}

sealed class ControlledFactory : IAudioLevelReaderFactory
{
    private readonly object _sync = new();
    private AudioLevelReading _reading = new(0, 1);
    private bool _available = true;
    public int OpenCount;

    public void Set(float peak, ulong audibleSessions = 1, bool available = true)
    {
        lock (_sync)
        {
            _reading = new(peak, audibleSessions);
            _available = available;
        }
    }

    public IAudioLevelReader Open(IReadOnlyList<AudioLevelTarget> targets)
    {
        Interlocked.Increment(ref OpenCount);
        return new Reader(this);
    }

    private sealed class Reader(ControlledFactory owner) : IAudioLevelReader
    {
        public bool TryRead(out AudioLevelReading reading)
        {
            lock (owner._sync)
            {
                reading = owner._reading;
                return owner._available;
            }
        }
        public void Dispose() { }
    }
}

sealed class ManualClock : TimeProvider
{
    private long _ticks = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero).Ticks;
    public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);
    public void Advance(TimeSpan amount) => Interlocked.Add(ref _ticks, amount.Ticks);
}
