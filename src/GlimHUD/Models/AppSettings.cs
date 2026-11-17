namespace GlimHUD.Models;

public sealed class AppSettings
{
    public string Language { get; set; } = "en";
    public bool CheckForUpdates { get; set; } = true;
    public bool RequestAdministratorOnStartup { get; set; } = true;
    public bool CompactMode { get; set; }
    public HudTheme? Theme { get; set; }
    public double BackgroundOpacity { get; set; } = 0.30;
    public string TextColor { get; set; } = "#F2F8FC";
    public string BackgroundColor { get; set; } = "#07121D";
    public double Scale { get; set; } = 1.0;
    public double? Left { get; set; }
    public double? Top { get; set; }
    public bool Locked { get; set; }
    public bool SnapToGrid { get; set; } = true;
    public string? MediaSourceAppId { get; set; }
    public string? AudioSessionId { get; set; }
    public string? GpuIdentifier { get; set; }
    public string? NetworkAdapterId { get; set; }
    public bool ShowMusic { get; set; } = true;
    public bool ScrollMusicVolume { get; set; } = true;
    public bool AutoShowInGames { get; set; }
    public bool UseWindowsAccent { get; set; } = true;
    public string CustomAccentColor { get; set; } = "#65DDF2";
    public bool GlowEnabled { get; set; }
    public string GlowStyle { get; set; } = GlowStyles.RainbowWave;
    public bool GlowReactive { get; set; } = true;
    public double GlowIntensity { get; set; } = 0.65;
    public double GlowSensitivity { get; set; } = 0.75;
    public List<string> GameExecutablePaths { get; set; } = [];
    public bool ShowFps { get; set; } = true;
    public bool ShowCpu { get; set; } = true;
    public bool ShowGpu { get; set; } = true;
    public bool ShowRam { get; set; } = true;
    public bool ShowNetwork { get; set; }
}
