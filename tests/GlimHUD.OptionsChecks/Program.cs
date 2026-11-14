using GlimHUD.Models;
using GlimHUD.Services;
using System.Text.Json;

var passed = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException(name);
    Console.WriteLine("PASS " + name);
    passed++;
}
var visibility = new OverlayVisibilityPolicy();
Check(visibility.Evaluate(false, false, false, false), "Ordinary mode shows on the desktop.");
Check(!visibility.Evaluate(true, false, false, false), "Automatic mode hides outside games.");
Check(visibility.Evaluate(true, true, false, false), "Entering a game shows the overlay.");
Check(visibility.Evaluate(true, false, true, false), "Own focus preserves game visibility.");
Check(!visibility.Evaluate(true, false, false, false), "Alt-Tab to desktop hides instead of keeping the last FPS process.");
Check(visibility.Evaluate(true, false, true, true), "Settings can preview the overlay without a game.");
Check(!visibility.Evaluate(true, false, false, false), "Closing settings restores automatic hiding.");
visibility.Toggle(false);
Check(visibility.Evaluate(true, false, false, false), "Hotkey can manually show an automatically hidden overlay.");
Check(visibility.Evaluate(true, true, false, false), "Entering a game clears the temporary manual show override.");
Check(!visibility.Evaluate(true, false, false, false), "Leaving that game hides again.");
visibility.Toggle(true);
Check(!visibility.Evaluate(true, true, false, false), "Manual hide is respected even in a game.");
Check(!visibility.Evaluate(true, true, true, true), "A settings refresh does not override explicit manual hide.");
Check(visibility.Evaluate(false, false, false, false), "Changing visibility mode resets stale manual overrides.");

