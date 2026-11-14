using System.Net;
using System.Net.Http;
using System.Text;
using System.Xml.Linq;
using Chaos.NaCl;
using GlimHUD.Services;

if (args is ["--verify-fixture", var appcastPath, var publicKeyPath, var installerPath])
{
    using var fixture = new Fixture
    {
        Feed = File.ReadAllBytes(appcastPath),
        FeedSignature = File.ReadAllText(appcastPath + ".signature").Trim(),
        InstallerPayload = File.ReadAllBytes(installerPath)
    };
    using var service = fixture.CreateService(key: File.ReadAllText(publicKeyPath).Trim());
    await service.CheckAsync();
    if (service.Snapshot.State != UpdateState.Available) throw new InvalidOperationException("Generated feed rejected: " + service.Snapshot.ErrorCode);
    await service.DownloadAsync();
    if (service.Snapshot.State != UpdateState.Downloaded || await service.PrepareInstallAsync() is null)
        throw new InvalidOperationException("Generated payload rejected: " + service.Snapshot.ErrorCode);
    Console.WriteLine("Official release-tool feed and payload accepted by the production updater (fake HTTP; installer not executed).");
    return;
}

var passed = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    passed++;
}
async Task RejectedFeed(byte[] feed, string message, string? expectedCode = null)
{
    using var fixture = new Fixture { Feed = feed };
    using var service = fixture.CreateService();
    await service.CheckAsync();
    Check(service.Snapshot.State == UpdateState.Failed && (expectedCode is null || service.Snapshot.ErrorCode == expectedCode), message);
    Check(fixture.Handler.Requests.Count == 2, "An invalid feed must never request an installer.");
}

using (var fixture = new Fixture())
using (var service = fixture.CreateService())
{
    var changes = new List<UpdateState>();
    service.Changed += (_, _) => changes.Add(service.Snapshot.State);
    await service.CheckAsync();
    Check(service.Snapshot.State == UpdateState.Available && service.Snapshot.AvailableVersion == "0.7.1", "A signed newer release must be offered.");
    Check(fixture.Handler.Requests.Count == 2 && !Directory.Exists(fixture.Cache), "An automatic check must not download or install anything.");
    Check(await service.PrepareInstallAsync() is null, "Installation before download must be unavailable.");
    await service.DownloadAsync();
    Check(service.Snapshot.State == UpdateState.Downloaded && service.Snapshot.Progress == 1, "A completely downloaded signed installer must be verified.");
    Check(changes.Contains(UpdateState.Checking) && changes.Contains(UpdateState.Downloading), "State changes must support the settings progress UI.");
    var start = await service.PrepareInstallAsync();
    Check(start is not null && File.Exists(start.FileName) && Path.GetExtension(start.FileName) == ".exe", "Only the verified complete executable can be prepared.");
    Check(start!.UseShellExecute && string.IsNullOrEmpty(start.Verb) && start.ArgumentList.Contains("/CURRENTUSER")
        && start.ArgumentList.Contains("/GLIMHUDRESTART=1") && start.ArgumentList.Contains("/NORESTART"), "Installer preparation must keep per-user settings and request only app restart.");
    Check(File.ReadAllBytes(start.FileName).SequenceEqual(Fixture.Payload), "The verified installer must contain the signed payload bytes.");
    var requests = fixture.Handler.Requests.Count;
    await service.CheckAsync(manual: true);
    Check(fixture.Handler.Requests.Count == requests && service.Snapshot.State == UpdateState.Downloaded, "A prepared update must survive another check without network or state loss.");
    File.WriteAllBytes(start.FileName, Fixture.Payload.Select(value => (byte)(value ^ 1)).ToArray());
    Check(await service.PrepareInstallAsync() is null && service.Snapshot.ErrorCode == "InstallerSignature", "Tampering after download must prevent installation.");
}

