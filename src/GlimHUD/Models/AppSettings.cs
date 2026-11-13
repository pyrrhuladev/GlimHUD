namespace GlimHUD.Models;

public sealed class AppSettings
{
    public string Language { get; set; } = "en";
    public double BackgroundOpacity { get; set; } = 0.30;
    public double Scale { get; set; } = 1.0;
    public double? Left { get; set; }
    public double? Top { get; set; }
    public bool Locked { get; set; }
    public string? MediaSourceAppId { get; set; }
    public string? AudioSessionId { get; set; }
    public string? GpuIdentifier { get; set; }
    public bool ShowMusic { get; set; } = true;
    public bool ScrollMusicVolume { get; set; } = true;
    public bool AutoShowInGames { get; set; }
    public bool UseWindowsAccent { get; set; } = true;
    public string CustomAccentColor { get; set; } = "#65DDF2";
    public List<string> GameExecutablePaths { get; set; } = [];
    public bool ShowFps { get; set; } = true;
    public bool ShowCpu { get; set; } = true;
    public bool ShowGpu { get; set; } = true;
    public bool ShowRam { get; set; } = true;
}