var originalDirectory = Environment.GetEnvironmentVariable("GLIMHUD_DATA_DIR");
var directory = Path.Combine(Path.GetTempPath(), "GlimHUD-OptionsChecks-" + Guid.NewGuid().ToString("N"));
Environment.SetEnvironmentVariable("GLIMHUD_DATA_DIR", directory);
try
{
    Directory.CreateDirectory(directory);
    File.WriteAllText(SettingsStore.FilePath, "{\"ShowMusic\":false,\"Scale\":1.1}");
    var old = SettingsStore.Load();
    Check(old.Language == "en", "Legacy settings default to English.");
    Check(old.CheckForUpdates, "Legacy settings enable automatic update checks by default.");
    Check(!old.CompactMode && old.RequestAdministratorOnStartup,
        "Legacy settings retain the regular layout and enable the requested administrator prompt by default.");
    Check(old.SnapToGrid, "Legacy settings enable the alignment grid.");
    Check(old.TextColor == "#F2F8FC" && old.BackgroundColor == "#07121D", "Legacy settings retain the original text and background colors.");
    Check(!old.ShowMusic && old.Scale == 1.1, "Legacy settings keep existing player visibility and scale.");
    Check(old.ScrollMusicVolume && !old.AutoShowInGames && old.UseWindowsAccent, "New options have migration defaults.");
    Check(!old.GlowEnabled && old.GlowReactive && old.GlowIntensity == 0.65,
        "Legacy settings keep experimental glow disabled with a useful music-response default.");
    Check(old.GlowStyle == GlowStyles.RainbowWave, "Legacy settings retain Rainbow Wave as the default glow style.");
    Check(old.GlowSensitivity == 0.75, "Legacy settings receive the music sensitivity default.");
    Check(!old.ShowNetwork && old.NetworkAdapterId is null, "Legacy settings keep the network module hidden with automatic adapter selection.");
    File.WriteAllText(SettingsStore.FilePath, "{\"GlowEnabled\":true,\"GlowReactive\":false,\"GlowIntensity\":0.82}");
    var previousGlow = SettingsStore.Load();
    Check(previousGlow.GlowStyle == GlowStyles.RainbowWave && previousGlow.GlowEnabled
        && !previousGlow.GlowReactive && previousGlow.GlowIntensity == 0.82,
        "Existing enabled glow migrates to Rainbow Wave without changing its options.");
    old.CustomAccentColor = "#E8845A";
    old.UseWindowsAccent = false;
    old.AutoShowInGames = true;
    old.ScrollMusicVolume = false;
    old.Language = "ru";
    old.CheckForUpdates = false;
    old.CompactMode = true;
    old.RequestAdministratorOnStartup = false;
    old.SnapToGrid = false;
    old.TextColor = "#000000";
    old.BackgroundColor = "#F8EEDC";
    old.GlowEnabled = true;
    old.GlowReactive = false;
    old.GlowIntensity = 0.82;
    old.GlowSensitivity = 0.91;
    old.GlowStyle = GlowStyles.NeonFlow;
    old.ShowNetwork = true;
    old.NetworkAdapterId = "fixture-adapter";
    old.GameExecutablePaths = [@"D:\Games\Example.exe", @"d:\games\example.exe"];
    SettingsStore.Save(old);
    var saved = SettingsStore.Load();
    Check(saved.Language == "ru", "Russian language survives a restart.");
    Check(!saved.CheckForUpdates, "Disabling automatic update checks survives a restart.");
    Check(saved.CompactMode && !saved.RequestAdministratorOnStartup,
        "Compact mode and opting out of startup administrator requests survive a restart.");
    Check(!saved.SnapToGrid, "Disabling the alignment grid survives a restart.");
    Check(saved.TextColor == "#000000" && saved.BackgroundColor == "#F8EEDC", "Text and background colors survive a restart independently.");
    Check(saved.GlowEnabled && !saved.GlowReactive && saved.GlowIntensity == 0.82,
        "Glow mode and intensity survive a restart independently of panel colors.");
    Check(saved.GlowStyle == GlowStyles.NeonFlow, "Neon Flow selection survives a restart.");
    Check(saved.GlowSensitivity == 0.91, "Music sensitivity survives a restart independently of glow intensity and style.");
    Check(saved.ShowNetwork && saved.NetworkAdapterId == "fixture-adapter", "Network visibility and an explicit adapter survive a restart.");
    Check(saved.CustomAccentColor == "#E8845A" && !saved.UseWindowsAccent, "Custom accent survives a restart.");
    Check(saved.AutoShowInGames && !saved.ScrollMusicVolume && !saved.ShowMusic, "All optional switches survive a restart.");
    Check(saved.GameExecutablePaths.Count == 1, "Duplicate game paths are normalized case-insensitively.");
    saved.GlowStyle = GlowStyles.RainbowWave;
    saved.CheckForUpdates = true;
    saved.CompactMode = false;
    saved.RequestAdministratorOnStartup = true;
    saved.ShowNetwork = false;
    saved.NetworkAdapterId = null;
    SettingsStore.Save(saved);
    Check(SettingsStore.Load().GlowStyle == GlowStyles.RainbowWave, "Switching back to Rainbow Wave survives a restart.");
    Check(SettingsStore.Load().CheckForUpdates, "Re-enabling automatic update checks survives a restart.");
    var restoredLayout = SettingsStore.Load();
    Check(!restoredLayout.CompactMode && restoredLayout.RequestAdministratorOnStartup,
        "Returning to regular layout and re-enabling startup administrator requests survive a restart.");
    var automaticNetwork = SettingsStore.Load();
    Check(!automaticNetwork.ShowNetwork && automaticNetwork.NetworkAdapterId is null,
        "Hiding the network module and returning to automatic adapter selection survive a restart.");
    File.WriteAllText(SettingsStore.FilePath, "{\"NetworkAdapterId\":\"   \",\"ShowNetwork\":true,\"Scale\":1.1}");
    var blankNetwork = SettingsStore.Load();
    Check(blankNetwork.NetworkAdapterId is null && blankNetwork.ShowNetwork && blankNetwork.Scale == 1.1,
        "A blank adapter ID becomes automatic without discarding other settings.");
    File.WriteAllText(SettingsStore.FilePath, "{\"NetworkAdapterId\":\" missing-adapter \"}");
    Check(SettingsStore.Load().NetworkAdapterId == "missing-adapter",
        "An explicit unavailable adapter ID is preserved after trimming so selection does not silently switch.");
    File.WriteAllText(SettingsStore.FilePath, "{\"CompactMode\":true,\"RequestAdministratorOnStartup\":false,\"GlowIntensity\":2}");
    var normalizedPreferences = SettingsStore.Load();
    Check(normalizedPreferences.CompactMode && !normalizedPreferences.RequestAdministratorOnStartup && normalizedPreferences.GlowIntensity == 1,
        "Normalizing an unrelated appearance value preserves compact layout and the administrator opt-out.");
    foreach (var unsupportedStyle in new string?[] { null, "future-style", "" })
    {
        File.WriteAllText(SettingsStore.FilePath, JsonSerializer.Serialize(new
        {
            GlowStyle = unsupportedStyle, GlowEnabled = true, GlowReactive = false,
            GlowIntensity = 0.82, GlowSensitivity = 0.91, ShowMusic = false, Scale = 1.1, TextColor = "#000000"
        }));
        var fallback = SettingsStore.Load();
        Check(fallback.GlowStyle == GlowStyles.RainbowWave && fallback.GlowEnabled
            && !fallback.GlowReactive && fallback.GlowIntensity == 0.82 && fallback.GlowSensitivity == 0.91 && !fallback.ShowMusic
            && fallback.Scale == 1.1 && fallback.TextColor == "#000000",
            $"Unsupported glow style '{unsupportedStyle ?? "<null>"}' falls back without discarding other settings.");
    }
    File.WriteAllText(SettingsStore.FilePath, "{\"GlowStyle\":\" NeOn-FlOw \"}");
    Check(SettingsStore.Load().GlowStyle == GlowStyles.NeonFlow, "Stored glow style normalizes harmless case and whitespace.");
    Check(GlowStyles.All.Select(style => style.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() == GlowStyles.All.Count,
        "Glow style catalog has unique stable identifiers.");
    File.WriteAllText(SettingsStore.FilePath, "{\"CustomAccentColor\":\"broken\",\"GameExecutablePaths\":null}");
    var malformed = SettingsStore.Load();
    Check(malformed.CustomAccentColor == "#65DDF2" && malformed.GameExecutablePaths.Count == 0, "Malformed option values fall back safely.");
    File.WriteAllText(SettingsStore.FilePath, "{\"Language\":\"fr\"}");
    Check(SettingsStore.Load().Language == "en", "Unsupported stored languages fall back to English.");
    File.WriteAllText(SettingsStore.FilePath, "{\"TextColor\":\"broken\",\"BackgroundColor\":null}");
    var invalidColors = SettingsStore.Load();
    Check(invalidColors.TextColor == "#F2F8FC" && invalidColors.BackgroundColor == "#07121D", "Malformed stored overlay colors fall back safely.");
    File.WriteAllText(SettingsStore.FilePath, "{\"TextColor\":\" #dF5511 \",\"BackgroundColor\":\"#aabbcc\"}");
    var normalizedColors = SettingsStore.Load();
    Check(normalizedColors.TextColor == "#DF5511" && normalizedColors.BackgroundColor == "#AABBCC", "Stored overlay colors normalize harmless case and whitespace.");
    File.WriteAllText(SettingsStore.FilePath, "{\"GlowIntensity\":2}");
    Check(SettingsStore.Load().GlowIntensity == 1, "Oversized glow intensity is clamped to full strength.");
    File.WriteAllText(SettingsStore.FilePath, "{\"GlowIntensity\":-1}");
    Check(SettingsStore.Load().GlowIntensity == 0, "Negative glow intensity cannot create an invalid effect.");
    File.WriteAllText(SettingsStore.FilePath, "{\"GlowIntensity\":1e999}");
    Check(SettingsStore.Load().GlowIntensity == 0.65, "Non-finite glow intensity returns to the safe default.");
    File.WriteAllText(SettingsStore.FilePath, "{\"GlowSensitivity\":2,\"ShowMusic\":false}");
    var highSensitivity = SettingsStore.Load();
    Check(highSensitivity.GlowSensitivity == 1 && !highSensitivity.ShowMusic,
        "Oversized music sensitivity is clamped without resetting other settings.");
    File.WriteAllText(SettingsStore.FilePath, "{\"GlowSensitivity\":-1}");
    Check(SettingsStore.Load().GlowSensitivity == 0, "Negative music sensitivity is clamped to zero.");
    File.WriteAllText(SettingsStore.FilePath, "{\"GlowSensitivity\":1e999,\"GlowStyle\":\"neon-flow\"}");
    var nonfiniteSensitivity = SettingsStore.Load();
    Check(nonfiniteSensitivity.GlowSensitivity == 0.75 && nonfiniteSensitivity.GlowStyle == GlowStyles.NeonFlow,
        "Non-finite music sensitivity uses its safe default without resetting the selected style.");
}
finally { Environment.SetEnvironmentVariable("GLIMHUD_DATA_DIR", originalDirectory); }
Console.WriteLine($"{passed} option checks passed.");
