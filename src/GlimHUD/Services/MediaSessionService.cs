using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Media.Control;
using Windows.Storage.Streams;
using GlimHUD.Localization;

namespace GlimHUD.Services;

public sealed record MediaSourceInfo(string SourceAppId, string DisplayName, bool IsPlaying);

public sealed record MediaSnapshot(
    string? SourceAppId,
    string Title,
    string Artist,
    bool IsAvailable,
    bool IsPlaying,
    bool CanTogglePlayback,
    bool CanPrevious,
    bool CanNext,
    byte[]? ArtworkBytes,
    IReadOnlyList<MediaSourceInfo> Sessions,
    string Status);

/// <summary>Controls only sessions exposed by Windows' system media transport controls.</summary>
public sealed class MediaSessionService : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private readonly MediaSessionSelectionPolicy _selection = new();
    private readonly MediaArtworkCache _artwork = new();
    private GlobalSystemMediaTransportControlsSession? _observedSession;
    private string? _observedSourceAppId;
    private bool _disposed;

    public async Task<MediaSnapshot> GetSnapshotAsync(
        string? preferredSourceAppId = null,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        IReadOnlyList<MediaSourceInfo> knownSources = Array.Empty<MediaSourceInfo>();
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var manager = await GetManagerAsync();
            cancellationToken.ThrowIfCancellationRequested();
            var sessions = manager.GetSessions();
            var sources = sessions
                .GroupBy(session => session.SourceAppUserModelId, StringComparer.OrdinalIgnoreCase)
                .Select(group => new MediaSourceInfo(
                    group.Key, GetDisplayName(group.Key), group.Any(IsPlaying)))
                .OrderByDescending(source => source.IsPlaying)
                .ThenBy(source => source.DisplayName)
                .ToArray();
            knownSources = sources;
            var session = SelectSession(manager, preferredSourceAppId);
            if (session is null)
            {
                ObserveSession(null);
                _artwork.Clear();
                return Unavailable(sources, string.IsNullOrEmpty(preferredSourceAppId)
                    ? L10n.Text("Start playing music in your player", "Запустите музыку в плеере")
                    : L10n.Text("The selected player is unavailable", "Выбранный плеер недоступен"));
            }

            ObserveSession(session);
            var playback = session.GetPlaybackInfo();
            var media = await session.TryGetMediaPropertiesAsync();
            cancellationToken.ThrowIfCancellationRequested();
            var sourceAppId = session.SourceAppUserModelId;
            var title = string.IsNullOrWhiteSpace(media.Title) ? GetDisplayName(sourceAppId) : media.Title;
            var artist = string.IsNullOrWhiteSpace(media.Artist) ? GetDisplayName(sourceAppId) : media.Artist;
            // Players can publish metadata before a thumbnail is available.
            var artworkKey = string.Join('\u001f', sourceAppId, media.Title, media.Artist, media.AlbumTitle);
            var request = _artwork.BeginRead(artworkKey, DateTimeOffset.UtcNow);
            if (request is not null)
            {
                byte[]? bytes = null;
                try
                {
                    if (media.Thumbnail is not null)
                    {
                        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                        timeout.CancelAfter(TimeSpan.FromSeconds(2));
                        using var stream = await media.Thumbnail.OpenReadAsync().AsTask(timeout.Token);
                        bytes = await ReadArtworkAsync(stream, timeout.Token);
                    }
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
                catch (Exception exception) when (exception is not OperationCanceledException) { }
                cancellationToken.ThrowIfCancellationRequested();
                _artwork.Complete(request, bytes, DateTimeOffset.UtcNow);
            }

            var controls = playback.Controls;
            var playing = playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            return new MediaSnapshot(
                sourceAppId, title, artist, true, playing,
                controls.IsPlayPauseToggleEnabled || (playing ? controls.IsPauseEnabled : controls.IsPlayEnabled),
                controls.IsPreviousEnabled, controls.IsNextEnabled, _artwork.GetData(artworkKey), sources, "");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            _manager = null; // A disconnected Windows session manager can be requested again next poll.
            ObserveSession(null);
            _artwork.Clear();
            return Unavailable(knownSources, GetFailureStatus(exception));
        }
        finally { _gate.Release(); }
    }

    public Task<bool> TogglePlaybackAsync(string? preferredSourceAppId = null, CancellationToken cancellationToken = default)
        => RunCommandAsync(preferredSourceAppId, async session =>
        {
            var playback = session.GetPlaybackInfo();
            if (playback.Controls.IsPlayPauseToggleEnabled)
                return await session.TryTogglePlayPauseAsync();
            if (playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                return playback.Controls.IsPauseEnabled && await session.TryPauseAsync();
            return playback.Controls.IsPlayEnabled && await session.TryPlayAsync();
        }, cancellationToken);

    public Task<bool> PreviousAsync(string? preferredSourceAppId = null, CancellationToken cancellationToken = default)
        => RunCommandAsync(preferredSourceAppId,
            async session => session.GetPlaybackInfo().Controls.IsPreviousEnabled && await session.TrySkipPreviousAsync(),
            cancellationToken);

    public Task<bool> NextAsync(string? preferredSourceAppId = null, CancellationToken cancellationToken = default)
        => RunCommandAsync(preferredSourceAppId,
            async session => session.GetPlaybackInfo().Controls.IsNextEnabled && await session.TrySkipNextAsync(),
            cancellationToken);

    private async Task<bool> RunCommandAsync(
        string? sourceAppId,
        Func<GlobalSystemMediaTransportControlsSession, Task<bool>> command,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var session = SelectSession(await GetManagerAsync(), sourceAppId);
            cancellationToken.ThrowIfCancellationRequested();
            return session is not null && await command(session);
        }
        catch (OperationCanceledException) { throw; }
        catch { _manager = null; return false; }
        finally { _gate.Release(); }
    }

    private async Task<GlobalSystemMediaTransportControlsSessionManager> GetManagerAsync()
        => _manager ??= await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();

    private GlobalSystemMediaTransportControlsSession? SelectSession(
        GlobalSystemMediaTransportControlsSessionManager manager, string? preferredSourceAppId)
    {
        var currentSource = manager.GetCurrentSession()?.SourceAppUserModelId;
        var available = manager.GetSessions().Select(session => new { Session = session, Status = PlaybackStatus(session) })
            .Where(item => item.Status != GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed).ToArray();
        var sessions = available.Select(item => item.Session).ToArray();
        var candidates = available.Select(item => new MediaSelectionCandidate(
            item.Session.SourceAppUserModelId,
            item.Status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            string.Equals(item.Session.SourceAppUserModelId, currentSource, StringComparison.OrdinalIgnoreCase),
            IsDedicatedMusicPlayer(item.Session.SourceAppUserModelId),
            item.Status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused)).ToArray();
        var selected = _selection.Select(candidates, preferredSourceAppId);
        return selected is { } index ? sessions[index] : null;
    }

    private static async Task<byte[]?> ReadArtworkAsync(IRandomAccessStreamWithContentType stream, CancellationToken token)
    {
        const uint maximumBytes = 4 * 1024 * 1024;
        if (stream.Size is 0 or > maximumBytes) return null;
        var expected = checked((uint)stream.Size);
        using var reader = new DataReader(stream) { InputStreamOptions = InputStreamOptions.Partial };
        using var output = new MemoryStream((int)expected);
        while (output.Length < expected)
        {
            var count = await reader.LoadAsync(Math.Min(64 * 1024, expected - (uint)output.Length)).AsTask(token);
            token.ThrowIfCancellationRequested();
            if (count == 0) throw new EndOfStreamException("Incomplete media thumbnail.");
            var chunk = new byte[count];
            reader.ReadBytes(chunk);
            output.Write(chunk);
        }
        return output.ToArray();
    }

    private void ObserveSession(GlobalSystemMediaTransportControlsSession? session)
    {
        var sourceAppId = session?.SourceAppUserModelId;
        if (string.Equals(_observedSourceAppId, sourceAppId, StringComparison.OrdinalIgnoreCase)) return;
        if (_observedSession is { } previous)
        {
            try { previous.MediaPropertiesChanged -= OnMediaPropertiesChanged; }
            catch { }
        }
        _observedSession = session;
        _observedSourceAppId = sourceAppId;
        if (session is not null)
        {
            try { session.MediaPropertiesChanged += OnMediaPropertiesChanged; }
            catch { }
        }
        _artwork.Invalidate();
    }

    private void OnMediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args)
    {
        // WinRT can supply a different managed wrapper for the same event sender.
        // A queued notification from the detached source can only request an extra
        // refresh; it cannot install bytes into the current generation.
        if (!_disposed) _artwork.Invalidate();
    }

    private static bool IsPlaying(GlobalSystemMediaTransportControlsSession session)
        => PlaybackStatus(session) == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

    private static GlobalSystemMediaTransportControlsSessionPlaybackStatus PlaybackStatus(GlobalSystemMediaTransportControlsSession session)
    {
        try { return session.GetPlaybackInfo().PlaybackStatus; }
        catch { return GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed; }
    }

    internal static bool IsDedicatedMusicPlayer(string sourceAppId)
    {
        var value = sourceAppId;
        if (value.Contains('!')) value = value.Split('!')[0].Split('_')[0];
        value = Path.GetFileName(value);
        if (value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) value = value[..^4];
        return value.ToLowerInvariant() is "ru.yandex.desktop.music" or "yandexmusic" or "yandex music" or "яндекс музыка"
            or "spotify" or "spotifyab.spotifymusic" or "aimp" or "foobar2000" or "musicbee" or "winamp"
            or "deezer" or "tidal" or "applemusic" or "appleinc.applemusicwin" or "microsoft.zunemusic";
    }

    private static MediaSnapshot Unavailable(IReadOnlyList<MediaSourceInfo> sources, string status)
        => new(null, L10n.Text("Nothing playing", "Нет воспроизведения"), L10n.Text("Open a music player", "Откройте музыкальный плеер"), false, false, false, false, false, null, sources, status);

    private static string GetFailureStatus(Exception exception)
        => exception is UnauthorizedAccessException
            ? L10n.Text("Windows denied access to media sessions", "Windows не разрешила доступ к медиасессиям")
            : L10n.Text("The media session is temporarily unavailable", "Медиасессия временно недоступна");

    internal static string GetDisplayName(string appId)
    {
        var value = appId;
        if (value.Contains('!')) value = value.Split('!')[0].Split('_')[0];
        value = Path.GetFileName(value);
        if (value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) value = value[..^4];
        return value.ToLowerInvariant() switch
        {
            "spotify" or "spotifyab.spotifymusic" => "Spotify",
            "chrome" => "Google Chrome",
            "msedge" => "Microsoft Edge",
            "firefox" => "Firefox",
            "yandex" or "browser" => L10n.Text("Yandex Browser", "Яндекс Браузер"),
            "microsoft.zunemusic" => L10n.Text("Media Player", "Медиаплеер"),
            "ru.yandex.desktop.music" or "yandexmusic" or "yandex music" or "яндекс музыка" => L10n.Text("Yandex Music", "Яндекс Музыка"),
            "vlc" => "VLC",
            "aimp" => "AIMP",
            _ => string.IsNullOrWhiteSpace(value) ? L10n.Text("Player", "Плеер") : value
        };
    }

    public void Dispose()
    {
        _disposed = true;
        _manager = null;
        ObserveSession(null);
        _artwork.Clear();
        // Do not dispose the semaphore while an in-flight WinRT call may still release it.
    }
}

