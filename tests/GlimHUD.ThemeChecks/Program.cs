using System.Text;
using System.Text.Json;
using GlimHUD.Models;
using GlimHUD.Services;

var passed = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException(name);
    Console.WriteLine("PASS " + name);
    passed++;
}
void Rejected(string json, string name)
{
    try { HudThemeCatalog.Import(json); throw new InvalidOperationException(name); }
    catch (Exception error) when (error is JsonException or FormatException) { Check(true, name); }
}

var settings = new AppSettings();
var classic = HudThemeCatalog.Resolve(settings);
Check(settings.Theme is null && classic.Id == "classic" && classic.CornerRadius == 34 && classic.ContentHeight == 50,
    "Legacy settings resolve to Classic without activating a theme or changing its geometry.");
settings.CompactMode = true;
settings.BackgroundColor = "#AABBCC";
settings.TextColor = "#223344";
settings.GlowEnabled = true;
settings.GlowStyle = GlowStyles.NeonFlow;
settings.UseWindowsAccent = false;
settings.CustomAccentColor = "#556677";
Check(HudThemeCatalog.Resolve(settings) is { CompactMode: true, BackgroundColor: "#AABBCC", TextColor: "#223344", GlowEnabled: true, GlowStyle: GlowStyles.NeonFlow, UseWindowsAccent: false, AccentColor: "#556677" },
    "Legacy compact, palette, and glow controls remain authoritative.");

var firstCatalog = HudThemeCatalog.All;
firstCatalog[0].Name = "Changed";
firstCatalog[0].ModuleOrder.Clear();
Check(HudThemeCatalog.All[0].Name == "Classic" && HudThemeCatalog.GetPreset("classic").ModuleOrder.Count == 6,
    "Editing a returned catalog theme cannot change the built-in presets.");
Check(HudThemeCatalog.All.Select(item => item.Id).Distinct().Count() == 5,
    "All five built-in presets have stable distinct IDs.");
Check(HudThemeCatalog.GetPreset("capsule").ContentHeight < classic.ContentHeight
    && HudThemeCatalog.GetPreset("slate").Layout == "cards"
    && HudThemeCatalog.GetPreset("terminal").CornerRadius == 0
    && HudThemeCatalog.GetPreset("paper").TextColor == "#272A31",
    "Presets differ in density, surface layout, shape, typography, and light palette.");

settings.ShowMusic = false;
settings.ShowFps = false;
settings.ShowNetwork = true;
settings.RequestAdministratorOnStartup = false;
settings.CheckForUpdates = false;
settings.Language = "ru";
settings.Left = 123;
settings.Top = 456;
settings.Scale = 1.2;
settings.Locked = true;
settings.NetworkAdapterId = "fixture-network";
settings.GpuIdentifier = "fixture-gpu";
settings.AudioSessionId = "fixture-audio";
settings.GameExecutablePaths = ["fixture-game.exe"];
var imported = HudThemeCatalog.Import(HudThemeCatalog.Export(new AppSettings()));
imported.Name = "My custom theme";
imported.ModuleOrder = ["network", "gpu", "music", "cpu", "fps", "ram"];
HudThemeCatalog.Apply(settings, imported);
Check(!settings.ShowMusic && !settings.ShowFps && settings.ShowNetwork && !settings.RequestAdministratorOnStartup
    && !settings.CheckForUpdates && settings.Language == "ru" && settings.Left == 123 && settings.Top == 456 && settings.Scale == 1.2
    && settings.Locked && settings.NetworkAdapterId == "fixture-network" && settings.GpuIdentifier == "fixture-gpu"
    && settings.AudioSessionId == "fixture-audio" && settings.GameExecutablePaths.SequenceEqual(["fixture-game.exe"]),
    "Applying a theme preserves modules, devices, admin/update choices, language, position, scale, and game paths.");
Check(!ReferenceEquals(settings.Theme, imported) && settings.Theme!.ModuleOrder[0] == "network", "Applying a theme copies mutable values.");
imported.ModuleOrder.Clear();
Check(settings.Theme.ModuleOrder.Count == 6, "Mutating imported data cannot change the active theme.");