foreach (var (installed, packaged, state) in new[] { (false, false, UpdateState.Portable), (true, true, UpdateState.Disabled) })
{
    using var fixture = new Fixture();
    using var service = fixture.CreateService(installed: installed, packaged: packaged);
    await service.CheckAsync(manual: true);
    await service.DownloadAsync();
    Check(service.Snapshot.State == state && fixture.Handler.Requests.Count == 0, "Portable and MSIX distributions must never use installer updates.");
}
using (var fixture = new Fixture())
using (var service = fixture.CreateService(key: "not a key"))
{
    await service.CheckAsync();
    Check(service.Snapshot.ErrorCode == "MissingKey" && fixture.Handler.Requests.Count == 0, "An absent or malformed pinned key must fail closed before network access.");
}
using (var fixture = new Fixture())
using (var service = fixture.CreateService(key: Convert.ToBase64String(Ed25519.PublicKeyFromSeed(Enumerable.Repeat((byte)99, 32).ToArray()))))
{
    await service.CheckAsync();
    Check(service.Snapshot.ErrorCode == "FeedSignature", "A feed signed by an unpinned key must be rejected.");
}
foreach (var signature in new[] { "", "unsigned", Convert.ToBase64String(new byte[64]) })
{
    using var fixture = new Fixture { FeedSignature = signature };
    using var service = fixture.CreateService();
    await service.CheckAsync();
    Check(service.Snapshot.ErrorCode == "FeedSignature", "Missing, malformed and invalid detached feed signatures must be rejected.");
}
using (var fixture = new Fixture())
using (var service = fixture.CreateService())
{
    fixture.FeedSignature = Fixture.Sign(fixture.Feed);
    fixture.Feed = Fixture.BuildFeed(version: "0.7.2");
    await service.CheckAsync();
    Check(service.Snapshot.ErrorCode == "FeedSignature", "Changing signed feed bytes must invalidate the detached signature.");
}

foreach (var version in new[] { "0.6.9", "0.7.0", "0.7.0.0" })
{
    using var fixture = new Fixture { Feed = Fixture.BuildFeed(version) };
    using var service = fixture.CreateService();
    await service.CheckAsync();
    Check(service.Snapshot.State == UpdateState.UpToDate, "Equal and older release versions must never be installed.");
}
using (var fixture = new Fixture { Feed = Fixture.BuildFeed(version: "0.7.1", channel: "beta") })
using (var service = fixture.CreateService())
{
    await service.CheckAsync();
    Check(service.Snapshot.State == UpdateState.UpToDate, "The stable updater must not offer beta-channel releases.");
}
using (var fixture = new Fixture { Feed = Fixture.BuildFeed(version: "0.7.1", os: "macos") })
using (var service = fixture.CreateService())
{
    await service.CheckAsync();
    Check(service.Snapshot.State == UpdateState.UpToDate, "Other operating systems' releases must be ignored.");
}
await RejectedFeed(Encoding.UTF8.GetBytes("<!DOCTYPE rss [<!ENTITY bad SYSTEM 'file:///C:/Windows/win.ini'>]><rss><channel>&bad;</channel></rss>"), "XML DTDs and external entities must be prohibited.", "InvalidFeed");
await RejectedFeed(Encoding.UTF8.GetBytes("<rss><channel>"), "Malformed signed XML must be rejected.", "InvalidFeed");
await RejectedFeed(Fixture.BuildFeed(version: "1.0.0-beta"), "Unparseable or prerelease version metadata must fail closed.", "InvalidVersion");
await RejectedFeed(Fixture.BuildFeed(signature: ""), "The installer must have its own signature even when the feed is signed.", "InvalidInstaller");
await RejectedFeed(Fixture.BuildFeed(length: 0), "An empty installer must be rejected.", "InvalidInstaller");
await RejectedFeed(Fixture.BuildFeed(length: AppUpdateService.InstallerLimit + 1), "Oversized installer metadata must be rejected before downloading.", "InvalidInstaller");

