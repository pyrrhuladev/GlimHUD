using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GlimHUD.Localization;
using GlimHUD.Models;
using GlimHUD.Services;

namespace GlimHUD;

public partial class SettingsWindow
{
    private bool _themeControlsReady;
    private string? _themeStatusKey;
    private object[] _themeStatusArguments = [];
    private bool _themeStatusFailed;

    private void InitializeThemeControls()
    {
        _themeControlsReady = true;
        RefreshThemeControls();
    }

    private void RefreshThemeChoices()
    {
        var settings = _overlay.Settings;
        var choices = HudThemeCatalog.All.Select(theme => new Choice(theme.Id, theme.Name)).ToList();
        var selected = settings.Theme?.Id ?? "current";
        if (selected == "current") choices.Insert(0, new("current", FormatUi("Loc.ThemeCurrent")));
        else if (choices.All(choice => choice.Key != selected))
            choices.Add(new(selected, string.IsNullOrWhiteSpace(settings.Theme?.Name)
                ? FormatUi("Loc.ThemeCustom") : $"{FormatUi("Loc.ThemeCustom")} · {settings.Theme.Name}"));
        UpdateChoices(ThemeCombo, choices, selected);
    }

    private void RefreshThemeControls()
    {
        if (!_themeControlsReady) return;
        var syncing = _syncing;
        _syncing = true;
        try
        {
            var theme = HudThemeCatalog.Resolve(_overlay.Settings);
            RefreshThemeChoices();
            ThemeLayoutCombo.ItemsSource = new[]
            {
                new Choice("bar", FormatUi("Loc.ThemeBar")), new Choice("cards", FormatUi("Loc.ThemeCards"))
            };
            ThemeOrientationCombo.ItemsSource = new[]
            {
                new Choice("horizontal", FormatUi("Loc.ThemeHorizontal")), new Choice("vertical", FormatUi("Loc.ThemeVertical"))
            };
            var fontNames = new[] { "Segoe UI", "Segoe UI Variable", "Arial", "Calibri", "Consolas", "Cascadia Mono", "Courier New", "Verdana", "Tahoma" };
            var fonts = fontNames.Append(theme.FontFamily).Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(font => new Choice(font, font)).ToArray();
            ThemeFontCombo.ItemsSource = fonts;
            ThemeNameBox.Text = theme.Name;
            ThemeLayoutCombo.SelectedValue = theme.Layout;
            ThemeOrientationCombo.SelectedValue = theme.Orientation;
            ThemeFontCombo.SelectedValue = theme.FontFamily;
            ThemeRadiusSlider.Value = theme.CornerRadius;
            ThemeModuleRadiusSlider.Value = theme.ModuleCornerRadius;
            ThemeHeightSlider.Value = theme.ContentHeight;
            ThemePaddingHorizontalSlider.Value = theme.PaddingHorizontal;
            ThemePaddingVerticalSlider.Value = theme.PaddingVertical;
            ThemeGapSlider.Value = theme.ModuleGap;
            ThemeBorderSlider.Value = theme.BorderThickness;
            ThemeValueSizeSlider.Value = theme.ValueFontSize;
            ThemeLabelSizeSlider.Value = theme.LabelFontSize;
            ThemeSecondarySizeSlider.Value = theme.SecondaryFontSize;
            ThemeIconsCheck.IsChecked = theme.ShowIcons;
            ThemeLabelsCheck.IsChecked = theme.ShowLabels;
            ThemeArtworkCheck.IsChecked = theme.ShowArtwork;
            ThemeVolumeCheck.IsChecked = theme.ShowVolumeSlider;
            ThemeDividersCheck.IsChecked = theme.ShowDividers;
            RefreshThemeModuleOrder(theme);
            UpdateThemeGeometryLabels();
            DrawThemePreview();
            RefreshThemeFileStatus();
        }
        finally { _syncing = syncing; }
    }

