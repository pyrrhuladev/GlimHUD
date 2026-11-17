using System.IO;
using System.Text.Json;
using GlimHUD.Models;

namespace GlimHUD.Services;

public static class SettingsStore
{
    public static string DirectoryPath => Environment.GetEnvironmentVariable("GLIMHUD_DATA_DIR") is { Length: > 0 } overridePath
        ? Path.GetFullPath(overridePath)
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GlimHUD");
    public static string FilePath => Path.Combine(DirectoryPath, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new();
            settings.Language = string.Equals(settings.Language, "ru", StringComparison.OrdinalIgnoreCase) ? "ru" : "en";
            settings.BackgroundOpacity = double.IsFinite(settings.BackgroundOpacity) ? Math.Clamp(settings.BackgroundOpacity, 0.05, 0.85) : 0.30;
            settings.TextColor = NormalizeColor(settings.TextColor, "#F2F8FC");
            settings.BackgroundColor = NormalizeColor(settings.BackgroundColor, "#07121D");
            settings.NetworkAdapterId = string.IsNullOrWhiteSpace(settings.NetworkAdapterId) ? null : settings.NetworkAdapterId.Trim();
            settings.GlowStyle = GlowStyles.Normalize(settings.GlowStyle);
            settings.GlowIntensity = double.IsFinite(settings.GlowIntensity) ? Math.Clamp(settings.GlowIntensity, 0, 1) : 0.65;
            settings.GlowSensitivity = double.IsFinite(settings.GlowSensitivity) ? Math.Clamp(settings.GlowSensitivity, 0, 1) : 0.75;
            settings.Scale = double.IsFinite(settings.Scale) ? Math.Clamp(settings.Scale, 0.65, 1.5) : 1;
            // Theme structure is optional so old installations retain their exact layout.
            // Palette/compact/glow values above remain authoritative after any appearance edits.
            if (settings.Theme is { } theme)
                settings.Theme = theme.SchemaVersion == HudThemeCatalog.CurrentSchemaVersion ? HudThemeCatalog.Normalize(theme) : null;
            if (settings.Left is { } left && !double.IsFinite(left)) settings.Left = null;
            if (settings.Top is { } top && !double.IsFinite(top)) settings.Top = null;
            if (settings.CustomAccentColor is null || !System.Text.RegularExpressions.Regex.IsMatch(settings.CustomAccentColor, "^#[0-9A-Fa-f]{6}$"))
                settings.CustomAccentColor = "#65DDF2";
            settings.GameExecutablePaths = (settings.GameExecutablePaths ?? [])
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            // Audio session instance IDs expire across application restarts.
            settings.AudioSessionId = null;
            return settings;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new();
        }
    }

    public static void Save(AppSettings settings)
    {
        Directory.CreateDirectory(DirectoryPath);
        var temporary = FilePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, FilePath, true);
    }

    private static string NormalizeColor(string? value, string fallback)
    {
        value = value?.Trim();
        return value is not null && System.Text.RegularExpressions.Regex.IsMatch(value, "^#[0-9A-Fa-f]{6}$")
            ? value.ToUpperInvariant() : fallback;
    }
}