settings.BackgroundColor = "#102030";
settings.TextColor = "#FAFBFC";
settings.BackgroundOpacity = .71;
settings.CustomAccentColor = "#AA5500";
settings.GlowEnabled = true;
settings.GlowIntensity = .28;
settings.CompactMode = true;
var exported = HudThemeCatalog.Import(HudThemeCatalog.Export(settings));
Check(exported is { Name: "My custom theme", BackgroundColor: "#102030", TextColor: "#FAFBFC", AccentColor: "#AA5500", BackgroundOpacity: .71, CompactMode: true, GlowEnabled: true, GlowIntensity: .28 }
    && exported.ModuleOrder.SequenceEqual(settings.Theme.ModuleOrder),
    "Export captures current palette/compact/glow edits and the complete structural customization.");
HudThemeCatalog.MarkCustom(settings);
Check(settings.Theme is { Id: "custom", Name: "My custom theme" }, "Marking a custom theme retains its user name.");

var normalized = HudThemeCatalog.Normalize(new HudTheme
{
    Id = "<bad/path>", Name = "\0  Custom\n", Layout = " CARDS ", Orientation = " VERTICAL ",
    CornerRadius = double.PositiveInfinity, ModuleCornerRadius = 2000, BorderThickness = -5,
    ContentHeight = -30, PaddingHorizontal = 999, PaddingVertical = double.NaN, ModuleGap = -1,
    ValueFontSize = 999, LabelFontSize = -100, SecondaryFontSize = double.NegativeInfinity,
    FontFamily = "https://example.invalid/font.ttf#Custom", TextColor = "#ABC", BackgroundColor = null!, AccentColor = " #abcDeF ",
    BackgroundOpacity = 999, GlowStyle = "unknown", GlowIntensity = double.NaN, GlowSensitivity = -5,
    ModuleOrder = ["GPU", "gpu", "unknown", null!, " network "]
});
Check(normalized is { Id: "custom", Name: "Custom", Layout: "cards", Orientation: "vertical" }, "Metadata and harmless enum case/whitespace normalize safely.");
Check(normalized is { CornerRadius: 34, ModuleCornerRadius: 50, BorderThickness: 0, ContentHeight: 28, PaddingHorizontal: 40, PaddingVertical: 8, ModuleGap: 0 },
    "Malformed geometry cannot create oversized, negative, or nonfinite layout measurements.");
Check(normalized is { ValueFontSize: 32, LabelFontSize: 8, SecondaryFontSize: 11, FontFamily: "Segoe UI" },
    "Typography is bounded and font URI/file references fall back to a local family.");
Check(normalized is { TextColor: "#F2F8FC", BackgroundColor: "#07121D", AccentColor: "#ABCDEF", BackgroundOpacity: .85, GlowIntensity: .65, GlowSensitivity: 0 },
    "Malformed colors and glow measurements normalize to supported values.");
Check(normalized.ModuleOrder.SequenceEqual(["gpu", "network", "music", "fps", "cpu", "ram"]),
    "Module order removes duplicates/unknown values and restores every omitted module once.");
var unicodeFont = HudThemeCatalog.Normalize(new HudTheme { FontFamily = "Noto Sans CJK JP", Name = "Моя тема" });
Check(unicodeFont.FontFamily == "Noto Sans CJK JP" && unicodeFont.Name == "Моя тема", "Theme names support Unicode and local font family names remain usable.");

Rejected("{}", "An explicit schema version is required for imported files.");
Rejected("{\"schemaVersion\":2}", "Future schema versions cannot be silently misinterpreted.");
Rejected("{\"schemaVersion\":1,\"xaml\":\"<ObjectDataProvider/>\"}", "Unknown/executable-markup fields are rejected.");
Rejected("{\"schemaVersion\":1,\"backgroundColro\":\"#FFFFFF\"}", "Misspelled fields cannot silently disappear during import.");
Rejected("{\"schemaVersion\":1,\"requestAdministratorOnStartup\":true}", "App preferences are not accepted in a theme file.");
Rejected("{\"schemaVersion\":1,\"cornerRadius\":\"32\"}", "Wrong JSON property types are rejected.");
Rejected("null", "Null themes cannot replace the current theme.");
Rejected("[]", "JSON arrays are not theme documents.");
Rejected("{", "Truncated files fail before mutating app settings.");
Rejected(new string(' ', HudThemeCatalog.MaximumFileBytes) + "{}", "Oversized theme text is rejected before JSON parsing.");
var beforeFailure = HudThemeCatalog.Export(settings);
try { HudThemeCatalog.Apply(settings, HudThemeCatalog.Import("{\"schemaVersion\":999}")); }
catch (FormatException) { }
Check(HudThemeCatalog.Export(settings) == beforeFailure, "A failed import leaves the active theme unchanged.");
Check(HudThemeCatalog.Import("\uFEFF{\"schemaVersion\":1,\"name\":\"BOM\"}").Name == "BOM", "A UTF-8 BOM does not prevent theme import.");