foreach (var url in new[] {
    "http://github.com/pyrrhuladev/GlimHUD/releases/download/v0.7.1/GlimHUD-Setup-x64.exe",
    "file:///C:/untrusted.exe",
    "https://example.com/pyrrhuladev/GlimHUD/releases/download/v0.7.1/GlimHUD-Setup-x64.exe",
    "https://github.com/another/GlimHUD/releases/download/v0.7.1/GlimHUD-Setup-x64.exe",
    "https://github.com/pyrrhuladev/GlimHUD/releases/latest/download/GlimHUD-Setup-x64.exe",
    "https://github.com/pyrrhuladev/GlimHUD/releases/download/v0.7.2/GlimHUD-Setup-x64.exe",
    "https://github.com/pyrrhuladev/GlimHUD/releases/download/v0.7.1/GlimHUD-0.7.2-Setup-x64.exe",
    "https://name@github.com/pyrrhuladev/GlimHUD/releases/download/v0.7.1/GlimHUD-Setup-x64.exe",
    "https://github.com:444/pyrrhuladev/GlimHUD/releases/download/v0.7.1/GlimHUD-Setup-x64.exe",
    "https://github.com/pyrrhuladev/GlimHUD/releases/download/v0.7.1/GlimHUD-Setup-x64.exe?other=1" })
    await RejectedFeed(Fixture.BuildFeed(url: url), "Installer URL must pin HTTPS, repository, version, filename and default port.", "InvalidInstaller");

