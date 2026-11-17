namespace GlimHUD.Models;

/// <summary>
/// Versioned, data-only appearance of a HUD. Themes carry no executable markup,
/// application preferences, device selections, hotkeys, or window positions.
/// All measurements use WPF device-independent pixels before the panel scale.
/// </summary>
public sealed class HudTheme
{
    public int SchemaVersion { get; set; } = 1;
    public string Id { get; set; } = "classic";
    public string Name { get; set; } = "Classic";
    public string Layout { get; set; } = "bar";
    public string Orientation { get; set; } = "horizontal";
    public double CornerRadius { get; set; } = 34;
    public double ModuleCornerRadius { get; set; } = 12;
    public double BorderThickness { get; set; } = 1;
    public double ContentHeight { get; set; } = 50;
    public double PaddingHorizontal { get; set; } = 24;
    public double PaddingVertical { get; set; } = 8;
    public double ModuleGap { get; set; } = 13;
    public string FontFamily { get; set; } = "Segoe UI";
    public double ValueFontSize { get; set; } = 22;
    public double LabelFontSize { get; set; } = 10;
    public double SecondaryFontSize { get; set; } = 11;
    public bool ShowIcons { get; set; } = true;
    public bool ShowLabels { get; set; } = true;
    public bool ShowArtwork { get; set; } = true;
    public bool ShowVolumeSlider { get; set; } = true;
    public bool ShowDividers { get; set; } = true;
    public List<string> ModuleOrder { get; set; } = ["music", "fps", "cpu", "gpu", "ram", "network"];
    public bool CompactMode { get; set; }
    public string TextColor { get; set; } = "#F2F8FC";
    public string BackgroundColor { get; set; } = "#07121D";
    public string AccentColor { get; set; } = "#65DDF2";
    public double BackgroundOpacity { get; set; } = 0.30;
    public bool UseWindowsAccent { get; set; } = true;
    public bool GlowEnabled { get; set; }
    public string GlowStyle { get; set; } = GlowStyles.RainbowWave;
    public bool GlowReactive { get; set; } = true;
    public double GlowIntensity { get; set; } = 0.65;
    public double GlowSensitivity { get; set; } = 0.75;
}