var originalDirectory = Environment.GetEnvironmentVariable("GLIMHUD_DATA_DIR");
var directory = Path.Combine(Path.GetTempPath(), "GlimHUD-ThemeChecks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
Environment.SetEnvironmentVariable("GLIMHUD_DATA_DIR", directory);
try
{
    var path = Path.Combine(directory, "roundtrip.glimtheme.json");
    HudThemeCatalog.ExportFile(settings, path);
    var fromFile = HudThemeCatalog.ImportFile(path);
    Check(fromFile.Name == settings.Theme!.Name && fromFile.ModuleOrder.SequenceEqual(settings.Theme.ModuleOrder), "UTF-8 file export and import retain the named custom theme and module order.");
    HudThemeCatalog.Apply(settings, HudThemeCatalog.GetPreset("terminal"));
    HudThemeCatalog.ExportFile(settings, path);
    Check(HudThemeCatalog.ImportFile(path).Id == "terminal", "Export can atomically replace an existing theme file.");
    Check(!Directory.EnumerateFiles(directory, "*.tmp").Any(), "Successful exports leave no temporary file behind.");
    var oversized = Path.Combine(directory, "oversized.json");
    File.WriteAllBytes(oversized, new byte[HudThemeCatalog.MaximumFileBytes + 1]);
    try { HudThemeCatalog.ImportFile(oversized); throw new InvalidOperationException("Oversized file accepted."); }
    catch (FormatException) { Check(true, "Oversized files are rejected before reading their contents."); }
    SettingsStore.Save(settings);
    var restored = SettingsStore.Load();
    Check(restored.Theme is { Id: "terminal", CornerRadius: 0, FontFamily: "Consolas" }
        && !restored.ShowMusic && restored.ShowNetwork && restored.BackgroundColor == "#08140D",
        "Theme structure, palette, and independent module preferences survive a settings restart.");
    File.WriteAllText(SettingsStore.FilePath, "{\"ShowMusic\":false,\"CompactMode\":true,\"TextColor\":\"#112233\"}");
    var legacy = SettingsStore.Load();
    Check(legacy.Theme is null && legacy.CompactMode && !legacy.ShowMusic && legacy.TextColor == "#112233", "Settings migration retains all legacy appearance choices without activating a theme.");
    File.WriteAllText(SettingsStore.FilePath, "{\"ShowMusic\":false,\"Theme\":{\"SchemaVersion\":900}}" );
    var future = SettingsStore.Load();
    Check(future.Theme is null && !future.ShowMusic, "Unsupported stored themes fall back without discarding unrelated settings.");
    File.WriteAllText(SettingsStore.FilePath, "{\"ShowMusic\":false,\"Theme\":{\"CornerRadius\":1e999,\"ModuleOrder\":null}}" );
    var malformed = SettingsStore.Load();
    Check(malformed.Theme is { CornerRadius: 34 } && malformed.Theme.ModuleOrder.Count == 6 && !malformed.ShowMusic, "Malformed stored theme measurements and order normalize without resetting app preferences.");

    if (args is ["--write-samples", var samplesDirectory])
    {
        Directory.CreateDirectory(samplesDirectory);
        foreach (var preset in HudThemeCatalog.All)
        {
            var sampleSettings = new AppSettings();
            HudThemeCatalog.Apply(sampleSettings, preset);
            HudThemeCatalog.ExportFile(sampleSettings, Path.Combine(samplesDirectory, preset.Id + ".glimtheme.json"));
        }
        Console.WriteLine("Theme samples written to " + samplesDirectory);
    }
}
finally
{
    Environment.SetEnvironmentVariable("GLIMHUD_DATA_DIR", originalDirectory);
    Directory.Delete(directory, true);
}
Console.WriteLine($"{passed} theme checks passed.");
