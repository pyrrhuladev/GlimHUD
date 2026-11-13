using GlimHUD.Services;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

if (args.FirstOrDefault() == "--diagnostics")
{
    using var service = new MediaSessionService();
    using var token = new CancellationTokenSource(TimeSpan.FromSeconds(20));
    var automatic = await service.GetSnapshotAsync(cancellationToken: token.Token);
    var sources = new List<object>();
    foreach (var source in automatic.Sessions)
    {
        var snapshot = await service.GetSnapshotAsync(source.SourceAppId, token.Token);
        sources.Add(new { source.SourceAppId, source.IsPlaying, snapshot.IsAvailable,
            ArtworkBytes = snapshot.ArtworkBytes?.Length ?? 0, snapshot.Status });
    }
    var json = JsonSerializer.Serialize(new { SelectedSource = automatic.SourceAppId,
        automatic.IsPlaying, ArtworkBytes = automatic.ArtworkBytes?.Length ?? 0,
        automatic.Status, Sources = sources }, new JsonSerializerOptions { WriteIndented = true });
    if (args.Length > 1) File.WriteAllText(args[1], json);
    else Console.WriteLine(json);
    return 0;
}

// Integration smoke checks against the real Windows APIs. Commands always name a
// nonexistent, random explicit source/session and must never affect playback.
var missingSource = "GlimHUD.MediaChecks.Missing." + Guid.NewGuid().ToString("N");
var missingSession = "GlimHUD.MediaChecks.MissingSession." + Guid.NewGuid().ToString("N");
var passed = 0;

void Check([DoesNotReturnIf(false)] bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    Console.WriteLine("PASS " + message);
    passed++;
}

