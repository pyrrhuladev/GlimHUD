using System.Text.RegularExpressions;
using System.Xml.Linq;
using GlimHUD.Localization;

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

var entries = UiStrings.Entries;
var keys = entries.Select(entry => entry.Key).ToHashSet(StringComparer.Ordinal);
Check(keys.Count == entries.Length, "The UI catalog contains duplicate keys.");
foreach (var entry in entries)
{
    Check(!string.IsNullOrWhiteSpace(entry.English) && !string.IsNullOrWhiteSpace(entry.Russian), $"Missing translation: {entry.Key}");
    Check(!Regex.IsMatch(entry.English, "[\\u0400-\\u04ff]"), $"English text contains Russian: {entry.Key}");
}

var references = 0;
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
    }
}
Check(references > 0, "The checks did not inspect any localized XAML references.");
Console.WriteLine($"Localization checks passed: language switching, culture, concurrent reads, {entries.Length} translations and {references} XAML references.");