using (var fixture = new Fixture { Feed = new byte[1024 * 1024 + 1] })
using (var service = fixture.CreateService())
{
    await service.CheckAsync();
    Check(service.Snapshot.ErrorCode == "ResponseTooLarge" && fixture.Handler.Requests.Count == 1, "Feed responses must be bounded before signature parsing.");
}
using (var fixture = new Fixture { FeedSignature = new string('A', 4097) })
using (var service = fixture.CreateService())
{
    await service.CheckAsync();
    Check(service.Snapshot.ErrorCode == "ResponseTooLarge", "Detached signature responses must be bounded.");
}
using (var fixture = new Fixture())
using (var service = fixture.CreateService())
{
    fixture.Handler.Override = (request, _) => Task.FromResult<HttpResponseMessage?>(Fixture.Redirect("https://example.com/appcast.xml"));
    await service.CheckAsync();
    Check(service.Snapshot.ErrorCode == "Redirect" && fixture.Handler.Requests.Count == 1, "The pinned feed must not follow redirects.");
}
using (var fixture = new Fixture())
using (var service = fixture.CreateService())
{
    await service.CheckAsync();
    fixture.InstallerPayload = Fixture.Payload.Select(value => (byte)(value ^ 1)).ToArray();
    await service.DownloadAsync();
    Check(service.Snapshot.ErrorCode == "InstallerSignature" && !Directory.GetFiles(fixture.Cache).Any(), "A payload signature mismatch must remove partial data and disable installation.");
    Check(await service.PrepareInstallAsync() is null, "A rejected payload must never be installable.");
}
using (var fixture = new Fixture())
using (var service = fixture.CreateService())
{
    await service.CheckAsync();
    fixture.InstallerPayload = Fixture.Payload[..^1];
    await service.DownloadAsync();
    Check(service.Snapshot.ErrorCode == "InstallerLength" && !Directory.GetFiles(fixture.Cache).Any(), "A Content-Length mismatch must reject the download.");
}
foreach (var payload in new[] { Fixture.Payload[..^1], Fixture.Payload.Concat(new byte[] { 1 }).ToArray() })
{
    using var fixture = new Fixture();
    using var service = fixture.CreateService();
    await service.CheckAsync();
    fixture.Handler.Override = (_, _) => Task.FromResult<HttpResponseMessage?>(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StreamContent(new NonSeekableStream(payload)) });
    await service.DownloadAsync();
    Check(service.Snapshot.ErrorCode == "InstallerLength" && !Directory.GetFiles(fixture.Cache).Any(), "Streaming bodies without Content-Length must also match the exact signed size.");
}
using (var fixture = new Fixture())
using (var service = fixture.CreateService())
{
    await service.CheckAsync();
    fixture.Handler.Override = (request, _) => Task.FromResult<HttpResponseMessage?>(Fixture.Redirect("https://evil.example/installer.exe"));
    await service.DownloadAsync();
    Check(service.Snapshot.ErrorCode == "Redirect" && fixture.Handler.Requests.Count == 3, "Installer redirects to untrusted hosts must be rejected before fetching them.");
}
using (var fixture = new Fixture())
using (var service = fixture.CreateService())
{
    await service.CheckAsync();
    fixture.Handler.Override = (request, _) => Task.FromResult<HttpResponseMessage?>(request.RequestUri!.Host == "github.com"
        ? Fixture.Redirect("https://release-assets.githubusercontent.com/test/signed.exe?token=fixture") : null);
    await service.DownloadAsync();
    Check(service.Snapshot.State == UpdateState.Downloaded && fixture.Handler.Requests.Count == 4, "GitHub's HTTPS asset-host redirects must work while retaining payload verification.");
}
using (var fixture = new Fixture())
using (var service = fixture.CreateService())
{
    await service.CheckAsync();
    using var cancellation = new CancellationTokenSource();
    fixture.Handler.Override = async (_, token) => { cancellation.Cancel(); await Task.Delay(Timeout.Infinite, token); return null; };
    await service.DownloadAsync(cancellation.Token);
    Check(service.Snapshot.ErrorCode == "Cancelled" && !Directory.GetFiles(fixture.Cache).Any(), "Cancellation must remove all incomplete installers.");
}
using (var fixture = new Fixture())
using (var service = fixture.CreateService())
{
    await service.CheckAsync();
    var downloading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    fixture.Handler.Override = async (_, token) => { downloading.SetResult(); await Task.Delay(Timeout.Infinite, token); return null; };
    var download = service.DownloadAsync();
    await downloading.Task.WaitAsync(TimeSpan.FromSeconds(2));
    await service.StopAsync().WaitAsync(TimeSpan.FromSeconds(2));
    await download;
    Check(!Directory.GetFiles(fixture.Cache).Any(), "Shutdown must await cleanup of a manual download before returning.");
}
using (var fixture = new Fixture())
using (var service = fixture.CreateService())
{
    using var cancellation = new CancellationTokenSource();
    fixture.Handler.Override = async (_, token) => { cancellation.Cancel(); await Task.Delay(Timeout.Infinite, token); return null; };
    await service.CheckAsync(cancellationToken: cancellation.Token);
    Check(service.Snapshot.State == UpdateState.Ready && service.Snapshot.ErrorCode is null, "Turning off automatic checks must cancel quietly.");
}
using (var fixture = new Fixture())
using (var service = fixture.CreateService(elevated: true))
{
    await service.CheckAsync();
    await service.DownloadAsync();
    Check(await service.PrepareInstallAsync() is null && service.Snapshot.ErrorCode == "ElevatedInstall", "A per-user update must not be launched from an elevated HUD.");
}
using (var fixture = new Fixture())
{
    Directory.CreateDirectory(fixture.Cache);
    Check(!AppUpdateService.HasInstallerMarker(fixture.Cache), "A portable directory without a marker must not self-update.");
    File.WriteAllText(Path.Combine(fixture.Cache, "installed.marker"), "something else");
    Check(!AppUpdateService.HasInstallerMarker(fixture.Cache), "Unknown marker contents must not enable installer updates.");
    File.WriteAllText(Path.Combine(fixture.Cache, "installed.marker"), "GlimHUD installed v1\r\n");
    Check(AppUpdateService.HasInstallerMarker(fixture.Cache), "The exact installer marker must enable the installed distribution.");
}
Console.WriteLine($"Passed {passed} updater checks (fake HTTP and test-only Ed25519 keys; no installer execution).");

