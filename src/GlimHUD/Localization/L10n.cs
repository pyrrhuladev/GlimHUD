using System.Globalization;
using System.Threading;

namespace GlimHUD.Localization;

/// <summary>Shared localization for UI and background services.</summary>
public static class L10n
{
    private static readonly CultureInfo EnglishCulture = CultureInfo.GetCultureInfo("en-US");
    private static readonly CultureInfo RussianCulture = CultureInfo.GetCultureInfo("ru-RU");
    private static string _language = "en";

    public static string Language => Volatile.Read(ref _language);
    public static CultureInfo Culture => Language == "ru" ? RussianCulture : EnglishCulture;
    public static event EventHandler? LanguageChanged;

    public static string NormalizeLanguage(string? language)
        => string.Equals(language?.Trim(), "ru", StringComparison.OrdinalIgnoreCase)
            || language?.Trim().StartsWith("ru-", StringComparison.OrdinalIgnoreCase) == true ? "ru" : "en";

    public static void SetLanguage(string? language)
    {
        var normalized = NormalizeLanguage(language);
        if (Interlocked.Exchange(ref _language, normalized) != normalized)
            LanguageChanged?.Invoke(null, EventArgs.Empty);
    }

    public static string Text(string english, string russian, params object?[] args)
    {
        var useRussian = Language == "ru";
        var text = useRussian ? russian : english;
        return args.Length == 0 ? text : string.Format(useRussian ? RussianCulture : EnglishCulture, text, args);
    }
}
