using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Windows.UI.ViewManagement;

namespace GlimHUD.Services;

/// <summary>Applies Windows' live accent or an explicit RGB color to the app's shared brushes.</summary>
public sealed class AccentThemeService : IDisposable
{
    public static readonly Color DefaultColor = Color.FromRgb(102, 223, 255);
    public static readonly Color DefaultTextColor = Color.FromRgb(242, 248, 252);
    public static readonly Color DefaultBackgroundColor = Color.FromRgb(7, 18, 29);
    private readonly Dispatcher _dispatcher;
    private UISettings? _uiSettings;
    private bool _useSystemColor = true;
    private string? _customColor;
    private bool _published;
    private volatile bool _disposed;

    public Color CurrentColor { get; private set; } = DefaultColor;
    public Color SystemColor { get; private set; } = DefaultColor;
    public string CurrentHex => FormatHex(CurrentColor);
    public string SystemHex => FormatHex(SystemColor);
    public event EventHandler? AccentChanged;

    public AccentThemeService()
    {
        _dispatcher = System.Windows.Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        try
        {
            _uiSettings = new UISettings();
            _uiSettings.ColorValuesChanged += OnSystemColorsChanged;
        }
        catch { _uiSettings = null; }
        SystemParameters.StaticPropertyChanged += OnSystemParametersChanged;
        SystemColor = ReadSystemColor();
    }

    public void Apply(bool useSystemColor, string? customColor)
    {
        if (_disposed) return;
        if (!_dispatcher.CheckAccess())
        {
            Post(() => Apply(useSystemColor, customColor));
            return;
        }
        _useSystemColor = useSystemColor;
        _customColor = customColor;
        ApplyCurrentColor();
    }

    /// <summary>Overlay foreground and background are independent of the accent and each other.</summary>
    public void ApplyOverlayColors(string? textHex, string? backgroundHex, double opacity)
    {
        if (_disposed) return;
        if (!_dispatcher.CheckAccess())
        {
            Post(() => ApplyOverlayColors(textHex, backgroundHex, opacity));
            return;
        }
        var text = TryParseHex(textHex, out var parsedText) ? parsedText : DefaultTextColor;
        var background = TryParseHex(backgroundHex, out var parsedBackground) ? parsedBackground : DefaultBackgroundColor;
        var alpha = (byte)Math.Round((double.IsFinite(opacity) ? Math.Clamp(opacity, 0.05, 0.85) : 0.30) * 255);
        var resources = System.Windows.Application.Current?.Resources;
        if (resources is null) return;
        resources["OverlayTextBrush"] = Brush(text);
        resources["OverlayMutedTextBrush"] = Brush(text, 210);
        resources["OverlayIconBrush"] = Brush(text, 225);
        resources["OverlayBackgroundBrush"] = Brush(background, alpha);
    }

    public static bool TryParseHex(string? value, out Color color)
    {
        color = DefaultColor;
        value = value?.Trim();
        if (value is not { Length: 7 } || value[0] != '#' ||
            !uint.TryParse(value.AsSpan(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var rgb))
            return false;
        color = Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        return true;
    }

    private void ApplyCurrentColor()
    {
        if (_disposed) return;
        var next = _useSystemColor ? SystemColor
            : TryParseHex(_customColor, out var custom) ? custom : DefaultColor;
        var changed = CurrentColor != next;
        CurrentColor = next;
        if (changed || !_published)
        {
            var resources = System.Windows.Application.Current?.Resources;
            if (resources is not null)
            {
                resources["AccentBrush"] = Brush(next);
                resources["AccentTextBrush"] = Brush(ReadableOnDark(next));
                resources["AccentSoftBrush"] = Brush(next, 20);
                resources["AccentBorderBrush"] = Brush(next, 68);
                resources["AccentPanelBorderBrush"] = Brush(next, 20);
                resources["AccentCoverBrush"] = Brush(next, 37);
                resources["AccentCoverBorderBrush"] = Brush(next, 24);
                resources["AccentDividerBrush"] = Brush(next, 28);
                resources["AccentHoverBrush"] = Brush(next, 38);
                resources["AccentPressedBrush"] = Brush(next, 69);
                resources["AccentOnBrush"] = Brush(OnAccent(next));
                resources["AccentThumbBrush"] = Brush(BlendWithWhite(next, .65));
                resources["AccentThumbHoverBrush"] = Brush(BlendWithWhite(next, .85));
                resources["AccentFocusBrush"] = Brush(BlendWithWhite(next, .80));
                _published = true;
            }
        }
        if (changed) AccentChanged?.Invoke(this, EventArgs.Empty);
    }

    private Color ReadSystemColor()
    {
        try
        {
            if (_uiSettings is { } settings)
            {
                var color = settings.GetColorValue(UIColorType.Accent);
                return Color.FromRgb(color.R, color.G, color.B);
            }
        }
        catch { }
        try
        {
            var color = SystemParameters.WindowGlassColor;
            return Color.FromRgb(color.R, color.G, color.B);
        }
        catch { return DefaultColor; }
    }

    private void OnSystemColorsChanged(UISettings sender, object args) => Post(RefreshSystemColor);

    private void OnSystemParametersChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is null or "WindowGlassColor") Post(RefreshSystemColor);
    }

    private void RefreshSystemColor()
    {
        if (_disposed) return;
        var previousSystem = SystemColor;
        var previousAccent = CurrentColor;
        SystemColor = ReadSystemColor();
        ApplyCurrentColor();
        // Settings may show Windows' swatch even while custom mode is active.
        if (SystemColor != previousSystem && CurrentColor == previousAccent)
            AccentChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Post(Action action)
    {
        if (_disposed || _dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished) return;
        try { _dispatcher.BeginInvoke(DispatcherPriority.Background, action); }
        catch (InvalidOperationException) { }
    }

    private static string FormatHex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";
    private static SolidColorBrush Brush(Color color, byte alpha = 255)
    {
        var result = new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B));
        result.Freeze();
        return result;
    }

    private static Color BlendWithWhite(Color color, double amount) => Color.FromRgb(
        (byte)Math.Round(color.R + (255 - color.R) * amount),
        (byte)Math.Round(color.G + (255 - color.G) * amount),
        (byte)Math.Round(color.B + (255 - color.B) * amount));

    private static double Luminance(Color color)
    {
        static double Linear(byte component)
        {
            var channel = component / 255d;
            return channel <= .04045 ? channel / 12.92 : Math.Pow((channel + .055) / 1.055, 2.4);
        }
        return .2126 * Linear(color.R) + .7152 * Linear(color.G) + .0722 * Linear(color.B);
    }

    private static Color OnAccent(Color color)
    {
        var dark = Color.FromRgb(7, 18, 29);
        var luminance = Luminance(color);
        var darkContrast = (luminance + .05) / (Luminance(dark) + .05);
        var whiteContrast = 1.05 / (luminance + .05);
        return darkContrast >= whiteContrast ? dark : Colors.White;
    }

    private static Color ReadableOnDark(Color color)
    {
        var background = Luminance(Color.FromRgb(16, 25, 35));
        for (var amount = 0d; amount <= 1; amount += .05)
        {
            var candidate = BlendWithWhite(color, amount);
            if ((Luminance(candidate) + .05) / (background + .05) >= 4.5) return candidate;
        }
        return Colors.White;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        SystemParameters.StaticPropertyChanged -= OnSystemParametersChanged;
        if (_uiSettings is { } settings)
        {
            try { settings.ColorValuesChanged -= OnSystemColorsChanged; }
            catch { }
        }
        _uiSettings = null;
    }
}
