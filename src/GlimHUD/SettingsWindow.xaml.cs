using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using GlimHUD.Localization;
using GlimHUD.Models;
using GlimHUD.Native;
using GlimHUD.Services;
using Forms = System.Windows.Forms;

namespace GlimHUD;

public partial class SettingsWindow : Window
{
    private readonly OverlayWindow _overlay;
    private readonly DispatcherTimer _saveTimer;
    private readonly DispatcherTimer _glowSignalTimer;
    private readonly StartupService _startup = new();
    private bool _syncing = true;
    private int? _pinnedPid;
    private bool _requestingRestart;
    private string? _failedProjectUrl;
    private bool _updateActionPending;
    private bool _updateActionFailed;
    private bool _updateInstalling;
    private bool _updateLinkFailed;
    private bool _isClosed;
    private IReadOnlyList<NetworkAdapterInfo> _networkAdapters = [];

    public SettingsWindow(OverlayWindow overlay)
    {
        _overlay = overlay;
        InitializeComponent();
        Language = System.Windows.Markup.XmlLanguage.GetLanguage(L10n.Culture.Name);
        var version = typeof(App).Assembly.GetName().Version;
        if (version is not null) VersionBadge.Text = $"{version.Major}.{version.Minor}.{version.Build}";
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        _saveTimer.Tick += SaveTimer_Tick;
        _glowSignalTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _glowSignalTimer.Tick += (_, _) => UpdateGlowSignal();
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
        SnapGridCheck.IsChecked = settings.SnapToGrid;
        WheelVolumeCheck.IsChecked = settings.ScrollMusicVolume;
        AutoShowCheck.IsChecked = settings.AutoShowInGames;
        WindowsAccentCheck.IsChecked = settings.UseWindowsAccent;
        GlowEnabledCheck.IsChecked = settings.GlowEnabled;
        GlowReactiveCheck.IsChecked = settings.GlowReactive;
        GlowIntensitySlider.Value = settings.GlowIntensity;
        GlowSensitivitySlider.Value = settings.GlowSensitivity;
        RefreshGlowStyles();
        UpdateGlowControls();
        ColorHexBox.Text = settings.CustomAccentColor;
        UpdateAccentControls();
        TextColorHexBox.Text = settings.TextColor;
        BackgroundColorHexBox.Text = settings.BackgroundColor;
        RefreshOverlayColorPresets();
        UpdateOverlayColor(true, false);
        UpdateOverlayColor(false, false);
        RefreshGamePaths();
        _pinnedPid = overlay.FpsSnapshot is { IsPinned: true } fps ? fps.ProcessId : null;
        RefreshSnapshots();
        UpdateAppearanceLabels();
        RefreshStartupState();
        RefreshUpdateControls();
        _syncing = false;
        _overlay.SnapshotsUpdated += Overlay_SnapshotsUpdated;
        L10n.LanguageChanged += LanguageChanged;
        _overlay.AppUpdates.Changed += AppUpdates_Changed;
        Activated += (_, _) => RefreshStartupState();
        IsVisibleChanged += (_, _) =>
        {
            UpdateGlowSignalMonitoring();
            _overlay.SetNetworkSettingsVisible(IsLoaded && IsVisible && !_isClosed);
        };
        MaxHeight = Math.Max(540, SystemParameters.WorkArea.Height - 20);
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        RefreshFpsTargets();
        UpdateGlowSignalMonitoring();
        _overlay.SetNetworkSettingsVisible(IsVisible && !_isClosed);
    }

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
            RefreshOverlayColorPresets();
            RefreshGlowStyles();
            RefreshStartupState();
            UpdateProjectLinkStatus();
            RefreshUpdateControls();
        }
        finally { _syncing = wasSyncing; }
    }

    private void RefreshSnapshots()
    {
        var wasSyncing = _syncing;
        _syncing = true;
        try
        {
            RefreshHotkeys();
            RefreshNetworkControls();
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
            var fps = _overlay.FpsSnapshot;
            FpsRecoveryActions.Visibility = fps is { IsRunning: false } ? Visibility.Visible : Visibility.Collapsed;
            RestartAdminButton.Visibility = fps is { RequiresElevation: true } && AppRestartService.CanRequestElevation
                ? Visibility.Visible : Visibility.Collapsed;
            FpsAccessHint.Visibility = RestartAdminButton.Visibility;
            LockedCheck.IsChecked = _overlay.Settings.Locked;
            SnapGridCheck.IsChecked = _overlay.Settings.SnapToGrid;
            MusicCheck.IsChecked = _overlay.Settings.ShowMusic;
            FpsCheck.IsChecked = _overlay.Settings.ShowFps;
            CpuCheck.IsChecked = _overlay.Settings.ShowCpu;
            GpuCheck.IsChecked = _overlay.Settings.ShowGpu;
            RamCheck.IsChecked = _overlay.Settings.ShowRam;
            NetworkCheck.IsChecked = _overlay.Settings.ShowNetwork;
            WheelVolumeCheck.IsChecked = _overlay.Settings.ScrollMusicVolume;
            AutoShowCheck.IsChecked = _overlay.Settings.AutoShowInGames;
            GlowEnabledCheck.IsChecked = _overlay.Settings.GlowEnabled;
            GlowReactiveCheck.IsChecked = _overlay.Settings.GlowReactive;
            GlowStyleCombo.SelectedValue = GlowStyles.Normalize(_overlay.Settings.GlowStyle);
            if (!GlowIntensitySlider.IsMouseCaptureWithin) GlowIntensitySlider.Value = _overlay.Settings.GlowIntensity;
            if (!GlowSensitivitySlider.IsMouseCaptureWithin) GlowSensitivitySlider.Value = _overlay.Settings.GlowSensitivity;
            UpdateGlowControls();
        }
        finally { _syncing = wasSyncing; }
    }

    private void GlowOption_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        _overlay.Settings.GlowEnabled = GlowEnabledCheck.IsChecked == true;
        _overlay.Settings.GlowReactive = GlowReactiveCheck.IsChecked == true;
        UpdateGlowControls();
        ApplyAndSave();
    }

    private void RefreshNetworkControls()
    {
        var snapshot = _overlay.NetworkSnapshot;
        if (snapshot is not null) _networkAdapters = snapshot.AvailableAdapters;
        var choices = new List<Choice> { new("", FormatUi("Loc.NetworkAutomatic")) };
        choices.AddRange(_networkAdapters.Select(adapter => new Choice(adapter.Id,
            $"{adapter.Name} · {FormatUi(adapter.IsPhysical ? "Loc.NetworkPhysical" : "Loc.NetworkVirtual")}" +
            (adapter.IsOperational ? "" : $" · {FormatUi("Loc.NetworkInactive")}"))));
        var selectedId = _overlay.Settings.NetworkAdapterId;
        var selectedAdapter = _networkAdapters.FirstOrDefault(adapter => string.Equals(adapter.Id, selectedId, StringComparison.OrdinalIgnoreCase));
        UpdateChoices(NetworkAdapterCombo, choices, selectedAdapter?.Id ?? selectedId);
        NetworkAdapterNameValue.Text = snapshot?.AdapterName is { Length: > 0 } adapterName ? adapterName
            : selectedAdapter?.Name ?? FormatUi(selectedId is null ? "Loc.NetworkAutomatic" : "Loc.NetworkSelectedAdapter");
        NetworkAdapterNameValue.ToolTip = selectedAdapter?.Description;
        NetworkStatusText.SetResourceReference(TextBlock.TextProperty, snapshot?.Status switch
        {
            NetworkStatus.Ready => "Loc.NetworkReady",
            NetworkStatus.NoAdapter => "Loc.NetworkNoAdapter",
            NetworkStatus.AdapterMissing => "Loc.NetworkAdapterMissing",
            NetworkStatus.AdapterDown => "Loc.NetworkAdapterDown",
            NetworkStatus.ReadError => "Loc.NetworkReadError",
            NetworkStatus.Unsupported => "Loc.NetworkUnsupported",
            _ => "Loc.NetworkWarmingUp"
        });
        NetworkDownloadValue.Text = NetworkValueFormatter.FormatRate(snapshot?.DownloadBytesPerSecond);
        NetworkUploadValue.Text = NetworkValueFormatter.FormatRate(snapshot?.UploadBytesPerSecond);
        NetworkReceivedValue.Text = NetworkValueFormatter.FormatBytes(snapshot?.SessionReceivedBytes);
        NetworkSentValue.Text = NetworkValueFormatter.FormatBytes(snapshot?.SessionSentBytes);
        NetworkLinkSpeedValue.Text = NetworkValueFormatter.FormatLinkSpeed(snapshot?.LinkSpeedBitsPerSecond);
        DrawNetworkHistory();
    }

    private void NetworkAdapter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing) return;
        _overlay.Settings.NetworkAdapterId = SelectedKey(NetworkAdapterCombo);
        ApplyAndSave();
        var wasSyncing = _syncing;
        _syncing = true;
        try { RefreshNetworkControls(); }
        finally { _syncing = wasSyncing; }
    }

    private void NetworkHistory_SizeChanged(object sender, SizeChangedEventArgs e) => DrawNetworkHistory();

    private void DrawNetworkHistory()
    {
        if (NetworkDownloadHistory is null || NetworkUploadHistory is null) return;
        var snapshot = _overlay.NetworkSnapshot;
        var end = snapshot?.SampledAt ?? DateTimeOffset.UtcNow;
        var start = end.AddSeconds(-60);
        var history = snapshot?.History.Where(sample => sample.SampledAt >= start && sample.SampledAt <= end).ToArray() ?? [];
        var rates = history.SelectMany(sample => new[] { sample.DownloadBytesPerSecond, sample.UploadBytesPerSecond })
            .Where(IsNetworkRate).Select(rate => rate!.Value).ToArray();
        NetworkHistoryEmpty.Visibility = rates.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        var maximum = Math.Max(1000, rates.DefaultIfEmpty(0).Max());
        NetworkHistoryScaleValue.Text = FormatUi("Loc.NetworkHistoryScale", NetworkValueFormatter.FormatRate(rates.Length == 0 ? null : maximum));
        var width = NetworkHistoryPlot.ActualWidth;
        var height = NetworkHistoryPlot.ActualHeight;
        NetworkDownloadHistory.Data = BuildNetworkHistory(history, start, maximum, width, height, sample => sample.DownloadBytesPerSecond);
        NetworkUploadHistory.Data = BuildNetworkHistory(history, start, maximum, width, height, sample => sample.UploadBytesPerSecond);
    }

    private static bool IsNetworkRate(double? value) => value is { } rate && double.IsFinite(rate) && rate >= 0;

    private static Geometry BuildNetworkHistory(IReadOnlyList<NetworkHistorySample> history, DateTimeOffset start,
        double maximum, double width, double height, Func<NetworkHistorySample, double?> selectRate)
    {
        if (width <= 4 || height <= 4) return Geometry.Empty;
        var geometry = new StreamGeometry();
        using (var drawing = geometry.Open())
        {
            var connected = false;
            var previousAt = default(DateTimeOffset);
            foreach (var sample in history)
            {
                var rate = selectRate(sample);
                if (!IsNetworkRate(rate)) { connected = false; continue; }
                if (connected && (sample.SampledAt - previousAt).TotalSeconds > 1.75) connected = false;
                var point = new Point(2 + (sample.SampledAt - start).TotalSeconds / 60 * (width - 4),
                    height - 2 - rate!.Value / maximum * (height - 4));
                if (connected) drawing.LineTo(point, isStroked: true, isSmoothJoin: true);
                else drawing.BeginFigure(point, isFilled: false, isClosed: false);
                connected = true;
                previousAt = sample.SampledAt;
            }
        }
        geometry.Freeze();
        return geometry;
    }

    private void RefreshGlowStyles()
    {
        GlowStyleCombo.ItemsSource = GlowStyles.All.Select(style => new Choice(style.Id,
            (string)FindResource(style.LabelKey))).ToArray();
        GlowStyleCombo.SelectedValue = GlowStyles.Normalize(_overlay.Settings.GlowStyle);
    }

    private void GlowStyle_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing) return;
        _overlay.Settings.GlowStyle = GlowStyles.Normalize(SelectedKey(GlowStyleCombo));
        UpdateGlowControls();
        ApplyAndSave();
    }

    private void GlowIntensity_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncing) return;
        _overlay.Settings.GlowIntensity = Math.Clamp(e.NewValue, 0, 1);
        UpdateGlowControls();
        ApplyAndSave();
    }

    private void UpdateGlowControls()
    {
        var settings = _overlay.Settings;
        GlowOptions.IsEnabled = settings.GlowEnabled;
        GlowSensitivityOptions.IsEnabled = settings.GlowReactive;
        var style = GlowStyles.All.First(item => item.Id == GlowStyles.Normalize(settings.GlowStyle));
        GlowStyleHint.SetResourceReference(TextBlock.TextProperty, style.DescriptionKey);
        GlowIntensityValue.Text = $"{Math.Round(settings.GlowIntensity * 100):0}%";
        GlowSensitivityValue.Text = $"{Math.Round(settings.GlowSensitivity * 100):0}%";
        var hint = !settings.GlowEnabled || !settings.GlowReactive ? null
            : _overlay.AudioSnapshot is not { IsAvailable: true } ? "Loc.GlowAudioUnavailable"
            : !_overlay.GlowAudioAvailable ? "Loc.GlowMeterWaiting"
            : _overlay.MediaSnapshot is { IsAvailable: true, IsPlaying: false } ? "Loc.GlowWaiting" : null;
        GlowAudioStatus.Visibility = hint is null ? Visibility.Collapsed : Visibility.Visible;
        if (hint is not null) GlowAudioStatus.SetResourceReference(TextBlock.TextProperty, hint);
        UpdateGlowSignalMonitoring();
    }

    private void UpdateGlowSignalMonitoring()
    {
        if (IsLoaded && IsVisible && _overlay.Settings.GlowEnabled && _overlay.Settings.GlowReactive)
        {
            UpdateGlowSignal();
            _glowSignalTimer.Start();
        }
        else
        {
            _glowSignalTimer.Stop();
            GlowSignalMeter.Value = 0;
            GlowSignalValue.Text = "0%";
        }
    }

    private void UpdateGlowSignal()
    {
        var peak = _overlay.GlowAudioPeak;
        peak = double.IsFinite(peak) ? Math.Clamp(peak, 0, 1) : 0;
        GlowSignalMeter.Value = peak;
        GlowSignalValue.Text = $"{peak * 100:0}%";
    }

    private void GlowSensitivity_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncing) return;
        _overlay.Settings.GlowSensitivity = Math.Clamp(e.NewValue, 0, 1);
        UpdateGlowControls();
        ApplyAndSave();
    }

    private void OpenIssues_Click(object sender, RoutedEventArgs e) => OpenProjectLink(ProjectLinks.IssuesUrl);

    private void AppUpdates_Changed(object? sender, EventArgs e)
    {
        if (_isClosed || Dispatcher.HasShutdownStarted) return;
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => RefreshUpdateControls());
            return;
        }
        RefreshUpdateControls();
    }

    private void RefreshUpdateControls()
    {
        if (_isClosed) return;
        var snapshot = _overlay.AppUpdates.Snapshot;
        var installed = snapshot.IsInstalled && !snapshot.IsPackaged;
        var busy = _updateActionPending || snapshot.State is UpdateState.Checking or UpdateState.Downloading;
        var wasSyncing = _syncing;
        _syncing = true;
        try
        {
            AutomaticUpdatesCheck.IsChecked = _overlay.Settings.CheckForUpdates;
            AutomaticUpdatesCheck.IsEnabled = installed;
        }
        finally { _syncing = wasSyncing; }

        UpdateCurrentVersion.Text = FormatUi("Loc.UpdateCurrentVersion", snapshot.CurrentVersion);
        UpdateModeHint.SetResourceReference(TextBlock.TextProperty, snapshot.IsPackaged
            ? "Loc.PackagedUpdatesHint" : installed ? "Loc.AutomaticUpdatesHint" : "Loc.PortableUpdatesHint");
        var version = snapshot.AvailableVersion ?? "—";
        var progress = double.IsFinite(snapshot.Progress) ? Math.Clamp(snapshot.Progress, 0, 1) : 0;
        UpdateStatusText.Text = _updateInstalling ? FormatUi("Loc.UpdateStartingInstaller")
            : _updateActionFailed ? FormatUi(UpdateFailureResource(snapshot.ErrorCode)) : snapshot.State switch
            {
                UpdateState.Disabled => FormatUi("Loc.UpdateDisabled"),
                UpdateState.Portable => FormatUi("Loc.UpdatePortable"),
                UpdateState.Checking => FormatUi("Loc.UpdateChecking"),
                UpdateState.UpToDate => FormatUi("Loc.UpdateLatest"),
                UpdateState.Available => FormatUi("Loc.UpdateAvailable", version),
                UpdateState.Downloading => FormatUi("Loc.UpdateDownloading", progress * 100),
                UpdateState.Downloaded => FormatUi("Loc.UpdateVerified", version),
                UpdateState.Failed => FormatUi(UpdateFailureResource(snapshot.ErrorCode)),
                _ => FormatUi("Loc.UpdateReady")
            };
        if (_updateActionFailed || snapshot.State == UpdateState.Failed && snapshot.ErrorCode != "Cancelled")
            UpdateStatusText.Foreground = new SolidColorBrush(Color.FromRgb(246, 162, 159));
        else UpdateStatusText.ClearValue(TextBlock.ForegroundProperty);
        UpdateProgressBar.Visibility = _updateInstalling || snapshot.State is UpdateState.Checking or UpdateState.Downloading
            ? Visibility.Visible : Visibility.Collapsed;
        UpdateProgressBar.IsIndeterminate = _updateInstalling || snapshot.State == UpdateState.Checking;
        UpdateProgressBar.Value = progress;
        CheckUpdatesButton.Visibility = installed ? Visibility.Visible : Visibility.Collapsed;
        CheckUpdatesButton.IsEnabled = installed && !busy && snapshot.State is not (UpdateState.Disabled or UpdateState.Downloaded);
        DownloadUpdateButton.Visibility = installed && snapshot.State == UpdateState.Available ? Visibility.Visible : Visibility.Collapsed;
        DownloadUpdateButton.IsEnabled = !busy;
        InstallUpdateButton.Visibility = installed && snapshot.State == UpdateState.Downloaded ? Visibility.Visible : Visibility.Collapsed;
        InstallUpdateButton.IsEnabled = !busy;
        UpdateLinkError.Visibility = _updateLinkFailed ? Visibility.Visible : Visibility.Collapsed;
        if (_updateLinkFailed) UpdateLinkError.Text = FormatUi("Loc.ProjectLinkFailed", ProjectLinks.ReleasesUrl);
    }

    private string FormatUi(string key, params object?[] args)
        => string.Format(L10n.Culture, (string)FindResource(key), args);

    private static string UpdateFailureResource(string? code) => code switch
    {
        "FeedSignature" or "InstallerSignature" or "MissingKey" => "Loc.UpdateUnverified",
        "ElevatedInstall" => "Loc.UpdateElevated",
        "Cancelled" => "Loc.UpdateCancelled",
        _ => "Loc.UpdateFailed"
    };

    private void AutomaticUpdates_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        _overlay.Settings.CheckForUpdates = AutomaticUpdatesCheck.IsChecked == true;
        ApplyAndSave();
    }

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        var snapshot = _overlay.AppUpdates.Snapshot;
        if (!snapshot.IsInstalled || snapshot.IsPackaged || snapshot.State is UpdateState.Disabled or UpdateState.Downloaded or UpdateState.Checking or UpdateState.Downloading) return;
        await RunUpdateActionAsync(() => _overlay.AppUpdates.CheckAsync(manual: true));
    }

    private async void DownloadUpdate_Click(object sender, RoutedEventArgs e)
    {
        var snapshot = _overlay.AppUpdates.Snapshot;
        if (!snapshot.IsInstalled || snapshot.IsPackaged || snapshot.State != UpdateState.Available) return;
        await RunUpdateActionAsync(() => _overlay.AppUpdates.DownloadAsync());
    }

    private async void InstallUpdate_Click(object sender, RoutedEventArgs e)
    {
        var snapshot = _overlay.AppUpdates.Snapshot;
        if (!snapshot.IsInstalled || snapshot.IsPackaged || snapshot.State != UpdateState.Downloaded) return;
        await RunUpdateActionAsync(() => _overlay.BeginUpdateInstallAsync(), installing: true);
    }

    private async Task RunUpdateActionAsync(Func<Task> action, bool installing = false)
    {
        if (_updateActionPending || _isClosed) return;
        _updateActionPending = true;
        _updateActionFailed = false;
        _updateInstalling = installing;
        RefreshUpdateControls();
        try { await action(); }
        catch (Exception error)
        {
            App.LogError(error);
            _updateActionFailed = true;
        }
        finally
        {
            _updateActionPending = false;
            _updateInstalling = false;
            RefreshUpdateControls();
        }
    }

    private void OpenReleases_Click(object sender, RoutedEventArgs e)
    {
        _updateLinkFailed = !ProjectLinks.TryOpen(ProjectLinks.ReleasesUrl);
        RefreshUpdateControls();
    }

    private void OpenRepository_Click(object sender, RoutedEventArgs e) => OpenProjectLink(ProjectLinks.RepositoryUrl);

    private void OpenProjectLink(string url)
    {
        _failedProjectUrl = ProjectLinks.TryOpen(url) ? null : url;
        UpdateProjectLinkStatus();
    }

    private void UpdateProjectLinkStatus()
    {
        ProjectLinkStatus.Visibility = _failedProjectUrl is null ? Visibility.Collapsed : Visibility.Visible;
        if (_failedProjectUrl is not null)
            ProjectLinkStatus.Text = string.Format(L10n.Culture, (string)FindResource("Loc.ProjectLinkFailed"), _failedProjectUrl);
    }

    private void RefreshStartupState(bool operationFailed = false)
    {
        var syncing = _syncing;
        _syncing = true;
        try
        {
            var state = _startup.ReadState();
            StartupCheck.IsChecked = state.IsEnabled;
            StartupCheck.IsEnabled = state.CanChange;
            var key = operationFailed ? "Loc.StartupFailed" : state.UnavailableReason switch
            {
                StartupUnavailableReason.Packaged => "Loc.StartupPackaged",
                StartupUnavailableReason.UnsupportedExecutable => "Loc.StartupUnsupported",
                StartupUnavailableReason.ReadFailed => "Loc.StartupReadFailed",
                _ when state.IsEnabled && !state.UsesCurrentExecutable => "Loc.StartupOtherCopy",
                _ => "Loc.StartupHint"
            };
            StartupStatus.SetResourceReference(TextBlock.TextProperty, key);
            StartupStatus.Foreground = new SolidColorBrush(operationFailed ? Color.FromRgb(246, 162, 159) : Color.FromRgb(147, 166, 180));
        }
        finally { _syncing = syncing; }
    }

    private void RefreshHotkeys()
    {
        var rows = HotkeyBindings.All.Select(binding => new HotkeyRow(binding.LocalizedGesture,
            TryFindResource(binding.LabelKey) as string ?? binding.LabelKey)).ToArray();
        if (HotkeyList.ItemsSource is not IEnumerable<HotkeyRow> existing || !existing.SequenceEqual(rows))
            HotkeyList.ItemsSource = rows;

        var unavailable = HotkeyBindings.All.Where(binding => _overlay.FailedHotkeys.Contains(binding.Id))
            .Select(binding => binding.LocalizedGesture).ToArray();
        HotkeyFailureStatus.Visibility = unavailable.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        HotkeyFailureStatus.Text = unavailable.Length > 0
            ? L10n.Text("Unavailable shortcuts (in use or blocked by Windows): {0}",
                "Недоступные сочетания (заняты или заблокированы Windows): {0}", string.Join(", ", unavailable))
            : string.Empty;
    }

    private void Startup_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        try
        {
            _startup.SetEnabled(StartupCheck.IsChecked == true);
            RefreshStartupState();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException
            or System.Security.SecurityException or InvalidOperationException or ArgumentException)
        {
            App.LogError(error);
            RefreshStartupState(operationFailed: true);
        }
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
        settings.ShowNetwork = NetworkCheck.IsChecked == true;
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

    private void SnapGrid_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        _overlay.Settings.SnapToGrid = SnapGridCheck.IsChecked == true;
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

    private void RefreshOverlayColorPresets()
    {
        Choice[] Choices() =>
        [
            new("default", (string)FindResource("Loc.ColorPresetDefault")),
            new("white", (string)FindResource("Loc.ColorPresetWhite")),
            new("black", (string)FindResource("Loc.ColorPresetBlack")),
            new("custom", (string)FindResource("Loc.ColorPresetCustom"))
        ];
        TextColorPresetCombo.ItemsSource = Choices();
        BackgroundColorPresetCombo.ItemsSource = Choices();
        TextColorPresetCombo.SelectedValue = ColorPreset(_overlay.Settings.TextColor, true);
        BackgroundColorPresetCombo.SelectedValue = ColorPreset(_overlay.Settings.BackgroundColor, false);
    }

    private static string ColorPreset(string value, bool text)
    {
        if (value.Equals(text ? "#F2F8FC" : "#07121D", StringComparison.OrdinalIgnoreCase)) return "default";
        if (value.Equals("#FFFFFF", StringComparison.OrdinalIgnoreCase)) return "white";
        return value.Equals("#000000", StringComparison.OrdinalIgnoreCase) ? "black" : "custom";
    }

    private void OverlayColorPreset_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing) return;
        var text = ReferenceEquals(sender, TextColorPresetCombo);
        var combo = text ? TextColorPresetCombo : BackgroundColorPresetCombo;
        var value = SelectedKey(combo) switch
        {
            "default" => text ? "#F2F8FC" : "#07121D",
            "white" => "#FFFFFF",
            "black" => "#000000",
            _ => null
        };
        if (value is not null) (text ? TextColorHexBox : BackgroundColorHexBox).Text = value;
    }

    private void OverlayColorHex_Changed(object sender, TextChangedEventArgs e)
    {
        if (!_syncing) UpdateOverlayColor(ReferenceEquals(sender, TextColorHexBox), true);
    }

    private void OverlayColorHex_LostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_syncing) return;
        var text = ReferenceEquals(sender, TextColorHexBox);
        var box = text ? TextColorHexBox : BackgroundColorHexBox;
        var value = TryParseCustomColor(box.Text, out _, out var normalized)
            ? normalized : text ? _overlay.Settings.TextColor : _overlay.Settings.BackgroundColor;
        var wasSyncing = _syncing;
        _syncing = true;
        try { box.Text = value; UpdateOverlayColor(text, false); }
        finally { _syncing = wasSyncing; }
    }

    private void UpdateOverlayColor(bool text, bool save)
    {
        var box = text ? TextColorHexBox : BackgroundColorHexBox;
        var error = text ? TextColorError : BackgroundColorError;
        var swatch = text ? TextColorSwatch : BackgroundColorSwatch;
        var preset = text ? TextColorPresetCombo : BackgroundColorPresetCombo;
        if (!TryParseCustomColor(box.Text, out var color, out var normalized))
        {
            error.Visibility = Visibility.Visible;
            box.BorderBrush = new SolidColorBrush(Color.FromRgb(246, 162, 159));
            return;
        }
        error.Visibility = Visibility.Collapsed;
        box.ClearValue(BorderBrushProperty);
        swatch.Background = new SolidColorBrush(color);
        if (!save) return;
        if (text) _overlay.Settings.TextColor = normalized;
        else _overlay.Settings.BackgroundColor = normalized;
        var wasSyncing = _syncing;
        _syncing = true;
        try { preset.SelectedValue = ColorPreset(normalized, text); }
        finally { _syncing = wasSyncing; }
        ApplyAndSave();
    }

    private void ChooseTextColor_Click(object sender, RoutedEventArgs e) => ChooseOverlayColor(true);
    private void ChooseBackgroundColor_Click(object sender, RoutedEventArgs e) => ChooseOverlayColor(false);

    private void ChooseOverlayColor(bool text)
    {
        if (_syncing) return;
        var box = text ? TextColorHexBox : BackgroundColorHexBox;
        var value = text ? _overlay.Settings.TextColor : _overlay.Settings.BackgroundColor;
        if (!TryParseCustomColor(box.Text, out var current, out _) && !TryParseCustomColor(value, out current, out _))
            current = text ? Services.AccentThemeService.DefaultTextColor : Services.AccentThemeService.DefaultBackgroundColor;
        using var dialog = new Forms.ColorDialog
        {
            FullOpen = true,
            AnyColor = true,
            Color = System.Drawing.Color.FromArgb(current.R, current.G, current.B)
        };
        if (dialog.ShowDialog(new DialogOwner(new WindowInteropHelper(this).Handle)) == Forms.DialogResult.OK)
            box.Text = $"#{dialog.Color.R:X2}{dialog.Color.G:X2}{dialog.Color.B:X2}";
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

    private void RetryFps_Click(object sender, RoutedEventArgs e)
    {
        _overlay.RetryFps();
        RefreshSnapshots();
    }

    private async void RestartAdmin_Click(object sender, RoutedEventArgs e)
    {
        if (_requestingRestart) return;
        _requestingRestart = true;
        RestartAdminButton.IsEnabled = false;
        try
        {
            if (System.Windows.Application.Current is App app)
                await app.RestartWithAdministratorAccessAsync(_overlay);
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception
            or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            App.LogError(error);
            System.Windows.MessageBox.Show(this,
                (string)FindResource("Loc.RestartFailed"),
                "GlimHUD", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _requestingRestart = false;
            RestartAdminButton.IsEnabled = true;
        }
    }
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
        _isClosed = true;
        _overlay.SetNetworkSettingsVisible(false);
        _overlay.AppUpdates.Changed -= AppUpdates_Changed;
        _overlay.SnapshotsUpdated -= Overlay_SnapshotsUpdated;
        L10n.LanguageChanged -= LanguageChanged;
        _saveTimer.Stop();
        try { _overlay.SaveSettings(); } catch { /* Overlay reports persistence failures independently. */ }
        _glowSignalTimer.Stop();
    }

    private sealed record Choice(string Key, string Label)
    {
        public override string ToString() => Label;
    }

    private sealed record HotkeyRow(string Gesture, string Description);

    private sealed record GamePathEntry(string FullPath)
    {
        public string Name => Path.GetFileNameWithoutExtension(FullPath);
    }

    private sealed class DialogOwner(IntPtr handle) : Forms.IWin32Window
    {
        public IntPtr Handle { get; } = handle;
    }
}
