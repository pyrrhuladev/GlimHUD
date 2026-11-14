using System.Diagnostics;
using GlimHUD.Services;

var passed = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    passed++;
}

foreach (var target in new[] { ProjectLinks.RepositoryUrl, ProjectLinks.IssuesUrl, ProjectLinks.ReleasesUrl, ProjectLinks.WebsiteUrl, ProjectLinks.PawnIoUrl })
{
    ProcessStartInfo? captured = null;
    Check(ProjectLinks.TryOpen(target, info => captured = info), "An official project link was rejected.");
    Check(captured is { UseShellExecute: true } && captured.FileName == target
        && captured.Arguments.Length == 0, "A project link did not use the exact browser destination.");
}
foreach (var invalid in new string?[] { null, "", "file:///C:/Windows/System32/cmd.exe", "javascript:alert(1)",
    "https://github.com/pyrrhuladev/GlimHUD.evil", "https://example.com", ProjectLinks.IssuesUrl + "?redirect=bad",
    "http://pawnio.eu/", "https://pawnio.eu.evil/", "https://pawnio.eu@evil.example/", "https://pawnio.eu",
    ProjectLinks.PawnIoUrl + "?download=1", ProjectLinks.PawnIoUrl + "driver.exe" })
{
    var launched = false;
    Check(!ProjectLinks.TryOpen(invalid, _ => launched = true) && !launched,
        "An unofficial destination reached the browser launcher.");
}
Check(!ProjectLinks.TryOpen(ProjectLinks.RepositoryUrl, _ => throw new System.ComponentModel.Win32Exception()),
    "A missing browser did not return a recoverable failure.");
Check(!ProjectLinks.TryOpen(ProjectLinks.IssuesUrl, _ => throw new InvalidOperationException()),
    "A shell failure did not return a recoverable failure.");
Check(!ProjectLinks.TryOpen(ProjectLinks.PawnIoUrl, _ => throw new System.ComponentModel.Win32Exception()),
    "An unavailable browser for PawnIO did not return a recoverable failure.");
Console.WriteLine($"{passed} project link checks passed; no browser was opened.");
