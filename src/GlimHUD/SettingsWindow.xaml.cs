using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using GlimHUD.Localization;
using Forms = System.Windows.Forms;

namespace GlimHUD;

public partial class SettingsWindow : Window
{
    private readonly OverlayWindow _overlay;
    private readonly DispatcherTimer _saveTimer;
    private bool _syncing = true;
    private int? _pinnedPid;

    public SettingsWindow(OverlayWindow overlay)
    {
        _overlay = overlay;
        InitializeComponent();
        Language = System.Windows.Markup.XmlLanguage.GetLanguage(L10n.Culture.Name);
        var version = typeof(App).Assembly.GetName().Version;
        if (version is not null) VersionBadge.Text = $"{version.Major}.{version.Minor}.{version.Build}";
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        _saveTimer.Tick += SaveTimer_Tick;
        var settings = overlay.Settings;
        LanguageCombo.ItemsSource = new[] { new Choice("en", "English"), new Choice("ru", "Русский") };
        LanguageCombo.SelectedValue = L10n.NormalizeLanguage(settings.Language);
        OpacitySlider.Value = settings.BackgroundOpacity;
        ScaleSlider.Value = settings.Scale;
        MusicCheck.IsChecked = settings.ShowMusic;
        FpsCheck.IsChecked = settings.ShowFps;
        CpuCheck.IsChecked = settings.ShowCpu;
        GpuCheck.IsChecked = settings.ShowGpu;
        RamCheck.IsChecked = settings.ShowRam;
        LockedCheck.IsChecked = settings.Locked;
        WheelVolumeCheck.IsChecked = settings.ScrollMusicVolume;
        AutoShowCheck.IsChecked = settings.AutoShowInGames;
        WindowsAccentCheck.IsChecked = settings.UseWindowsAccent;
        ColorHexBox.Text = settings.CustomAccentColor;
        UpdateAccentControls();
        RefreshGamePaths();
        _pinnedPid = overlay.FpsSnapshot is { IsPinned: true } fps ? fps.ProcessId : null;
        RefreshSnapshots();
        UpdateAppearanceLabels();
        _syncing = false;
        _overlay.SnapshotsUpdated += Overlay_SnapshotsUpdated;
        L10n.LanguageChanged += LanguageChanged;
        MaxHeight = Math.Max(540, SystemParameters.WorkArea.Height - 20);
    }

    private void Window_Loaded(object sender, RoutedEventArgs e) => RefreshFpsTargets();

    private void Overlay_SnapshotsUpdated(object? sender, EventArgs e) => RefreshSnapshots();