    private void Theme_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || !_themeControlsReady) return;
        var selected = SelectedKey(ThemeCombo);
        if (selected is null || HudThemeCatalog.All.All(theme => theme.Id != selected)) return;
        ApplyThemeToSettings(HudThemeCatalog.GetPreset(selected));
        _themeStatusKey = null;
        ThemeFileStatus.Visibility = Visibility.Collapsed;
    }

    private void ApplyThemeToSettings(HudTheme theme)
    {
        HudThemeCatalog.Apply(_overlay.Settings, theme);
        var syncing = _syncing;
        _syncing = true;
        try
        {
            var settings = _overlay.Settings;
            OpacitySlider.Value = settings.BackgroundOpacity;
            CompactModeCheck.IsChecked = settings.CompactMode;
            WindowsAccentCheck.IsChecked = settings.UseWindowsAccent;
            ColorHexBox.Text = settings.CustomAccentColor;
            TextColorHexBox.Text = settings.TextColor;
            BackgroundColorHexBox.Text = settings.BackgroundColor;
            GlowEnabledCheck.IsChecked = settings.GlowEnabled;
            GlowReactiveCheck.IsChecked = settings.GlowReactive;
            GlowIntensitySlider.Value = settings.GlowIntensity;
            GlowSensitivitySlider.Value = settings.GlowSensitivity;
            RefreshGlowStyles();
            UpdateGlowControls();
            UpdateAccentControls();
            RefreshOverlayColorPresets();
            UpdateOverlayColor(true, false);
            UpdateOverlayColor(false, false);
            UpdateAppearanceLabels();
            RefreshThemeControls();
        }
        finally { _syncing = syncing; }
        ApplyAndSave();
    }

    private void ThemeName_Changed(object sender, TextChangedEventArgs e)
    {
        if (_syncing || !_themeControlsReady) return;
        var theme = HudThemeCatalog.Resolve(_overlay.Settings);
        theme.Id = "custom";
        theme.Name = ThemeNameBox.Text;
        _overlay.Settings.Theme = HudThemeCatalog.Normalize(theme);
        ApplyAndSave();
    }

    private void ThemeStructure_Changed(object sender, SelectionChangedEventArgs e) => UpdateThemeStructure();
    private void ThemeGeometry_Changed(object sender, RoutedPropertyChangedEventArgs<double> e) => UpdateThemeStructure();
    private void ThemeDetails_Changed(object sender, RoutedEventArgs e) => UpdateThemeStructure();

    private void UpdateThemeStructure()
    {
        if (_syncing || !_themeControlsReady) return;
        var theme = HudThemeCatalog.Resolve(_overlay.Settings);
        theme.Id = "custom";
        theme.Name = ThemeNameBox.Text;
        theme.Layout = SelectedKey(ThemeLayoutCombo) ?? "bar";
        theme.Orientation = SelectedKey(ThemeOrientationCombo) ?? "horizontal";
        theme.FontFamily = SelectedKey(ThemeFontCombo) ?? "Segoe UI";
        theme.CornerRadius = ThemeRadiusSlider.Value;
        theme.ModuleCornerRadius = ThemeModuleRadiusSlider.Value;
        theme.ContentHeight = ThemeHeightSlider.Value;
        theme.PaddingHorizontal = ThemePaddingHorizontalSlider.Value;
        theme.PaddingVertical = ThemePaddingVerticalSlider.Value;
        theme.ModuleGap = ThemeGapSlider.Value;
        theme.BorderThickness = ThemeBorderSlider.Value;
        theme.ValueFontSize = ThemeValueSizeSlider.Value;
        theme.LabelFontSize = ThemeLabelSizeSlider.Value;
        theme.SecondaryFontSize = ThemeSecondarySizeSlider.Value;
        theme.ShowIcons = ThemeIconsCheck.IsChecked == true;
        theme.ShowLabels = ThemeLabelsCheck.IsChecked == true;
        theme.ShowArtwork = ThemeArtworkCheck.IsChecked == true;
        theme.ShowVolumeSlider = ThemeVolumeCheck.IsChecked == true;
        theme.ShowDividers = ThemeDividersCheck.IsChecked == true;
        _overlay.Settings.Theme = HudThemeCatalog.Normalize(theme);
        UpdateThemeGeometryLabels();
        UpdateGlowControls();
        ApplyAndSave();
    }

    private void UpdateThemeGeometryLabels()
    {
        static string Pixels(double value) => value.ToString("0.#", L10n.Culture);
        ThemeRadiusValue.Text = Pixels(ThemeRadiusSlider.Value);
        ThemeModuleRadiusValue.Text = Pixels(ThemeModuleRadiusSlider.Value);
        ThemeHeightValue.Text = Pixels(ThemeHeightSlider.Value);
        ThemePaddingHorizontalValue.Text = Pixels(ThemePaddingHorizontalSlider.Value);
        ThemePaddingVerticalValue.Text = Pixels(ThemePaddingVerticalSlider.Value);
        ThemeGapValue.Text = Pixels(ThemeGapSlider.Value);
        ThemeBorderValue.Text = Pixels(ThemeBorderSlider.Value);
        ThemeValueSizeValue.Text = Pixels(ThemeValueSizeSlider.Value);
        ThemeLabelSizeValue.Text = Pixels(ThemeLabelSizeSlider.Value);
        ThemeSecondarySizeValue.Text = Pixels(ThemeSecondarySizeSlider.Value);
    }

    private void MarkThemeCustomized()
    {
        // Plain color and compact changes retain the original layout until a
        // preset or a structural theme control is explicitly chosen.
        if (_syncing || !_themeControlsReady || _overlay.Settings.Theme is null) return;
        HudThemeCatalog.MarkCustom(_overlay.Settings);
    }

    private void RefreshThemePresentation()
    {
        if (!_themeControlsReady) return;
        var syncing = _syncing;
        _syncing = true;
        try { RefreshThemeChoices(); DrawThemePreview(); }
        finally { _syncing = syncing; }
    }

    private static string ThemeModuleLabel(string id) => id switch
    {
        "music" => L10n.Text("Player", "Плеер"),
        "network" => L10n.Text("Network", "Сеть"),
        "ram" => L10n.Text("RAM", "ОЗУ"),
        _ => id.ToUpperInvariant()
    };

    private void RefreshThemeModuleOrder(HudTheme theme, int selectedIndex = -1)
    {
        ThemeModuleOrderList.ItemsSource = theme.ModuleOrder.Select(id => new Choice(id, ThemeModuleLabel(id))).ToArray();
        ThemeModuleOrderList.SelectedIndex = selectedIndex;
        UpdateThemeModuleButtons();
    }

    private void ThemeModuleSelection_Changed(object sender, SelectionChangedEventArgs e) => UpdateThemeModuleButtons();

    private void UpdateThemeModuleButtons()
    {
        if (ThemeModuleUpButton is null || ThemeModuleDownButton is null) return;
        var index = ThemeModuleOrderList.SelectedIndex;
        ThemeModuleUpButton.IsEnabled = index > 0;
        ThemeModuleDownButton.IsEnabled = index >= 0 && index < ThemeModuleOrderList.Items.Count - 1;
    }

    private void ThemeModuleUp_Click(object sender, RoutedEventArgs e) => MoveThemeModule(-1);
    private void ThemeModuleDown_Click(object sender, RoutedEventArgs e) => MoveThemeModule(1);

    private void MoveThemeModule(int direction)
    {
        if (_syncing) return;
        var index = ThemeModuleOrderList.SelectedIndex;
        var theme = HudThemeCatalog.Resolve(_overlay.Settings);
        var destination = index + direction;
        if (index < 0 || destination < 0 || destination >= theme.ModuleOrder.Count) return;
        (theme.ModuleOrder[index], theme.ModuleOrder[destination]) = (theme.ModuleOrder[destination], theme.ModuleOrder[index]);
        theme.Id = "custom";
        _overlay.Settings.Theme = theme;
        RefreshThemeModuleOrder(theme, destination);
        ApplyAndSave();
    }

    private void DrawThemePreview()
    {
        var theme = HudThemeCatalog.Resolve(_overlay.Settings);
        var compact = _overlay.Settings.CompactMode;
        var density = compact ? .68 : 1;
        var vertical = theme.Orientation == "vertical";
        var cards = theme.Layout == "cards";
        var textColor = (Color)ColorConverter.ConvertFromString(theme.TextColor);
        var backgroundColor = (Color)ColorConverter.ConvertFromString(theme.BackgroundColor);
        var accentColor = (Color)ColorConverter.ConvertFromString(_overlay.CurrentAccentHex);
        var foreground = new SolidColorBrush(textColor);
        var muted = new SolidColorBrush(textColor) { Opacity = .67 };
        var background = new SolidColorBrush(backgroundColor) { Opacity = theme.BackgroundOpacity };
        var border = new SolidColorBrush(accentColor) { Opacity = .65 };
        ThemePreviewSurface.CornerRadius = new CornerRadius(theme.CornerRadius);
        ThemePreviewSurface.Background = cards ? Brushes.Transparent : background;
        ThemePreviewSurface.BorderBrush = border;
        ThemePreviewSurface.BorderThickness = cards ? new Thickness(0) : new Thickness(theme.BorderThickness);
        ThemePreviewSurface.Padding = cards ? new Thickness(0) : new Thickness(theme.PaddingHorizontal * density, theme.PaddingVertical * density, theme.PaddingHorizontal * density, theme.PaddingVertical * density);
        ThemePreviewModules.Orientation = vertical ? Orientation.Vertical : Orientation.Horizontal;
        ThemePreviewModules.Children.Clear();
        var enabledModules = theme.ModuleOrder.Where(ThemeModuleEnabled).ToArray();
        for (var index = 0; index < enabledModules.Length; index++)
        {
            var id = enabledModules[index];
            var module = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            if (id == "music" && theme.ShowArtwork && !compact)
            {
                module.Children.Add(new Border
                {
                    Width = 32, Height = 32, CornerRadius = new CornerRadius(Math.Min(theme.ModuleCornerRadius, 10)),
                    Background = border, Margin = new Thickness(0, 0, 9, 0),
                    Child = new TextBlock { Text = "G", FontSize = 18, FontWeight = FontWeights.SemiBold, Foreground = foreground, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
                });
            }
            if (theme.ShowIcons && !compact)
            {
                var icon = new TextBlock
                {
                    Text = id switch { "music" => "\uE8D6", "cpu" => "\uE950", "gpu" => "\uE7F4", "ram" => "\uE964", "network" => "\uE839", _ => "\uE9D9" },
                    FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 18 * density,
                    Foreground = border, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 7, 0)
                };
                module.Children.Add(icon);
            }
            var content = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            if (theme.ShowLabels)
                content.Children.Add(new TextBlock { Text = ThemeModuleLabel(id), FontFamily = new FontFamily(theme.FontFamily), FontSize = theme.LabelFontSize * density, Foreground = muted, TextWrapping = TextWrapping.NoWrap });
            content.Children.Add(new TextBlock
            {
                Text = id switch { "music" => "GlimHUD", "fps" => "144", "cpu" => "28%", "gpu" => "67%", "ram" => "48%", "network" => "↓ 1.8 MB/s", _ => "—" },
                FontFamily = new FontFamily(theme.FontFamily), FontSize = (id == "music" ? Math.Max(12, theme.ValueFontSize * .7) : theme.ValueFontSize) * density,
                FontWeight = FontWeights.SemiBold, Foreground = foreground, TextWrapping = TextWrapping.NoWrap
            });
            if (id is "cpu" or "gpu" or "ram")
                content.Children.Add(new TextBlock
                {
                    Text = id switch { "cpu" => "52°C", "gpu" => "64°C", _ => "15.4 / 32 GB" },
                    FontFamily = new FontFamily(theme.FontFamily), FontSize = theme.SecondaryFontSize * density,
                    Foreground = muted, TextWrapping = TextWrapping.NoWrap
                });
            module.Children.Add(content);
            if (id == "music" && theme.ShowVolumeSlider && !compact)
            {
                module.Children.Add(new Border { Width = 36, Height = 3, Background = border, CornerRadius = new CornerRadius(1.5), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(9, 0, 0, 0) });
            }
            var moduleBorder = new Border
            {
                Child = module, CornerRadius = new CornerRadius(theme.ModuleCornerRadius),
                Background = cards ? background : Brushes.Transparent,
                BorderBrush = border, BorderThickness = cards ? new Thickness(theme.BorderThickness) : new Thickness(0),
                MinHeight = theme.ContentHeight * density,
                Padding = cards ? new Thickness(theme.PaddingHorizontal * density, theme.PaddingVertical * density, theme.PaddingHorizontal * density, theme.PaddingVertical * density) : new Thickness(0),
                Margin = index == enabledModules.Length - 1 ? new Thickness(0) : vertical ? new Thickness(0, 0, 0, theme.ModuleGap) : new Thickness(0, 0, theme.ModuleGap, 0)
            };
            ThemePreviewModules.Children.Add(moduleBorder);
            if (!cards && theme.ShowDividers && index < enabledModules.Length - 1)
            {
                ThemePreviewModules.Children.Add(new Border
                {
                    Background = border, Width = vertical ? double.NaN : 1, Height = vertical ? 1 : Math.Max(12, theme.ContentHeight * density * .55),
                    VerticalAlignment = VerticalAlignment.Center, Margin = vertical ? new Thickness(0, 0, 0, theme.ModuleGap) : new Thickness(0, 0, theme.ModuleGap, 0)
                });
            }
        }
    }

    private bool ThemeModuleEnabled(string id) => id switch
    {
        "music" => _overlay.Settings.ShowMusic, "fps" => _overlay.Settings.ShowFps,
        "cpu" => _overlay.Settings.ShowCpu, "gpu" => _overlay.Settings.ShowGpu,
        "ram" => _overlay.Settings.ShowRam, "network" => _overlay.Settings.ShowNetwork,
        _ => false
    };

    private void ImportTheme_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = FormatUi("Loc.ThemeImportTitle"), Filter = FormatUi("Loc.ThemeFileFilter"),
            DefaultExt = ".json", CheckFileExists = true, Multiselect = false
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            // Parse and validate fully before changing the active theme.
            var imported = HudThemeCatalog.ImportFile(dialog.FileName);
            imported.Id = "custom";
            ApplyThemeToSettings(imported);
            SetThemeFileStatus("Loc.ThemeImported", false, imported.Name);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or FormatException or ArgumentException or NotSupportedException)
        {
            App.LogError(error);
            SetThemeFileStatus("Loc.ThemeImportFailed", true);
        }
    }

    private void ExportTheme_Click(object sender, RoutedEventArgs e)
    {
        var name = HudThemeCatalog.Resolve(_overlay.Settings).Name;
        var filename = new string(name.Select(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' ? character : '-').ToArray()).Trim('-');
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = FormatUi("Loc.ThemeExportTitle"), Filter = FormatUi("Loc.ThemeFileFilter"),
            DefaultExt = ".json", AddExtension = true, FileName = $"GlimHUD-{(filename.Length > 0 ? filename : "theme")}.json"
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            HudThemeCatalog.ExportFile(_overlay.Settings, dialog.FileName);
            SetThemeFileStatus("Loc.ThemeExported", false, Path.GetFileName(dialog.FileName));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            App.LogError(error);
            SetThemeFileStatus("Loc.ThemeExportFailed", true);
        }
    }

    private void ResetTheme_Click(object sender, RoutedEventArgs e)
    {
        ApplyThemeToSettings(HudThemeCatalog.GetPreset("classic"));
        SetThemeFileStatus("Loc.ThemeResetDone", false);
    }

    private void SetThemeFileStatus(string key, bool failed, params object[] arguments)
    {
        _themeStatusKey = key;
        _themeStatusArguments = arguments;
        _themeStatusFailed = failed;
        RefreshThemeFileStatus();
    }

    private void RefreshThemeFileStatus()
    {
        if (_themeStatusKey is null) return;
        ThemeFileStatus.Text = FormatUi(_themeStatusKey, _themeStatusArguments);
        ThemeFileStatus.Visibility = Visibility.Visible;
        if (_themeStatusFailed) ThemeFileStatus.Foreground = new SolidColorBrush(Color.FromRgb(246, 162, 159));
        else ThemeFileStatus.ClearValue(TextBlock.ForegroundProperty);
    }
}
