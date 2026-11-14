using System.Diagnostics;

namespace GlimHUD.Services;

/// <summary>Official project and hardware-support destinations, opened only after an explicit UI action.</summary>
public static class ProjectLinks
{
    public const string RepositoryUrl = "https://github.com/pyrrhuladev/GlimHUD";
    public const string IssuesUrl = RepositoryUrl + "/issues";
    public const string ReleasesUrl = RepositoryUrl + "/releases";
    public const string WebsiteUrl = "https://pyrrhuladev.github.io/GlimHUD/";
    public const string PawnIoUrl = "https://pawnio.eu/";

    public static bool TryOpen(string? url) => TryOpen(url, info =>
    {
        using var process = Process.Start(info);
    });

    internal static bool TryOpen(string? url, Action<ProcessStartInfo> launch)
    {
        // Buttons expose official project/support destinations; never pass arbitrary
        // protocols, local paths or executable arguments to the shell.
        if (url is not (RepositoryUrl or IssuesUrl or ReleasesUrl or WebsiteUrl or PawnIoUrl)) return false;
        try
        {
            launch(new ProcessStartInfo(url) { UseShellExecute = true });
            return true;
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception
            or InvalidOperationException or ArgumentException or NotSupportedException
            or System.Security.SecurityException)
        {
            return false;
        }
    }
}