internal sealed record MediaSelectionCandidate(string SourceAppId, bool IsPlaying, bool IsCurrent,
    bool IsMusicPlayer = false, bool IsPaused = false);

/// <summary>Dedicated music outranks stale browser flags; explicit selection always wins.</summary>
internal sealed class MediaSessionSelectionPolicy
{
    private string? _lastSelectedSource;

    internal int? Select(IReadOnlyList<MediaSelectionCandidate> candidates, string? preferredSource)
    {
        var explicitSelection = !string.IsNullOrWhiteSpace(preferredSource);
        var eligible = Enumerable.Range(0, candidates.Count);
        if (explicitSelection)
            eligible = eligible.Where(index => string.Equals(candidates[index].SourceAppId, preferredSource, StringComparison.OrdinalIgnoreCase));
        var indexes = eligible.ToArray();
        if (indexes.Length == 0) return null;
        var playing = indexes.Where(index => candidates[index].IsPlaying).ToArray();
        var playingMusic = !explicitSelection
            ? playing.Where(index => candidates[index].IsMusicPlayer).ToArray() : [];
        var pausedLastMusic = !explicitSelection && playingMusic.Length == 0
            ? indexes.Where(index => candidates[index].IsMusicPlayer && candidates[index].IsPaused
                && string.Equals(candidates[index].SourceAppId, _lastSelectedSource, StringComparison.OrdinalIgnoreCase)).ToArray()
            : [];
        var pausedMusic = !explicitSelection && playing.Length == 0
            ? indexes.Where(index => candidates[index].IsMusicPlayer && candidates[index].IsPaused).ToArray()
            : [];
        var pool = playingMusic.Length > 0 ? playingMusic
            : pausedLastMusic.Length > 0 ? pausedLastMusic
            : playing.Length > 0 ? playing
            : pausedMusic.Length > 0 ? pausedMusic : indexes;
        int? chosen = null;
        if (playing.Length == 0)
            chosen = pool.Cast<int?>().FirstOrDefault(index => string.Equals(candidates[index!.Value].SourceAppId,
                _lastSelectedSource, StringComparison.OrdinalIgnoreCase));
        chosen ??= pool.Cast<int?>().FirstOrDefault(index => candidates[index!.Value].IsCurrent);
        chosen ??= pool.Cast<int?>().FirstOrDefault(index => string.Equals(candidates[index!.Value].SourceAppId,
            _lastSelectedSource, StringComparison.OrdinalIgnoreCase));
        chosen ??= pool[0];
        _lastSelectedSource = candidates[chosen.Value].SourceAppId;
        return chosen;
    }
}

