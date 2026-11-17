using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using GlimHUD.Models;

namespace GlimHUD.Services;

/// <summary>Appearance presets, validation, and portable JSON theme files.</summary>
public static class HudThemeCatalog
{
    public const int CurrentSchemaVersion = 1;
    public const int MaximumFileBytes = 256 * 1024;
    private static readonly string[] Modules = ["music", "fps", "cpu", "gpu", "ram", "network"];
    private static readonly JsonSerializerOptions FileOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        MaxDepth = 12,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    private static readonly HudTheme[] Presets =
    [
        new(),
        new()
        {
            Id = "capsule", Name = "Capsule", CornerRadius = 64, ModuleCornerRadius = 20,
            ContentHeight = 36, PaddingHorizontal = 18, PaddingVertical = 5, ModuleGap = 9,
            ValueFontSize = 18, LabelFontSize = 9, SecondaryFontSize = 10,
            ShowArtwork = false, ShowVolumeSlider = false, ShowIcons = false,
            BackgroundColor = "#131224", AccentColor = "#BEA5FF", BackgroundOpacity = .80,
            UseWindowsAccent = false
        },
        new()
        {
            Id = "slate", Name = "Slate", Layout = "cards", CornerRadius = 0,
            ModuleCornerRadius = 9, PaddingHorizontal = 12, PaddingVertical = 7, ModuleGap = 8,
            BackgroundColor = "#141B25", AccentColor = "#8CBFFF", BackgroundOpacity = .85,
            UseWindowsAccent = false, ShowDividers = false
        },
        new()
        {
            Id = "terminal", Name = "Terminal", CornerRadius = 0, ModuleCornerRadius = 0,
            ContentHeight = 36, PaddingHorizontal = 12, PaddingVertical = 6, ModuleGap = 9,
            FontFamily = "Consolas", ValueFontSize = 18, LabelFontSize = 10, SecondaryFontSize = 11,
            TextColor = "#B7F5CB", BackgroundColor = "#08140D", AccentColor = "#54D58C",
            BackgroundOpacity = .85, UseWindowsAccent = false, ShowIcons = false,
            ShowArtwork = false, ShowVolumeSlider = false
        },
        new()
        {
            Id = "paper", Name = "Paper", CornerRadius = 8, ModuleCornerRadius = 5,
            ContentHeight = 46, PaddingHorizontal = 18, PaddingVertical = 8, ModuleGap = 12,
            TextColor = "#272A31", BackgroundColor = "#F8EEDC", AccentColor = "#A65C36",
            BackgroundOpacity = .85, UseWindowsAccent = false, ShowArtwork = false,
            ShowIcons = false, BorderThickness = 0
        }
    ];

    /// <summary>Returns independent copies; editing a theme never mutates a built-in preset.</summary>
    public static IReadOnlyList<HudTheme> All => Array.AsReadOnly(Presets.Select(Normalize).ToArray());

