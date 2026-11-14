namespace GlimHUD.Models;

public enum NetworkStatus { Ready, WarmingUp, NoAdapter, AdapterMissing, AdapterDown, ReadError, Unsupported }

public sealed record NetworkAdapterInfo(string Id, string Name, string Description,
    bool IsOperational, bool IsPhysical, bool HasDefaultGateway, long? LinkSpeedBitsPerSecond);

public sealed record NetworkHistorySample(DateTimeOffset SampledAt,
    double? DownloadBytesPerSecond, double? UploadBytesPerSecond);

/// <summary>
/// Traffic for one adapter, including all applications and local-network traffic.
/// Session totals start at the adapter's first observation in this process, never
/// at boot. Unmeasurable rates are null; a measured idle connection has zero rates.
/// </summary>
public sealed record NetworkSnapshot(NetworkStatus Status, string? AdapterId, string? AdapterName,
    long? LinkSpeedBitsPerSecond, double? DownloadBytesPerSecond, double? UploadBytesPerSecond,
    ulong? SessionReceivedBytes, ulong? SessionSentBytes,
    IReadOnlyList<NetworkAdapterInfo> AvailableAdapters, IReadOnlyList<NetworkHistorySample> History,
    DateTimeOffset SampledAt);
