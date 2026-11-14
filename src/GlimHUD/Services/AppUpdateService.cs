using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using NetSparkleUpdater.Enums;
using NetSparkleUpdater.SignatureVerifiers;

namespace GlimHUD.Services;

public enum UpdateState { Disabled, Portable, Ready, Checking, UpToDate, Available, Downloading, Downloaded, Failed }

public sealed record UpdateSnapshot(UpdateState State, string CurrentVersion, string? AvailableVersion = null,
    double Progress = 0, string? ErrorCode = null, bool IsInstalled = false, bool IsPackaged = false);

/// <summary>
/// Checks a signed feed without dialogs or automatic installation. NetSparkle's
/// strict Ed25519 verifier authenticates both the exact feed bytes and installer.
/// Transport and parsing are bounded; only this project's versioned GitHub assets
/// can be offered. This signature does not establish Windows publisher trust.
/// </summary>
public sealed class AppUpdateService : IDisposable
{
    public const string FeedUrl = "https://pyrrhuladev.github.io/GlimHUD/updates/stable/appcast.xml";
    public const string ReleasePageUrl = "https://github.com/pyrrhuladev/GlimHUD/releases/latest";
    private const int FeedLimit = 1024 * 1024;
    private const int SignatureLimit = 4096;
    internal const long InstallerLimit = 512L * 1024 * 1024;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly Ed25519Checker? _verifier;
    private readonly string _cacheDirectory;
    private readonly Version _currentVersion;
    private readonly bool _isElevated;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private UpdateSnapshot _snapshot;
    private UpdateCandidate? _candidate;
    private string? _verifiedPath;
    private bool _disposed;

    public UpdateSnapshot Snapshot => Volatile.Read(ref _snapshot);
    public event EventHandler? Changed;

    public AppUpdateService() : this(CreateHttpClient(), ReadPublicKey(),
        typeof(AppUpdateService).Assembly.GetName().Version ?? new Version(0, 0, 0),
        Path.Combine(SettingsStore.DirectoryPath, "Updates"),
        HasInstallerMarker(AppContext.BaseDirectory), IsPackagedProcess(), ownsHttp: true,
        isElevated: AppRestartService.IsAdministrator) { }

    internal AppUpdateService(HttpClient http, string? publicKey, Version currentVersion,
        string cacheDirectory, bool isInstalled, bool isPackaged = false, bool ownsHttp = false, bool isElevated = false)
    {
        _http = http;
        _ownsHttp = ownsHttp;
        _cacheDirectory = Path.GetFullPath(cacheDirectory);
        _currentVersion = NormalizeVersion(currentVersion);
        _isElevated = isElevated;
        var state = isPackaged ? UpdateState.Disabled : isInstalled ? UpdateState.Ready : UpdateState.Portable;
        _snapshot = new(state, _currentVersion.ToString(3), IsInstalled: isInstalled, IsPackaged: isPackaged);
        try
        {
            if (Convert.FromBase64String(publicKey?.Trim() ?? "").Length == 32)
                _verifier = new Ed25519Checker(SecurityMode.Strict, publicKey!.Trim(), readFileBeingVerifiedInChunks: true);
        }
        catch (Exception error) when (error is FormatException or ArgumentException) { }
    }