    public static HudTheme GetPreset(string? id)
        => Normalize(Presets.FirstOrDefault(theme => string.Equals(theme.Id, id?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? Presets[0]);

    /// <summary>Returns a bounded independent theme. Version validation belongs to import.</summary>
    public static HudTheme Normalize(HudTheme? theme)
    {
        theme ??= new();
        var order = (theme.ModuleOrder ?? []).Select(value => value?.Trim().ToLowerInvariant())
            .Where(value => value is not null && Modules.Contains(value, StringComparer.Ordinal))
            .Select(value => value!).Distinct(StringComparer.Ordinal).Concat(Modules).Distinct(StringComparer.Ordinal).ToList();
        var id = theme.Id?.Trim().ToLowerInvariant();
        return new()
        {
            SchemaVersion = CurrentSchemaVersion,
            Id = id is not null && Regex.IsMatch(id, "^[a-z0-9._-]{1,48}$") ? id : "custom",
            Name = CleanName(theme.Name),
            Layout = string.Equals(theme.Layout?.Trim(), "cards", StringComparison.OrdinalIgnoreCase) ? "cards" : "bar",
            Orientation = string.Equals(theme.Orientation?.Trim(), "vertical", StringComparison.OrdinalIgnoreCase) ? "vertical" : "horizontal",
            CornerRadius = Bound(theme.CornerRadius, 0, 100, 34),
            ModuleCornerRadius = Bound(theme.ModuleCornerRadius, 0, 50, 12),
            BorderThickness = Bound(theme.BorderThickness, 0, 4, 1),
            ContentHeight = Bound(theme.ContentHeight, 28, 80, 50),
            PaddingHorizontal = Bound(theme.PaddingHorizontal, 0, 40, 24),
            PaddingVertical = Bound(theme.PaddingVertical, 0, 24, 8),
            ModuleGap = Bound(theme.ModuleGap, 0, 32, 13),
            FontFamily = SafeFont(theme.FontFamily),
            ValueFontSize = Bound(theme.ValueFontSize, 10, 32, 22),
            LabelFontSize = Bound(theme.LabelFontSize, 8, 16, 10),
            SecondaryFontSize = Bound(theme.SecondaryFontSize, 8, 20, 11),
            ShowIcons = theme.ShowIcons, ShowLabels = theme.ShowLabels,
            ShowArtwork = theme.ShowArtwork, ShowVolumeSlider = theme.ShowVolumeSlider,
            ShowDividers = theme.ShowDividers, ModuleOrder = order, CompactMode = theme.CompactMode,
            TextColor = Color(theme.TextColor, "#F2F8FC"), BackgroundColor = Color(theme.BackgroundColor, "#07121D"),
            AccentColor = Color(theme.AccentColor, "#65DDF2"),
            BackgroundOpacity = Bound(theme.BackgroundOpacity, .05, .85, .30),
            UseWindowsAccent = theme.UseWindowsAccent, GlowEnabled = theme.GlowEnabled,
            GlowStyle = GlowStyles.Normalize(theme.GlowStyle), GlowReactive = theme.GlowReactive,
            GlowIntensity = Bound(theme.GlowIntensity, 0, 1, .65),
            GlowSensitivity = Bound(theme.GlowSensitivity, 0, 1, .75)
        };
    }

    /// <summary>
    /// Existing appearance controls remain authoritative for palette, compact mode, and glow.
    /// A null theme retains the legacy appearance until a preset or customization is chosen.
    /// </summary>
    public static HudTheme Resolve(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var theme = Normalize(settings.Theme);
        theme.TextColor = Color(settings.TextColor, "#F2F8FC");
        theme.BackgroundColor = Color(settings.BackgroundColor, "#07121D");
        theme.AccentColor = Color(settings.CustomAccentColor, "#65DDF2");
        theme.BackgroundOpacity = Bound(settings.BackgroundOpacity, .05, .85, .30);
        theme.UseWindowsAccent = settings.UseWindowsAccent;
        theme.CompactMode = settings.CompactMode;
        theme.GlowEnabled = settings.GlowEnabled;
        theme.GlowStyle = GlowStyles.Normalize(settings.GlowStyle);
        theme.GlowReactive = settings.GlowReactive;
        theme.GlowIntensity = Bound(settings.GlowIntensity, 0, 1, .65);
        theme.GlowSensitivity = Bound(settings.GlowSensitivity, 0, 1, .75);
        return theme;
    }

    /// <summary>Copies only appearance, preserving device, module visibility, and app preferences.</summary>
    public static void Apply(AppSettings settings, HudTheme theme)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(theme);
        var normalized = Normalize(theme);
        settings.Theme = normalized;
        settings.TextColor = normalized.TextColor;
        settings.BackgroundColor = normalized.BackgroundColor;
        settings.CustomAccentColor = normalized.AccentColor;
        settings.BackgroundOpacity = normalized.BackgroundOpacity;
        settings.UseWindowsAccent = normalized.UseWindowsAccent;
        settings.CompactMode = normalized.CompactMode;
        settings.GlowEnabled = normalized.GlowEnabled;
        settings.GlowStyle = normalized.GlowStyle;
        settings.GlowReactive = normalized.GlowReactive;
        settings.GlowIntensity = normalized.GlowIntensity;
        settings.GlowSensitivity = normalized.GlowSensitivity;
    }

    public static void MarkCustom(AppSettings settings)
    {
        var theme = Resolve(settings);
        theme.Id = "custom";
        settings.Theme = theme;
    }

    public static string Export(AppSettings settings) => JsonSerializer.Serialize(Resolve(settings), FileOptions) + Environment.NewLine;

    /// <summary>Validates before returning; import never mutates the active application settings.</summary>
    public static HudTheme Import(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (Encoding.UTF8.GetByteCount(json) > MaximumFileBytes) throw new FormatException("The theme exceeds the 256 KB limit.");
        // StreamReader handles a UTF-8 BOM in files; also accept pasted JSON with that marker.
        json = json.TrimStart('\uFEFF');
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 12 });
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new FormatException("The theme must be a JSON object.");
        var versionProperty = document.RootElement.EnumerateObject().FirstOrDefault(property => string.Equals(property.Name, "schemaVersion", StringComparison.OrdinalIgnoreCase));
        if (versionProperty.Value.ValueKind != JsonValueKind.Number || !versionProperty.Value.TryGetInt32(out var version) || version != CurrentSchemaVersion)
            throw new FormatException("This theme uses an unsupported or missing schema version.");
        var theme = JsonSerializer.Deserialize<HudTheme>(json, FileOptions) ?? throw new FormatException("The theme is empty.");
        if (theme.SchemaVersion != CurrentSchemaVersion) throw new FormatException("This theme uses an unsupported schema version.");
        return Normalize(theme);
    }

    public static HudTheme ImportFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaximumFileBytes) throw new FormatException("The theme exceeds the 256 KB limit.");
        // Limit reads as well as the initial length check, including files changed concurrently.
        var bytes = new byte[MaximumFileBytes + 1];
        var count = 0;
        while (count < bytes.Length)
        {
            var read = stream.Read(bytes, count, bytes.Length - count);
            if (read == 0) break;
            count += read;
        }
        if (count > MaximumFileBytes) throw new FormatException("The theme exceeds the 256 KB limit.");
        return Import(new UTF8Encoding(false, true).GetString(bytes, 0, count));
    }

    public static void ExportFile(AppSettings settings, string path)
    {
        var content = Export(settings);
        // Write alongside the destination before replacement, retaining an existing file on failure.
        var fullPath = Path.GetFullPath(path);
        var temporary = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, content, new UTF8Encoding(false));
            File.Move(temporary, fullPath, true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static double Bound(double value, double minimum, double maximum, double fallback)
        => double.IsFinite(value) ? Math.Clamp(value, minimum, maximum) : fallback;

    private static string Color(string? color, string fallback)
        => color?.Trim() is { } value && Regex.IsMatch(value, "^#[0-9a-fA-F]{6}$") ? value.ToUpperInvariant() : fallback;

    private static string SafeFont(string? family)
        => family?.Trim() is { } value && Regex.IsMatch(value, "^[\\p{L}\\p{N} _-]{1,80}$") ? value : "Segoe UI";

    private static string CleanName(string? name)
    {
        var clean = new string((name ?? "").Where(character => !char.IsControl(character)).Take(64).ToArray()).Trim();
        return clean.Length > 0 ? clean : "Custom";
    }
}