    private void Language_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing) return;
        _overlay.Settings.Language = SelectedKey(LanguageCombo) ?? "en";
        ApplyAndSave();
    }

    private void LanguageChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => LanguageChanged(sender, e));
            return;
        }
        var wasSyncing = _syncing;
        _syncing = true;
        try
        {
            LanguageCombo.SelectedValue = L10n.Language;
            RefreshSnapshots();
            RefreshFpsTargets();
            RefreshGamePaths();
            UpdateAppearanceLabels();
        }
        finally { _syncing = wasSyncing; }
    }

    private void RefreshSnapshots()
    {
        var wasSyncing = _syncing;
        _syncing = true;
        try
        {
            var media = _overlay.MediaSnapshot;
            var mediaChoices = new List<Choice> { new("", L10n.Text("Automatic — prefer music", "Автоматически — музыка в приоритете")) };
            if (media is not null)
                mediaChoices.AddRange(media.Sessions.Select(item => new Choice(item.SourceAppId, item.DisplayName)));
            UpdateChoices(MediaCombo, mediaChoices, _overlay.Settings.MediaSourceAppId);
            MediaStatus.Text = media is { IsAvailable: true } ? $"{media.Title} · {media.Artist}" : media?.Status ?? L10n.Text("Looking for media sessions…", "Поиск системных медиасессий…");

            var audio = _overlay.AudioSnapshot;
            var audioChoices = new List<Choice> { new("", L10n.Text("Automatic — player application", "Автоматически — приложение плеера")) };
            if (audio is not null)
                audioChoices.AddRange(audio.Sessions.Select(item => new Choice(item.Id, $"{item.DisplayName} · PID {item.ProcessId}")));
            UpdateChoices(AudioCombo, audioChoices, _overlay.Settings.AudioSessionId);
            AudioStatus.Text = audio is { IsAvailable: true } ? L10n.Text("Volume: {0}", "Громкость: {0}", audio.DisplayName) : audio?.Status ?? L10n.Text("Looking for audio applications…", "Поиск аудиоприложений…");

            var hardware = _overlay.TelemetrySnapshot;
            var gpuChoices = new List<Choice> { new("", L10n.Text("Automatic — active graphics card", "Автоматически — активная видеокарта")) };
            if (hardware is not null)
                gpuChoices.AddRange(hardware.AvailableGpus.Select(item => new Choice(item.Identifier, item.Name)));
            UpdateChoices(GpuCombo, gpuChoices, _overlay.Settings.GpuIdentifier);
            HardwareStatus.Text = hardware is null ? L10n.Text("Connecting to sensors…", "Подключение датчиков…") : string.Join("\n", new[] { hardware.CpuName, hardware.Status }.Where(value => !string.IsNullOrWhiteSpace(value)));
            FpsStatus.Text = _overlay.FpsSnapshot?.Status ?? L10n.Text("Connecting to the frame counter…", "Подключение счётчика кадров…");
            LockedCheck.IsChecked = _overlay.Settings.Locked;
            MusicCheck.IsChecked = _overlay.Settings.ShowMusic;
            FpsCheck.IsChecked = _overlay.Settings.ShowFps;
            CpuCheck.IsChecked = _overlay.Settings.ShowCpu;
            GpuCheck.IsChecked = _overlay.Settings.ShowGpu;
            RamCheck.IsChecked = _overlay.Settings.ShowRam;
            WheelVolumeCheck.IsChecked = _overlay.Settings.ScrollMusicVolume;
            AutoShowCheck.IsChecked = _overlay.Settings.AutoShowInGames;
        }
        finally { _syncing = wasSyncing; }
    }

    private static void UpdateChoices(ComboBox combo, List<Choice> choices, string? selected)
    {
        selected ??= "";
        if (selected.Length > 0 && choices.All(item => item.Key != selected))
            choices.Add(new(selected, L10n.Text("Selected source is currently unavailable", "Выбранный источник сейчас недоступен")));
        if (combo.ItemsSource is not IEnumerable<Choice> existing || !existing.SequenceEqual(choices))
            combo.ItemsSource = choices;
        combo.SelectedValue = selected;
    }

    private void RefreshFpsTargets()
    {
        var wasSyncing = _syncing;
        _syncing = true;
        try
        {
            var choices = new List<Choice> { new("", L10n.Text("Automatic — active game window", "Автоматически — активное окно игры")) };
            choices.AddRange(_overlay.GetFpsTargets().Select(item => new Choice(item.ProcessId.ToString(CultureInfo.InvariantCulture), item.DisplayName)));
            UpdateChoices(FpsCombo, choices, _pinnedPid?.ToString(CultureInfo.InvariantCulture));
        }
        finally { _syncing = wasSyncing; }
    }

    private void Appearance_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncing) return;
        _overlay.Settings.BackgroundOpacity = OpacitySlider.Value;
        _overlay.Settings.Scale = ScaleSlider.Value;
        UpdateAppearanceLabels();
        ApplyAndSave();
    }

    private void UpdateAppearanceLabels()
    {
        OpacityValue.Text = $"{OpacitySlider.Value * 100:0}%";
        ScaleValue.Text = $"{ScaleSlider.Value * 100:0}%";
    }

    private void Modules_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        var settings = _overlay.Settings;
        settings.ShowMusic = MusicCheck.IsChecked == true;
        settings.ShowFps = FpsCheck.IsChecked == true;
        settings.ShowCpu = CpuCheck.IsChecked == true;
        settings.ShowGpu = GpuCheck.IsChecked == true;
        settings.ShowRam = RamCheck.IsChecked == true;
        ApplyAndSave();
    }

    private void Lock_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        _overlay.Settings.Locked = LockedCheck.IsChecked == true;
        ApplyAndSave();
    }

    private void PlayerOptions_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        _overlay.Settings.ScrollMusicVolume = WheelVolumeCheck.IsChecked == true;
        ApplyAndSave();
    }

    private void AutoShow_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        _overlay.Settings.AutoShowInGames = AutoShowCheck.IsChecked == true;
        ApplyAndSave();
    }

    private void AccentMode_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        _overlay.Settings.UseWindowsAccent = WindowsAccentCheck.IsChecked == true;
        UpdateAccentControls();
        ApplyAndSave();
    }

    private void UpdateAccentControls()
    {
        var systemColor = WindowsAccentCheck.IsChecked == true;
        ColorHexBox.IsEnabled = ChooseColorButton.IsEnabled = !systemColor;
        if (systemColor)
        {
            CustomColorSwatch.SetResourceReference(Border.BackgroundProperty, "AccentBrush");
            ColorError.Visibility = Visibility.Collapsed;
        }
        else
        {
            UpdateCustomColor(false);
        }
    }

    private void ColorHex_Changed(object sender, TextChangedEventArgs e)
    {
        if (_syncing) return;
        UpdateCustomColor(true);
    }

    private void ColorHex_LostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_syncing || !TryParseCustomColor(ColorHexBox.Text, out _, out var normalized)) return;
        ColorHexBox.Text = normalized;
    }

    private void UpdateCustomColor(bool save)
    {
        if (WindowsAccentCheck.IsChecked == true) return;
        if (!TryParseCustomColor(ColorHexBox.Text, out var color, out var normalized))
        {
            ColorError.Visibility = Visibility.Visible;
            ColorHexBox.BorderBrush = new SolidColorBrush(Color.FromRgb(246, 162, 159));
            return;
        }
        ColorError.Visibility = Visibility.Collapsed;
        ColorHexBox.ClearValue(BorderBrushProperty);
        CustomColorSwatch.Background = new SolidColorBrush(color);
        if (!save) return;
        _overlay.Settings.CustomAccentColor = normalized;
        ApplyAndSave();
    }

    private static bool TryParseCustomColor(string? text, out Color color, out string normalized)
    {
        color = default;
        normalized = string.Empty;
        var value = text?.Trim();
        if (value?.Length == 6) value = "#" + value;
        if (value is not { Length: 7 } || value[0] != '#'
            || !byte.TryParse(value.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var red)
            || !byte.TryParse(value.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var green)
            || !byte.TryParse(value.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var blue)) return false;
        color = Color.FromRgb(red, green, blue);
        normalized = $"#{red:X2}{green:X2}{blue:X2}";
        return true;
    }

    private void ChooseColor_Click(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        if (!TryParseCustomColor(ColorHexBox.Text, out var current, out _))
            TryParseCustomColor(_overlay.Settings.CustomAccentColor, out current, out _);
        using var dialog = new Forms.ColorDialog
        {
            FullOpen = true,
            AnyColor = true,
            Color = System.Drawing.Color.FromArgb(current.R, current.G, current.B)
        };
        if (dialog.ShowDialog(new DialogOwner(new WindowInteropHelper(this).Handle)) == Forms.DialogResult.OK)
        {
            var color = dialog.Color;
            ColorHexBox.Text = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        }
    }

    private void RefreshGamePaths()
    {
        var selected = (GamePathsList.SelectedItem as GamePathEntry)?.FullPath;
        var choices = _overlay.Settings.GameExecutablePaths.Select(path => new GamePathEntry(path)).ToArray();
        GamePathsList.ItemsSource = choices;
        GamePathsList.SelectedItem = choices.FirstOrDefault(item => string.Equals(item.FullPath, selected, StringComparison.OrdinalIgnoreCase));
        RemoveGameButton.IsEnabled = GamePathsList.SelectedItem is not null;
        GamePathsStatus.Text = choices.Length == 0
            ? L10n.Text("No additional games added yet.", "Дополнительные игры пока не добавлены.")
            : L10n.Text("Games added: {0}", "Добавлено игр: {0}", choices.Length);
    }

    private void AddGame_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = L10n.Text("Select a game executable", "Выберите исполняемый файл игры"),
            Filter = L10n.Text("Applications (*.exe)|*.exe", "Приложения (*.exe)|*.exe"),
            CheckFileExists = true,
            Multiselect = true
        };
        if (dialog.ShowDialog(this) != true) return;
        var paths = _overlay.Settings.GameExecutablePaths;
        foreach (var selectedPath in dialog.FileNames)
        {
            try
            {
                var path = Path.GetFullPath(selectedPath);
                if (!File.Exists(path) || !string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase)) continue;
                if (paths.Count >= 64) break;
                if (!paths.Contains(path, StringComparer.OrdinalIgnoreCase)) paths.Add(path);
            }
            catch (Exception error) when (error is IOException or ArgumentException or UnauthorizedAccessException or NotSupportedException) { }
        }
        RefreshGamePaths();
        ApplyAndSave();
    }

    private void RemoveGame_Click(object sender, RoutedEventArgs e)
    {
        if (GamePathsList.SelectedItem is not GamePathEntry selected) return;
        _overlay.Settings.GameExecutablePaths.RemoveAll(path => string.Equals(path, selected.FullPath, StringComparison.OrdinalIgnoreCase));
        RefreshGamePaths();
        ApplyAndSave();
    }

    private void GamePath_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => RemoveGameButton.IsEnabled = GamePathsList.SelectedItem is not null;

    private void Media_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing) return;
        _overlay.Settings.MediaSourceAppId = SelectedKey(MediaCombo);
        _overlay.Settings.AudioSessionId = null;
        ApplyAndSave();
    }

    private void Audio_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing) return;
        _overlay.Settings.AudioSessionId = SelectedKey(AudioCombo);
        ApplyAndSave();
    }

    private void Gpu_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing) return;
        _overlay.Settings.GpuIdentifier = SelectedKey(GpuCombo);
        ApplyAndSave();
    }

    private void Fps_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing) return;
        var selected = SelectedKey(FpsCombo);
        _pinnedPid = int.TryParse(selected, out var pid) ? pid : null;
        _overlay.SetFpsTarget(_pinnedPid);
    }

    private void Fps_DropDownOpened(object? sender, EventArgs e) => RefreshFpsTargets();
    private static string? SelectedKey(ComboBox combo) => combo.SelectedValue is string value && value.Length > 0 ? value : null;

    private void ApplyAndSave()
    {
        _overlay.ApplySettings();
        SaveStatus.SetResourceReference(TextBlock.TextProperty, "Loc.Saving");
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void SaveTimer_Tick(object? sender, EventArgs e)
    {
        _saveTimer.Stop();
        try { _overlay.SaveSettings(); SaveStatus.SetResourceReference(TextBlock.TextProperty, "Loc.Saved"); }
        catch { SaveStatus.SetResourceReference(TextBlock.TextProperty, "Loc.SaveFailed"); }
    }

    private void Title_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left) DragMove();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_Closed(object? sender, EventArgs e)
    {
        _overlay.SnapshotsUpdated -= Overlay_SnapshotsUpdated;
        L10n.LanguageChanged -= LanguageChanged;
        _saveTimer.Stop();
        try { _overlay.SaveSettings(); } catch { /* Overlay reports persistence failures independently. */ }
    }

    private sealed record Choice(string Key, string Label)
    {
        public override string ToString() => Label;
    }

    private sealed record GamePathEntry(string FullPath)
    {
        public string Name => Path.GetFileNameWithoutExtension(FullPath);
    }

    private sealed class DialogOwner(IntPtr handle) : Forms.IWin32Window
    {
        public IntPtr Handle { get; } = handle;
    }
}