    public async Task CheckAsync(bool manual = false, CancellationToken cancellationToken = default)
    {
        if (_disposed || !Snapshot.IsInstalled || Snapshot.IsPackaged || Snapshot.State == UpdateState.Downloaded) return;
        if (!await _gate.WaitAsync(0, cancellationToken)) return;
        if (_disposed || Snapshot.State == UpdateState.Downloaded) { _gate.Release(); return; }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        linked.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            SetState(UpdateState.Checking);
            _candidate = null;
            if (_verifier is null) { SetState(UpdateState.Failed, error: "MissingKey"); return; }
            var feed = await ReadSmallAsync(new Uri(FeedUrl), FeedLimit, linked.Token);
            var signature = Encoding.UTF8.GetString(await ReadSmallAsync(new Uri(FeedUrl + ".signature"), SignatureLimit, linked.Token)).Trim();
            if (!IsSignature(signature) || _verifier.VerifySignature(signature, feed) != ValidationResult.Valid)
                throw new UpdateValidationException("FeedSignature");
            _candidate = ParseFeed(feed, _currentVersion);
            SetState(_candidate is null ? UpdateState.UpToDate : UpdateState.Available, _candidate?.VersionText);
        }
        catch (OperationCanceledException)
        {
            if (!_disposed) SetState(!manual && cancellationToken.IsCancellationRequested ? UpdateState.Ready : UpdateState.Failed,
                error: !manual && cancellationToken.IsCancellationRequested ? null : "Cancelled");
        }
        catch (UpdateValidationException error) { SetState(UpdateState.Failed, error: error.Code); }
        catch (HttpRequestException) { SetState(UpdateState.Failed, error: "FeedUnavailable"); }
        catch (Exception error) when (error is XmlException or InvalidOperationException or IOException or FormatException)
        { SetState(UpdateState.Failed, error: "InvalidFeed"); }
        finally { _gate.Release(); }
    }

    public async Task DownloadAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed || Snapshot.State != UpdateState.Available || _candidate is null || _verifier is null) return;
        if (!await _gate.WaitAsync(0, cancellationToken)) return;
        if (_disposed || Snapshot.State != UpdateState.Available || _candidate is null) { _gate.Release(); return; }
        var candidate = _candidate;
        string? partial = null;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        linked.CancelAfter(TimeSpan.FromMinutes(10));
        try
        {
            SetState(UpdateState.Downloading, candidate.VersionText);
            Directory.CreateDirectory(_cacheDirectory);
            partial = Path.Combine(_cacheDirectory, $"GlimHUD-{candidate.VersionText}-{Guid.NewGuid():N}.partial");
            using var response = await SendAsync(candidate.Url, allowAssetRedirects: true, linked.Token);
            if (response.Content.Headers.ContentLength is { } length && length != candidate.Length)
                throw new UpdateValidationException("InstallerLength");
            await using (var input = await response.Content.ReadAsStreamAsync(linked.Token))
            await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
            {
                var buffer = new byte[65536];
                long total = 0;
                double reported = 0;
                int count;
                while ((count = await input.ReadAsync(buffer, linked.Token)) != 0)
                {
                    total += count;
                    if (total > candidate.Length || total > InstallerLimit) throw new UpdateValidationException("InstallerLength");
                    await output.WriteAsync(buffer.AsMemory(0, count), linked.Token);
                    var progress = total / (double)candidate.Length;
                    if (progress - reported >= .01) { SetState(UpdateState.Downloading, candidate.VersionText, progress); reported = progress; }
                }
                if (total != candidate.Length) throw new UpdateValidationException("InstallerLength");
            }
            linked.Token.ThrowIfCancellationRequested();
            var valid = await Task.Run(() => _verifier.VerifySignatureOfFile(candidate.Signature, partial), linked.Token);
            linked.Token.ThrowIfCancellationRequested();
            if (valid != ValidationResult.Valid) throw new UpdateValidationException("InstallerSignature");
            var target = Path.ChangeExtension(partial, ".exe");
            File.Move(partial, target);
            partial = null;
            _verifiedPath = target;
            SetState(UpdateState.Downloaded, candidate.VersionText, 1);
        }
        catch (OperationCanceledException) { if (!_disposed) SetState(UpdateState.Failed, error: "Cancelled"); }
        catch (UpdateValidationException error) { SetState(UpdateState.Failed, error: error.Code); }
        catch (Exception error) when (error is HttpRequestException or IOException or UnauthorizedAccessException or InvalidOperationException)
        { SetState(UpdateState.Failed, error: "DownloadFailed"); }
        finally
        {
            if (partial is not null) { try { File.Delete(partial); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
            _gate.Release();
        }
    }

    /// <summary>Revalidates the cached file; the caller alone decides when to stop the HUD and launch it.</summary>
    public Task<ProcessStartInfo?> PrepareInstallAsync(CancellationToken cancellationToken = default)
        => Task.Run(() => TryCreateInstallerStartInfo(out var start) ? start : null, cancellationToken);

    public bool TryCreateInstallerStartInfo(out ProcessStartInfo? start)
    {
        start = null;
        if (_disposed || Snapshot.State != UpdateState.Downloaded || _candidate is null || _verifiedPath is null || _verifier is null) return false;
        if (_isElevated) { SetState(UpdateState.Failed, error: "ElevatedInstall"); return false; }
        try
        {
            var file = new FileInfo(_verifiedPath);
            if (!file.Exists || file.Length != _candidate.Length || _candidate.Version <= _currentVersion
                || !file.FullName.StartsWith(_cacheDirectory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || _verifier.VerifySignatureOfFile(_candidate.Signature, file.FullName) != ValidationResult.Valid)
                throw new UpdateValidationException("InstallerSignature");
            start = new ProcessStartInfo(file.FullName) { UseShellExecute = true, WorkingDirectory = _cacheDirectory };
            foreach (var argument in new[] { "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/SP-", "/CURRENTUSER", "/GLIMHUDRESTART=1" }) start.ArgumentList.Add(argument);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or UpdateValidationException or InvalidOperationException)
        { SetState(UpdateState.Failed, error: "InstallerSignature"); return false; }
    }

    private async Task<byte[]> ReadSmallAsync(Uri url, int maximum, CancellationToken token)
    {
        using var response = await SendAsync(url, allowAssetRedirects: false, token);
        if (response.Content.Headers.ContentLength > maximum) throw new UpdateValidationException("ResponseTooLarge");
        await using var input = await response.Content.ReadAsStreamAsync(token);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await input.ReadAsync(buffer, token)) != 0)
        {
            if (output.Length + read > maximum) throw new UpdateValidationException("ResponseTooLarge");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private async Task<HttpResponseMessage> SendAsync(Uri initial, bool allowAssetRedirects, CancellationToken token)
    {
        var url = initial;
        for (var redirects = 0; redirects <= 5; redirects++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd("GlimHUD-Updater/1.0");
            var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            if ((int)response.StatusCode is >= 300 and < 400)
            {
                var location = response.Headers.Location;
                response.Dispose();
                if (location is null) throw new UpdateValidationException("Redirect");
                url = location.IsAbsoluteUri ? location : new Uri(url, location);
                if (!allowAssetRedirects || !IsAssetRedirect(url)) throw new UpdateValidationException("Redirect");
                continue;
            }
            try
            {
                response.EnsureSuccessStatusCode();
                // An injected/changed HttpClient must not silently bypass redirect checks.
                if (response.RequestMessage?.RequestUri is { } actual && actual != url) throw new UpdateValidationException("Redirect");
                return response;
            }
            catch { response.Dispose(); throw; }
        }
        throw new UpdateValidationException("Redirect");
    }

    internal static UpdateCandidate? ParseFeed(byte[] feed, Version current)
    {
        using var reader = XmlReader.Create(new MemoryStream(feed), new XmlReaderSettings
        { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = FeedLimit });
        var document = XDocument.Load(reader, LoadOptions.None);
        XNamespace sparkle = "http://www.andymatuschak.org/xml-namespaces/sparkle";
        var items = document.Root?.Name.LocalName == "rss" ? document.Root.Element("channel")?.Elements("item").Take(101).ToArray() : null;
        if (items is null || items.Length > 100) throw new UpdateValidationException("InvalidFeed");
        var candidates = new List<UpdateCandidate>();
        foreach (var item in items)
        {
            if (item.Element(sparkle + "channel")?.Value is { Length: > 0 } channel && channel != "stable") continue;
            var enclosure = item.Element("enclosure");
            if (enclosure is null) continue;
            var os = enclosure.Attribute(sparkle + "os")?.Value;
            if (os is not (null or "win" or "windows")) continue;
            var versionText = enclosure.Attribute(sparkle + "version")?.Value ?? "";
            if (!Regex.IsMatch(versionText, @"^\d+\.\d+\.\d+(?:\.\d+)?$") || !Version.TryParse(versionText, out var version))
                throw new UpdateValidationException("InvalidVersion");
            version = NormalizeVersion(version);
            if (version <= NormalizeVersion(current)) continue;
            var signature = enclosure.Attribute(sparkle + "signature")?.Value ?? enclosure.Attribute(sparkle + "edSignature")?.Value ?? "";
            if (!IsSignature(signature) || !long.TryParse(enclosure.Attribute("length")?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var length)
                || length <= 0 || length > InstallerLimit || !Uri.TryCreate(enclosure.Attribute("url")?.Value, UriKind.Absolute, out var uri)
                || !IsInstallerUrl(uri, version)) throw new UpdateValidationException("InvalidInstaller");
            candidates.Add(new(version, versionText, uri, length, signature));
        }
        return candidates.OrderByDescending(candidate => candidate.Version).FirstOrDefault();
    }

    internal static bool IsInstallerUrl(Uri uri, Version version)
    {
        if (uri.Scheme != Uri.UriSchemeHttps || uri.Host != "github.com" || !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0) return false;
        var match = Regex.Match(uri.AbsolutePath, @"^/pyrrhuladev/GlimHUD/releases/download/v(?<v>\d+\.\d+\.\d+(?:\.\d+)?)/GlimHUD(?:-(?<f>\d+\.\d+\.\d+(?:\.\d+)?))?-Setup-x64\.exe$");
        return match.Success && Version.TryParse(match.Groups["v"].Value, out var tag) && NormalizeVersion(tag) == NormalizeVersion(version)
            && (!match.Groups["f"].Success || Version.TryParse(match.Groups["f"].Value, out var file) && NormalizeVersion(file) == NormalizeVersion(version));
    }

    private static bool IsAssetRedirect(Uri uri) => uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && uri.UserInfo.Length == 0
        && uri.Host is "release-assets.githubusercontent.com" or "objects.githubusercontent.com" or "github-releases.githubusercontent.com";
    private static bool IsSignature(string signature)
    { try { return Convert.FromBase64String(signature).Length == 64; } catch (FormatException) { return false; } }
    private static Version NormalizeVersion(Version version) => new(version.Major, version.Minor, Math.Max(0, version.Build), Math.Max(0, version.Revision));
    private static HttpClient CreateHttpClient() => new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
    private static string? ReadPublicKey()
    { try { return File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Assets", "update-public-key.txt")).Trim(); } catch (IOException) { return null; } catch (UnauthorizedAccessException) { return null; } }
    internal static bool HasInstallerMarker(string directory)
    { try { return File.ReadAllText(Path.Combine(directory, "installed.marker")).Trim() == "GlimHUD installed v1"; } catch (IOException) { return false; } catch (UnauthorizedAccessException) { return false; } }
    private static bool IsPackagedProcess()
    { if (!OperatingSystem.IsWindows()) return false; uint length = 0; return GetCurrentPackageFullName(ref length, IntPtr.Zero) != 15700; }
    private void SetState(UpdateState state, string? version = null, double progress = 0, string? error = null)
    {
        if (_disposed) return;
        Volatile.Write(ref _snapshot, Snapshot with { State = state, AvailableVersion = version, Progress = progress, ErrorCode = error });
        Changed?.Invoke(this, EventArgs.Empty);
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _shutdown.Cancel();
        if (_ownsHttp) _http.Dispose();
        // The verified installer is intentionally retained for a user-requested
        // launch after the HUD has saved its state and shut down its services.
    }
    /// <summary>Cancels checks/downloads and waits for partial-file cleanup before app shutdown.</summary>
    public async Task StopAsync()
    {
        Dispose();
        await _gate.WaitAsync();
        _gate.Release();
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref uint packageFullNameLength, IntPtr packageFullName);
    internal sealed record UpdateCandidate(Version Version, string VersionText, Uri Url, long Length, string Signature);
    private sealed class UpdateValidationException(string code) : Exception(code) { public string Code { get; } = code; }
}
