using System.Text.RegularExpressions;
using System.Xml.Linq;
using GlimHUD.Localization;
using GlimHUD.Native;

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

Check(L10n.Language == "en", "A fresh process must default to English.");
Check(L10n.Text("English", "Русский") == "English", "English text was not selected.");
Check(L10n.NormalizeLanguage(null) == "en" && L10n.NormalizeLanguage("de") == "en", "Unsupported languages must fall back to English.");
Check(L10n.NormalizeLanguage(" RU-ru ") == "ru", "Russian language tags must normalize.");

var changes = 0;
void OnLanguageChanged(object? sender, EventArgs args) => changes++;
L10n.LanguageChanged += OnLanguageChanged;
L10n.SetLanguage("ru");
Check(L10n.Text("English", "Русский") == "Русский", "Russian text was not selected.");
Check(L10n.Text("EN: {0:0.0}", "RU: {0:0.0}", 1.5) == "RU: 1,5", "Russian formatting must use the selected language.");
L10n.SetLanguage("ru-RU");
Check(changes == 1, "Selecting the same language must not raise another change event.");
L10n.SetLanguage("en");
Check(changes == 2 && L10n.Text("EN: {0:0.0}", "RU: {0:0.0}", 1.5) == "EN: 1.5", "Switching back must update language and formatting.");
L10n.LanguageChanged -= OnLanguageChanged;

Parallel.For(0, 1_000, index =>
{
    L10n.SetLanguage(index % 2 == 0 ? "en" : "ru");
    var text = L10n.Text("EN: {0:0.0}", "RU: {0:0.0}", 1.5);
    Check(text is "EN: 1.5" or "RU: 1,5", "A concurrent read mixed text and formatting languages.");
});
L10n.SetLanguage("en");

var musicShortcuts = HotkeyBindings.All.Where(binding => binding.Id is >= 4 and <= 8).ToArray();
var nativeKeys = musicShortcuts.Select(binding => (binding.Id, binding.VirtualKey, binding.Modifiers)).ToArray();
Check(musicShortcuts.All(binding => binding.LocalizedGesture == binding.Gesture),
    "English shortcut names must match their canonical registration labels.");
L10n.SetLanguage("ru");
Check(musicShortcuts.Single(binding => binding.Id == 4).LocalizedGesture == "Ctrl + Alt + Пробел"
    && musicShortcuts.Single(binding => binding.Id == 5).LocalizedGesture.EndsWith("Влево", StringComparison.Ordinal)
    && musicShortcuts.Single(binding => binding.Id == 6).LocalizedGesture.EndsWith("Вправо", StringComparison.Ordinal)
    && musicShortcuts.Single(binding => binding.Id == 7).LocalizedGesture.EndsWith("Вверх", StringComparison.Ordinal)
    && musicShortcuts.Single(binding => binding.Id == 8).LocalizedGesture.EndsWith("Вниз", StringComparison.Ordinal),
    "Russian settings must localize Space and arrow key names immediately.");
Check(nativeKeys.SequenceEqual(musicShortcuts.Select(binding => (binding.Id, binding.VirtualKey, binding.Modifiers))),
    "Translating shortcut labels must not change registered key combinations.");
L10n.SetLanguage("en");
Check(musicShortcuts.All(binding => binding.LocalizedGesture == binding.Gesture),
    "Existing shortcut rows must return to English without rebuilding the native catalog.");

var entries = UiStrings.Entries;
var keys = entries.Select(entry => entry.Key).ToHashSet(StringComparer.Ordinal);
Check(keys.Count == entries.Length, "The UI catalog contains duplicate keys.");
foreach (var entry in entries)
{
    Check(!string.IsNullOrWhiteSpace(entry.English) && !string.IsNullOrWhiteSpace(entry.Russian), $"Missing translation: {entry.Key}");
    Check(!Regex.IsMatch(entry.English, "[\\u0400-\\u04ff]"), $"English text contains Russian: {entry.Key}");
    static string[] Arguments(string text) => Regex.Matches(text, @"(?<!\{)\{(\d+)(?:[:,][^}]*)?\}(?!\})")
        .Select(match => match.Groups[1].Value).Distinct().OrderBy(value => value).ToArray();
    Check(Arguments(entry.English).SequenceEqual(Arguments(entry.Russian)),
        $"Translation formatting arguments do not match: {entry.Key}");
}

var references = 0;
var literalNames = new HashSet<string>(StringComparer.Ordinal)
    { "Text", "Content", "Header", "Title", "ToolTip", "AutomationProperties.Name" };
var languageNeutralLiterals = new HashSet<string>(StringComparer.Ordinal)
    { "GlimHUD", "Glim", "HUD", "GitHub", "FPS", "CPU", "GPU", "RAM" };
foreach (var name in new[] { "SettingsWindow.xaml", "OverlayWindow.xaml" })
{
    var document = XDocument.Load(Path.Combine(AppContext.BaseDirectory, name));
    foreach (var attribute in document.Descendants().Attributes())
    {
        foreach (Match match in Regex.Matches(attribute.Value, @"\{DynamicResource (Loc\.[^}]+)\}"))
        {
            Check(keys.Contains(match.Groups[1].Value), $"Missing UI resource in {name}: {match.Groups[1].Value}");
            references++;
        }
        Check(!Regex.IsMatch(attribute.Value, "[\\u0400-\\u04ff]"), $"Unlocalized Russian text in {name}: {attribute.Name}");
        if (literalNames.Contains(attribute.Name.LocalName) && !attribute.Value.StartsWith('{')
            && Regex.IsMatch(attribute.Value, "[A-Za-z]{2,}")
            && !attribute.Value.StartsWith("https://", StringComparison.Ordinal))
            Check(languageNeutralLiterals.Contains(attribute.Value),
                $"Unlocalized visible text in {name}: {attribute.Name}={attribute.Value}");
    }
}
Check(references > 0, "The checks did not inspect any localized XAML references.");
Console.WriteLine($"Localization checks passed: language switching, culture, concurrent reads, {entries.Length} translations and {references} XAML references.");