try
{
    var policy = new MediaSessionSelectionPolicy();
    Check(policy.Select([new("browser", false, true), new("music", true, false)], null) == 1,
        "Playing music outranks Windows' stale paused browser session.");
    Check(policy.Select([new("browser", false, true), new("music", false, false)], null) == 1,
        "Pausing music retains its metadata instead of jumping to a stale video.");
    Check(policy.Select([new("browser", true, true), new("music", false, false)], null) == 0,
        "A newly playing source can replace the last paused source.");
    Check(policy.Select([new("browser", true, true), new("music", false, false)], "music") == 1,
        "An explicit paused source remains selected over other playing applications.");
    Check(policy.Select([new("browser", true, true)], "missing") is null,
        "An explicit missing source never falls back to another player.");
    Check(new MediaSessionSelectionPolicy().Select([], null) is null,
        "An empty media list is unavailable.");
    Check(MediaSessionService.IsDedicatedMusicPlayer("ru.yandex.desktop.music")
        && MediaSessionService.IsDedicatedMusicPlayer("SpotifyAB.SpotifyMusic_1.2.3_x64__publisher!Spotify")
        && !MediaSessionService.IsDedicatedMusicPlayer("308046B0AF4A39CB"),
        "Known desktop music identities are distinguished from an opaque browser identity.");
    var musicPolicy = new MediaSessionSelectionPolicy();
    Check(musicPolicy.Select([new("308046B0AF4A39CB", true, true), new("ru.yandex.desktop.music", true, false, true)], null) == 1,
        "Yandex Music wins when a stale Firefox session also reports Playing.");
    Check(musicPolicy.Select([new("308046B0AF4A39CB", true, true), new("ru.yandex.desktop.music", false, false, true, true)], null) == 1,
        "The last music player stays visible while paused despite stale browser Playing.");
    Check(musicPolicy.Select([new("browser", true, true), new("ru.yandex.desktop.music", false, false, true, true), new("spotify", true, false, true)], null) == 2,
        "Another actively playing music app can replace the last paused music source.");
    Check(new MediaSessionSelectionPolicy().Select([new("browser", true, true), new("music", false, false, true, true)], null) == 0,
        "An initially paused music app does not hide another active source.");
    Check(new MediaSessionSelectionPolicy().Select([new("browser", false, true), new("ru.yandex.desktop.music", false, false, true, true)], null) == 1,
        "A fresh all-paused startup prefers the music player over a stale browser video.");
    Check(musicPolicy.Select([new("browser", true, true), new("spotify", false, false, true, false)], null) == 0,
        "A stopped or invalid previous music source does not suppress another active source.");
    Check(musicPolicy.Select([new("browser", true, true), new("ru.yandex.desktop.music", true, false, true)], "browser") == 0,
        "Explicit browser selection overrides automatic dedicated-music priority.");

    var cache = new MediaArtworkCache();
    var now = DateTimeOffset.UtcNow;
    var failed = cache.BeginRead("track-a", now)!;
    cache.Complete(failed, null, now);
    Check(cache.GetData("track-a") is null && cache.BeginRead("track-a", now.AddMilliseconds(500)) is null,
        "Missing artwork backs off instead of being cached as a permanent result.");
    var retry = cache.BeginRead("track-a", now.AddSeconds(1));
    Check(retry is not null, "The same track retries a missing artwork thumbnail.");
    byte[] firstArt = [1, 2, 3];
    cache.Complete(retry!, firstArt, now.AddSeconds(1));
    Check(ReferenceEquals(cache.GetData("track-a"), firstArt)
        && cache.BeginRead("track-a", now.AddSeconds(2)) is null,
        "Successful artwork is reused without downloading on every poll.");
    var fallbackRefresh = cache.BeginRead("track-a", now.AddSeconds(31));
    Check(fallbackRefresh is not null, "Artwork eventually refreshes even if a player misses its property event.");
    cache.Complete(fallbackRefresh!, [1, 2, 3], now.AddSeconds(31));
    Check(ReferenceEquals(cache.GetData("track-a"), firstArt),
        "Identical refreshed artwork preserves the decoded-image cache identity.");
    cache.Invalidate();
    var stale = cache.BeginRead("track-a", now.AddSeconds(32))!;
    cache.Invalidate();
    cache.Complete(stale, [9, 9], now.AddSeconds(32));
    Check(ReferenceEquals(cache.GetData("track-a"), firstArt),
        "A late read cannot overwrite an artwork property invalidation.");
    var changedTrack = cache.BeginRead("track-b", now.AddSeconds(32));
    Check(changedTrack is not null && cache.GetData("track-b") is null,
        "Switching source or track clears the previous artwork immediately.");
    cache.Complete(stale, [9, 9], now.AddSeconds(32));
    Check(cache.GetData("track-b") is null, "Late artwork from a previous track is rejected.");
    cache.Complete(changedTrack!, [4, 5], now.AddSeconds(32));
    cache.Clear();
    Check(cache.GetData("track-b") is null, "An unavailable media source clears its artwork.");

    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    using var media = new MediaSessionService();
    using var audio = new AudioSessionService();

    var selectedMissing = await media.GetSnapshotAsync(missingSource, timeout.Token)
        .WaitAsync(TimeSpan.FromSeconds(15));
    Check(!selectedMissing.IsAvailable && selectedMissing.SourceAppId is null,
        "Missing selected media source is unavailable without selecting another player.");
    Check(!selectedMissing.CanTogglePlayback && !selectedMissing.CanPrevious && !selectedMissing.CanNext,
        "Unavailable media has no enabled transport commands.");
    Check(!await media.TogglePlaybackAsync(missingSource, timeout.Token),
        "Toggle playback refuses a missing explicit source.");
    Check(!await media.PreviousAsync(missingSource, timeout.Token),
        "Previous track refuses a missing explicit source.");
    Check(!await media.NextAsync(missingSource, timeout.Token),
        "Next track refuses a missing explicit source.");

    var current = await media.GetSnapshotAsync(cancellationToken: timeout.Token)
        .WaitAsync(TimeSpan.FromSeconds(15));
    Check(current.Sessions is not null && current.Title is not null && current.Artist is not null,
        "Windows media enumeration produces a valid snapshot.");
    Check(current.IsAvailable == !string.IsNullOrWhiteSpace(current.SourceAppId),
        "Availability agrees with the selected media source identity.");
    if (current.Sessions.Count == 0)
        Check(!current.IsAvailable && !string.IsNullOrWhiteSpace(current.Status),
            "No media player produces a controlled unavailable state.");

    var missingAudio = await audio.GetSnapshotAsync(current.SourceAppId, missingSession, timeout.Token)
        .WaitAsync(TimeSpan.FromSeconds(15));
    Check(!missingAudio.IsAvailable && missingAudio.SessionId is null
          && !string.IsNullOrWhiteSpace(missingAudio.Status),
        "Missing selected audio session is unavailable without falling back.");
    Check(missingAudio.Sessions.All(session => session.ProcessId > 0
              && !string.IsNullOrWhiteSpace(session.Id)
              && float.IsFinite(session.Volume) && session.Volume >= 0 && session.Volume <= 1),
        "Enumerated audio sessions have valid identities and bounded volumes.");
    Check(missingAudio.Sessions.Select(session => session.Id).Distinct().Count() == missingAudio.Sessions.Count,
        "Audio session identities are unique across active endpoints.");
    Check(!await audio.SetVolumeAsync(0.223f, current.SourceAppId, missingSession, timeout.Token),
        "Volume change refuses an impossible explicit audio session.");
    Check(!await audio.SetMutedAsync(true, current.SourceAppId, missingSession, timeout.Token),
        "Mute refuses an impossible explicit audio session.");
    Check(!await audio.SetVolumeAsync(float.NaN, missingSource, null, timeout.Token),
        "Non-finite volume is rejected without a mutation.");

    var automaticAudio = await audio.GetSnapshotAsync(current.SourceAppId, cancellationToken: timeout.Token)
        .WaitAsync(TimeSpan.FromSeconds(15));
    Check(automaticAudio.IsAvailable
            ? automaticAudio.SessionId is not null && float.IsFinite(automaticAudio.Volume)
            : !string.IsNullOrWhiteSpace(automaticAudio.Status),
        "Automatic audio lookup returns a valid result or controlled unavailable state.");

    Console.WriteLine($"{passed} checks passed. Media sources: {current.Sessions.Count}; audio sessions: {missingAudio.Sessions.Count}.");
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine("FAIL " + exception.GetType().Name + ": " + exception.Message);
    return 1;
}
