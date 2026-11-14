namespace GlimHUD.Models;

public sealed record GlowStyleDescriptor(string Id, string LabelKey, string DescriptionKey);

/// <summary>Stable persisted identifiers and settings metadata for panel glow styles.</summary>
public static class GlowStyles
{
    public const string RainbowWave = "rainbow-wave";
    public const string NeonFlow = "neon-flow";

    public static IReadOnlyList<GlowStyleDescriptor> All { get; } = Array.AsReadOnly<GlowStyleDescriptor>(
    [
        new(RainbowWave, "Loc.GlowStyleRainbow", "Loc.GlowStyleRainbowHint"),
        new(NeonFlow, "Loc.GlowStyleNeon", "Loc.GlowStyleNeonHint")
    ]);

    public static string Normalize(string? value)
        => All.FirstOrDefault(style => string.Equals(style.Id, value?.Trim(), StringComparison.OrdinalIgnoreCase))?.Id
            ?? RainbowWave;
}