sealed class Fixture : IDisposable
{
    // This deterministic seed is a public test fixture, never a production signing key.
    private static readonly byte[] TestSeed = Enumerable.Range(1, 32).Select(value => (byte)value).ToArray();
    private static readonly byte[] TestPrivateKey = Ed25519.ExpandedPrivateKeyFromSeed(TestSeed);
    public static readonly byte[] Payload = Enumerable.Range(0, 150000).Select(value => (byte)(value % 251)).ToArray();
    private static readonly string TestPublicKey = Convert.ToBase64String(Ed25519.PublicKeyFromSeed(TestSeed));
    private static readonly string CacheRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "GlimHUD.UpdateChecks"));
    public string Cache { get; } = Path.Combine(CacheRoot, Guid.NewGuid().ToString("N"));
    public byte[] Feed { get; set; } = BuildFeed();
    public string? FeedSignature { get; set; }
    public byte[] InstallerPayload { get; set; } = Payload;
    public FakeHandler Handler { get; }
    private readonly HttpClient _http;
    public Fixture()
    {
        Handler = new FakeHandler(request => request.RequestUri!.AbsoluteUri switch
        {
            AppUpdateService.FeedUrl => new ByteArrayContent(Feed),
            AppUpdateService.FeedUrl + ".signature" => new StringContent(FeedSignature ?? Sign(Feed)),
            _ => new ByteArrayContent(InstallerPayload)
        });
        _http = new HttpClient(Handler);
    }
    public AppUpdateService CreateService(string? key = null, bool installed = true, bool packaged = false, bool elevated = false)
        => new(_http, key ?? TestPublicKey, new Version(0, 7, 0), Cache, installed, packaged, isElevated: elevated);
    public static string Sign(byte[] bytes) => Convert.ToBase64String(Ed25519.Sign(bytes, TestPrivateKey));
    public static byte[] BuildFeed(string version = "0.7.1", string? url = null, long? length = null, string? signature = null,
        string? channel = null, string os = "windows")
    {
        XNamespace sparkle = "http://www.andymatuschak.org/xml-namespaces/sparkle";
        var item = new XElement("item", new XElement("enclosure",
            new XAttribute("url", url ?? $"https://github.com/pyrrhuladev/GlimHUD/releases/download/v{version}/GlimHUD-{version}-Setup-x64.exe"),
            new XAttribute("length", length ?? Payload.LongLength), new XAttribute(sparkle + "version", version),
            new XAttribute(sparkle + "signature", signature ?? Sign(Payload)), new XAttribute(sparkle + "os", os)));
        if (channel is not null) item.Add(new XElement(sparkle + "channel", channel));
        return Encoding.UTF8.GetBytes(new XDocument(new XElement("rss", new XAttribute(XNamespace.Xmlns + "sparkle", sparkle),
            new XElement("channel", item))).ToString(SaveOptions.DisableFormatting));
    }
    public static HttpResponseMessage Redirect(string location)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Found);
        response.Headers.Location = new Uri(location);
        return response;
    }
    public void Dispose()
    {
        _http.Dispose();
        var full = Path.GetFullPath(Cache);
        if (full.StartsWith(CacheRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && Directory.Exists(full))
            Directory.Delete(full, recursive: true);
    }
}
sealed class FakeHandler(Func<HttpRequestMessage, HttpContent> content) : HttpMessageHandler
{
    public List<Uri> Requests { get; } = [];
    public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage?>>? Override { get; set; }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request.RequestUri!);
        var response = Override is null ? null : await Override(request, cancellationToken);
        response ??= new HttpResponseMessage(HttpStatusCode.OK) { Content = content(request) };
        response.RequestMessage ??= request;
        return response;
    }
}
sealed class NonSeekableStream(byte[] bytes) : MemoryStream(bytes, writable: false)
{
    public override bool CanSeek => false;
}
