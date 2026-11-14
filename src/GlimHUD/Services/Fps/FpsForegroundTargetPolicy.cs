namespace GlimHUD.Services.Fps;

/// <summary>
/// A recorder's control window can briefly become foreground while its game keeps
/// rendering. Those helper windows must not replace the application's FPS source.
/// An explicit pinned PID bypasses this automatic policy in PresentMonService.
/// </summary>
internal static class FpsForegroundTargetPolicy
{
    private static readonly HashSet<string> Ignored = new(StringComparer.OrdinalIgnoreCase)
    {
        "GlimHUD", "dwm", "explorer", "ShellExperienceHost", "StartMenuExperienceHost",
        "SearchHost", "SearchApp", "TextInputHost", "ApplicationFrameHost", "LockApp",
        "Taskmgr", "SystemSettings", "steam", "steamwebhelper", "EpicGamesLauncher",
        "EpicWebHelper", "Battle.net", "Agent", "EADesktop", "EABackgroundService",
        "NVIDIA Overlay", "NVIDIA Share", "NVIDIA App", "nvcontainer", "nvsphelper64",
        "nvsphelper", "NvBackend", "FrameView", "FrameViewSDK", "PresentMon", "PresentMonService",
        "GameBar", "GameBarFTServer", "GameBarPresenceWriter", "GameOverlayUI",
        "DiscordOverlayHost", "DiscordHookHelper", "DiscordHookHelper64", "Overwolf", "OverwolfBrowser",
        "obs64", "obs32"
    };

    // These known compositor/recording helpers have their own swap chains. They
    // are not game targets and do not need to consume our console CSV bandwidth.
    public static IReadOnlyList<string> CaptureExclusions { get; } =
    [
        "GlimHUD.exe", "dwm.exe", "NVIDIA Overlay.exe", "NVIDIA Share.exe",
        "nvcontainer.exe", "nvsphelper64.exe", "nvsphelper.exe", "FrameView.exe",
        "GameBar.exe", "GameBarFTServer.exe", "GameOverlayUI.exe", "obs64.exe", "obs32.exe"
    ];

    public static bool IsIgnoredProcess(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName)) return true;
        var name = processName.Trim();
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        return Ignored.Contains(name);
    }

    public static int? Select(int? previousProcessId, int foregroundProcessId,
        string? foregroundProcessName, int ownProcessId)
        => foregroundProcessId <= 0 || foregroundProcessId == ownProcessId || IsIgnoredProcess(foregroundProcessName)
            ? previousProcessId : foregroundProcessId;
}
