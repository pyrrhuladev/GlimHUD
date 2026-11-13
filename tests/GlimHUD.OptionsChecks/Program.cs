using GlimHUD.Models;
using GlimHUD.Services;

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
    Check(!old.ShowMusic && old.Scale == 1.1, "Legacy settings keep existing player visibility and scale.");
    Check(old.ScrollMusicVolume && !old.AutoShowInGames && old.UseWindowsAccent, "New options have migration defaults.");
    old.CustomAccentColor = "#E8845A";
    old.UseWindowsAccent = false;
    old.AutoShowInGames = true;
    old.ScrollMusicVolume = false;
    old.Language = "ru";
    old.GameExecutablePaths = [@"D:\Games\Example.exe", @"d:\games\example.exe"];
    SettingsStore.Save(old);
    var saved = SettingsStore.Load();
    Check(saved.Language == "ru", "Russian language survives a restart.");
    Check(saved.CustomAccentColor == "#E8845A" && !saved.UseWindowsAccent, "Custom accent survives a restart.");
    Check(saved.AutoShowInGames && !saved.ScrollMusicVolume && !saved.ShowMusic, "All optional switches survive a restart.");
    Check(saved.GameExecutablePaths.Count == 1, "Duplicate game paths are normalized case-insensitively.");
    File.WriteAllText(SettingsStore.FilePath, "{\"CustomAccentColor\":\"broken\",\"GameExecutablePaths\":null}");
    var malformed = SettingsStore.Load();
    Check(malformed.CustomAccentColor == "#65DDF2" && malformed.GameExecutablePaths.Count == 0, "Malformed option values fall back safely.");
    File.WriteAllText(SettingsStore.FilePath, "{\"Language\":\"fr\"}");
    Check(SettingsStore.Load().Language == "en", "Unsupported stored languages fall back to English.");
}
finally { Environment.SetEnvironmentVariable("GLIMHUD_DATA_DIR", originalDirectory); }
Console.WriteLine($"{passed} option checks passed.");