internal sealed record MediaArtworkRequest(string Key, long Generation);

/// <summary>Thread-safe because WinRT property notifications can arrive off the UI thread.</summary>
internal sealed class MediaArtworkCache
{
    private readonly object _sync = new();
    private string? _key;
    private long _generation;
    private byte[]? _bytes;
    private int _failures;
    private DateTimeOffset _nextAttempt;

    internal MediaArtworkRequest? BeginRead(string key, DateTimeOffset now)
    {
        lock (_sync)
        {
            if (!string.Equals(_key, key, StringComparison.Ordinal))
            {
                _key = key; _bytes = null; _failures = 0; _generation++; _nextAttempt = default;
            }
            return now >= _nextAttempt ? new(key, _generation) : null;
        }
    }

    internal void Complete(MediaArtworkRequest request, byte[]? bytes, DateTimeOffset now)
    {
        lock (_sync)
        {
            if (request.Generation != _generation || !string.Equals(request.Key, _key, StringComparison.Ordinal)) return;
            if (bytes is not { Length: > 0 })
            {
                _failures = Math.Min(_failures + 1, 4);
                _nextAttempt = now.AddSeconds(Math.Min(5, 1 << (_failures - 1)));
                return;
            }
            if (_bytes is null || !_bytes.AsSpan().SequenceEqual(bytes)) _bytes = bytes;
            _failures = 0;
            _nextAttempt = now.AddSeconds(30);
        }
    }

    internal byte[]? GetData(string key)
    {
        lock (_sync) return string.Equals(_key, key, StringComparison.Ordinal) ? _bytes : null;
    }

    internal void Invalidate()
    {
        lock (_sync) { _generation++; _failures = 0; _nextAttempt = default; }
    }

    internal void Clear()
    {
        lock (_sync) { _key = null; _bytes = null; _generation++; _failures = 0; _nextAttempt = default; }
    }
}
