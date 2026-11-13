using System.Windows;
using System.Windows.Markup;

namespace GlimHUD.Localization;

/// <summary>Updates WPF resources and window accessibility language in place.</summary>
public static class LocalizationService
{
    private static Application? _resourceOwner;
    private static string? _resourceLanguage;

    public static void ApplyLanguage(string? language)
    {
        var normalized = L10n.NormalizeLanguage(language);
        var app = Application.Current;
        if (app is null)
        {
            L10n.SetLanguage(normalized);
            return;
        }
        if (!app.Dispatcher.CheckAccess())
        {
            app.Dispatcher.Invoke(() => ApplyLanguage(normalized));
            return;
        }

        if (!ReferenceEquals(_resourceOwner, app) || _resourceLanguage != normalized)
        {
            foreach (var entry in UiStrings.Entries)
                app.Resources[entry.Key] = normalized == "ru" ? entry.Russian : entry.English;
            _resourceOwner = app;
            _resourceLanguage = normalized;
        }

        var xmlLanguage = XmlLanguage.GetLanguage(normalized == "ru" ? "ru-RU" : "en-US");
        foreach (Window window in app.Windows) window.Language = xmlLanguage;
        L10n.SetLanguage(normalized);
    }
}
