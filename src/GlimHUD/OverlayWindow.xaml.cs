using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GlimHUD.Models;
using GlimHUD.Localization;
using GlimHUD.Native;
using GlimHUD.Services;
using Forms = System.Windows.Forms;

namespace GlimHUD;

public partial class OverlayWindow : Window
{
    private readonly MediaSessionService _media = new();
    private readonly AudioSessionService _audio = new();
    private readonly AudioLevelMonitor _audioLevels = new();
    private readonly GlowMusicMotion _glowMotion = new();
    private readonly TelemetryService _telemetry = new();
    private readonly NetworkTelemetryService _network = new();
    private readonly PresentMonService _fps = new();
    private readonly GameActivityService _gameActivity = new();
    private readonly AccentThemeService _accentTheme = new();
    private readonly OverlayVisibilityPolicy _visibility = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly DispatcherTimer _sampleTimer;
    private readonly DispatcherTimer _telemetryTimer;
    private readonly DispatcherTimer _networkTimer;
    private readonly DispatcherTimer _fpsTimer;
    private readonly DispatcherTimer _volumeTimer;
    private readonly DispatcherTimer _glowTimer;
    private readonly DispatcherTimer _updateCheckTimer;
    private readonly bool _allowUpdateChecks;
    private CancellationTokenSource? _automaticUpdateCheck;
    private Task? _updateCheckTask;
    private bool _installingUpdate;
    private readonly PawnIoInstallerService _cpuDriverInstaller = new();
    private PawnIoInstallResult? _lastCpuDriverInstallResult;
    private long _glowTimestamp;
    private double _glowSignalPeak;
    private int _languageRevision;
    private Controls.ReactiveGlowWindow? _glowWindow;
    private string? _glowSessionId;
    private readonly bool _initializeServices;
    private readonly bool _allowWheelInterception;
    private readonly bool _allowHotkeyRegistration;
    private OverlayInterop? _native;
    private MusicWheelInterceptor? _wheel;
    private Forms.NotifyIcon? _tray;
    private Forms.ToolStripMenuItem? _lockItem;
    private SettingsWindow? _settingsWindow;
    private ContextMenu? _hudMenu;
    private OverlayMenuInterop? _hudMenuLayer;
    private bool _sampling;
    private bool _telemetrySampling;
    private bool _networkSampling;
    private int _networkSelectionRevision;
    private bool _applyingSnapshot;
    private bool _loaded;
    private bool _closing;
    private bool _volumeWriting;
    private bool _volumePending;
    private string? _volumeGestureSessionId;
    private string? _volumePendingSessionId;
    private string? _volumeValueSessionId;
    private double _volumePendingPercent;
    private long _volumeChangeVersion;
    private long _audioSnapshotVolumeVersion;
    private readonly HashSet<Task> _musicCommands = [];
    private byte[]? _lastArtwork;
    private Task? _sampleTask;
    private Task? _telemetryTask;
    private Task? _networkTask;
    private Task? _volumeTask;
    private Task? _stopTask;
    private HudTheme? _activeTheme;
    private readonly Dictionary<string, Border> _themeModuleFrames = new(StringComparer.Ordinal);

    public AppSettings Settings { get; } = SettingsStore.Load();
    public AppUpdateService AppUpdates { get; } = new();
    public MediaSnapshot? MediaSnapshot { get; private set; }
    public AudioSnapshot? AudioSnapshot { get; private set; }
    public TelemetrySnapshot? TelemetrySnapshot { get; private set; }
    public NetworkSnapshot? NetworkSnapshot { get; private set; }
    public FpsSnapshot? FpsSnapshot { get; private set; }
    public GameActivitySnapshot? GameActivitySnapshot { get; private set; }
    public string CurrentAccentHex => _accentTheme.CurrentHex;
    public string WindowsAccentHex => _accentTheme.SystemHex;
    public bool WheelHookInstalled => _wheel?.IsInstalled == true;
    public bool GlowAudioAvailable => IsFreshGlowAudio(_audioLevels.Snapshot);
    public double GlowAudioPeak
    {
        get
        {
            var snapshot = _audioLevels.Snapshot;
            return IsFreshGlowAudio(snapshot) ? Math.Max(snapshot.Peak, _glowSignalPeak) : 0;
        }
    }

    private bool IsFreshGlowAudio(AudioLevelSnapshot snapshot)
        => snapshot.IsAvailable && snapshot.SessionId == AudioSnapshot?.SessionId
            && DateTimeOffset.UtcNow - snapshot.SampledAt < TimeSpan.FromMilliseconds(250);
    public event EventHandler? SnapshotsUpdated;
    public IReadOnlyList<int> FailedHotkeys => _native?.FailedHotkeys ?? [];

    public OverlayWindow(bool initializeServices = true, bool allowWheelInterception = true,
        bool allowHotkeyRegistration = true, bool allowUpdateChecks = true)
    {
        _initializeServices = initializeServices;
        _allowWheelInterception = allowWheelInterception;
        _allowHotkeyRegistration = allowHotkeyRegistration;
        _allowUpdateChecks = allowUpdateChecks;
        LocalizationService.ApplyLanguage(Settings.Language);
        InitializeComponent();
        _sampleTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _sampleTimer.Tick += (_, _) => BeginSample();
        _telemetryTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _telemetryTimer.Tick += (_, _) => BeginTelemetrySample();
        _networkTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _networkTimer.Tick += (_, _) => BeginNetworkSample();
        _fpsTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _fpsTimer.Tick += (_, _) => { UpdateFps(); RefreshVisibility(); UpdateWheelRegion(); };
        _volumeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _volumeTimer.Tick += async (_, _) => { _volumeTimer.Stop(); await ScheduleVolumeWriteAsync(); };
        _glowTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
        _glowTimer.Tick += (_, _) => UpdateGlowFrame();
        _updateCheckTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _updateCheckTimer.Tick += (_, _) => BeginAutomaticUpdateCheck();
        _accentTheme.AccentChanged += AccentThemeChanged;
        VolumeSlider.PreviewMouseLeftButtonDown += (_, _) => _volumeGestureSessionId = AudioSnapshot?.SessionId;
        MusicSection.PreviewMouseWheel += (_, e) =>
        {
            if (_wheel?.IsInstalled != true && TryAdjustMusicVolume(e.Delta)) e.Handled = true;
        };
        LocationChanged += (_, _) => { UpdateWheelRegion(); UpdateGlowBounds(); };
        SizeChanged += (_, _) => { UpdateWheelRegion(); UpdateGlowBounds(); };
        PanelSurface.SizeChanged += (_, _) => UpdateThemeCornerRadius();
        ApplySettings();
    }

    private void Window_SourceInitialized(object? sender, EventArgs e)
    {
        if (!_initializeServices) return;
        _native = new OverlayInterop(this, OnHotkey, registerHotkeys: _allowHotkeyRegistration);
        _native.SetClickThrough(Settings.Locked);
        UpdateWheelRegion();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        var work = SystemParameters.WorkArea;
        Left = Settings.Left ?? work.Left + Math.Max(0, (work.Width - ActualWidth) / 2);
        Top = Settings.Top ?? work.Top + 24;
        EnsureVisiblePosition();
        _loaded = true;
        if (!_initializeServices) return;
        CreateTray();
        _fps.Start();
        _sampleTimer.Start();
        _telemetryTimer.Start();
        _networkTimer.Start();
        _fpsTimer.Start();
        BeginSample();
        BeginTelemetrySample();
        BeginNetworkSample();
        RefreshVisibility();
        UpdateWheelRegion();
        ConfigureAutomaticUpdates();
        if (FailedHotkeys.Count > 0)
            _tray?.ShowBalloonTip(6000, "GlimHUD", L10n.Text("Some hotkeys are unavailable. Use the tray icon to control GlimHUD.", "Некоторые горячие клавиши недоступны. Управление доступно через значок в трее."), Forms.ToolTipIcon.Info);
    }

    private void CreateTray()
    {
        _tray = new Forms.NotifyIcon();
        try
        {
            var resource = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/GlimHUD-tray.ico"));
            if (resource is not null)
            {
                using (resource.Stream)
                using (var sourceIcon = new System.Drawing.Icon(resource.Stream))
                    _tray.Icon = (System.Drawing.Icon)sourceIcon.Clone();
            }
        }
        catch (Exception error) { App.LogError(error); }
        _tray.Icon ??= (System.Drawing.Icon)System.Drawing.SystemIcons.Application.Clone();
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("", null, (_, _) => Dispatcher.Invoke(OpenSettings));
        _lockItem = new Forms.ToolStripMenuItem()
        {
            Checked = Settings.Locked,
            CheckOnClick = false
        };
        _lockItem.Click += (_, _) => Dispatcher.Invoke(ToggleLock);
        menu.Items.Add(_lockItem);
        menu.Items.Add("", null, (_, _) => Dispatcher.Invoke(ToggleVisibility));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("", null, (_, _) => Dispatcher.Invoke(ResetPosition));
        menu.Items.Add("", null, (_, _) => Dispatcher.Invoke(ExitApplication));
        menu.Opened += (_, _) => OverlayMenuInterop.BringNativeMenuToFront(menu.Handle);
        menu.Closed += (_, _) => ConfigureGlow();
        _tray.ContextMenuStrip = menu;
        UpdateTrayText();
        _tray.DoubleClick += (_, _) => Dispatcher.Invoke(OpenSettings);
        _tray.Visible = true;
    }

    private void UpdateTrayText()
    {
        if (_tray?.ContextMenuStrip is not { } menu) return;
        _tray.Text = L10n.Text("GlimHUD · music and PC stats", "GlimHUD · музыка и показатели ПК");
        menu.Items[0].Text = L10n.Text("Settings…    Ctrl+Alt+S", "Настройки…    Ctrl+Alt+S");
        menu.Items[1].Text = L10n.Text("Lock / click-through    Ctrl+Alt+G", "Закрепить / пропускать клики    Ctrl+Alt+G");
        menu.Items[2].Text = L10n.Text("Show / hide    Ctrl+Alt+H", "Показать / скрыть    Ctrl+Alt+H");
        menu.Items[4].Text = L10n.Text("Bring overlay back on screen", "Вернуть панель на экран");
        menu.Items[5].Text = L10n.Text("Exit", "Выход");
    }

    private async void OnHotkey(int id)
    {
        if (_closing) return;
        try
        {
            switch (id)
            {
                case 1: ToggleLock(); break;
                case 2: ToggleVisibility(); break;
                case 3: OpenSettings(); break;
                case 4: await RunTrackedMusicCommandAsync(() => RunMediaCommandAsync(source => _media.TogglePlaybackAsync(source, _shutdown.Token))); break;
                case 5: await RunTrackedMusicCommandAsync(() => RunMediaCommandAsync(source => _media.PreviousAsync(source, _shutdown.Token))); break;
                case 6: await RunTrackedMusicCommandAsync(() => RunMediaCommandAsync(source => _media.NextAsync(source, _shutdown.Token))); break;
                case 7: TryAdjustApplicationVolume(3); break;
                case 8: TryAdjustApplicationVolume(-3); break;
                case 9: await RunTrackedMusicCommandAsync(ToggleMusicMuteAsync); break;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { App.LogError(error); }
    }

    public void ToggleLock()
    {
        Settings.Locked = !Settings.Locked;
        if (Settings.Locked) CloseHudMenu();
        ApplySettings();
        SaveSettings();
    }

    public void ToggleVisibility()
    {
        _visibility.Toggle(IsVisible);
        RefreshVisibility();
        if (IsVisible) EnsureVisiblePosition();
    }

    public void OpenSettings()
    {
        if (_closing) return;
        if (_settingsWindow is not null)
        {
            if (_settingsWindow.WindowState == WindowState.Minimized) _settingsWindow.WindowState = WindowState.Normal;
            _settingsWindow.Show();
            _settingsWindow.Activate();
            return;
        }
        _settingsWindow = new SettingsWindow(this);
        _settingsWindow.Closed += (_, _) => { _settingsWindow = null; RefreshVisibility(); };
        _settingsWindow.Show();
        _settingsWindow.Activate();
        RefreshVisibility();
    }

    public void ApplySettings()
    {
        Settings.Language = L10n.NormalizeLanguage(Settings.Language);
        if (Settings.Language != L10n.Language) _languageRevision++;
        Settings.GlowStyle = GlowStyles.Normalize(Settings.GlowStyle);
        LocalizationService.ApplyLanguage(Settings.Language);
        if (MediaSnapshot is { IsAvailable: false })
        {
            TrackTitle.SetResourceReference(TextBlock.TextProperty, "Loc.NoPlayback");
            TrackArtist.SetResourceReference(TextBlock.TextProperty, "Loc.OpenPlayer");
        }
        if (PanelSurface is null) return;
        // Live glow has its own always-click-through HWND; the panel's drag area stays unchanged.
        ScaledRoot.Margin = new Thickness(!_initializeServices && Settings.GlowEnabled
            ? Math.Ceiling(26 * Math.Clamp(Settings.Scale, .65, 1.5)) + 2 : Settings.CompactMode ? 2 : 5);
        UpdateTrayText();
        _accentTheme.Apply(Settings.UseWindowsAccent, Settings.CustomAccentColor);
        _accentTheme.ApplyOverlayColors(Settings.TextColor, Settings.BackgroundColor, Settings.BackgroundOpacity);
        PanelScale.ScaleX = PanelScale.ScaleY = Math.Clamp(Settings.Scale, .65, 1.5);
        MusicSection.Visibility = Visible(Settings.ShowMusic);
        FpsSection.Visibility = Visible(Settings.ShowFps);
        CpuSection.Visibility = Visible(Settings.ShowCpu);
        GpuSection.Visibility = Visible(Settings.ShowGpu);
        RamSection.Visibility = Visible(Settings.ShowRam);
        NetworkSection.Visibility = Visible(Settings.ShowNetwork);
        // Always retain one visible module so the draggable overlay cannot become a zero-size window.
        if (!Settings.ShowMusic && !Settings.ShowFps && !Settings.ShowCpu && !Settings.ShowGpu && !Settings.ShowRam && !Settings.ShowNetwork)
        {
            Settings.ShowFps = true;
            FpsSection.Visibility = Visibility.Visible;
        }
        ApplyCompactLayout();
        ApplyThemeLayout();
        UpdateModuleDividers();
        _telemetry.PreferredGpuIdentifier = Settings.GpuIdentifier;
        var networkSelectionChanged = !string.Equals(_network.PreferredAdapterId, Settings.NetworkAdapterId, StringComparison.OrdinalIgnoreCase);
        if (networkSelectionChanged)
        {
            _networkSelectionRevision++;
            NetworkSnapshot = null;
        }
        _network.PreferredAdapterId = Settings.NetworkAdapterId;
        RenderNetwork();
        _native?.SetClickThrough(Settings.Locked);
        _hudMenuLayer?.BringToFront();
        if (_tray?.ContextMenuStrip is { Visible: true } trayMenu)
            OverlayMenuInterop.BringNativeMenuToFront(trayMenu.Handle);
        if (_lockItem is not null) _lockItem.Checked = Settings.Locked;
        Cursor = Settings.Locked ? Cursors.Arrow : Cursors.SizeAll;
        PanelSurface.ToolTip = Settings.Locked
            ? L10n.Text("Ctrl+Alt+G — unlock overlay controls", "Ctrl+Alt+G — включить управление панелью")
            : L10n.Text("Drag to move · Ctrl+Alt+G — lock · Right-click — settings", "Перетащите панель · Ctrl+Alt+G — закрепить · Правая кнопка — настройки");
        if (_loaded)
        {
            UpdateLayout();
            EnsureVisiblePosition();
            BeginSample();
            BeginTelemetrySample();
            if (networkSelectionChanged || NetworkSnapshot is null) BeginNetworkSample();
            RefreshVisibility();
            UpdateWheelRegion();
        }
        ConfigureGlow();
        ConfigureAutomaticUpdates();
    }

    private void ApplyCompactLayout()
    {
        var compact = Settings.CompactMode;
        FontFamily = new FontFamily("Segoe UI");
        Resources["ThemeButtonCornerRadius"] = new CornerRadius(12);
        ModuleHost.Orientation = Orientation.Horizontal;
        PanelSurface.BorderThickness = new Thickness(1);
        PanelSurface.SetResourceReference(Border.BackgroundProperty, "OverlayBackgroundBrush");
        foreach (var (_, section, divider) in ThemeModules())
        {
            section.MinHeight = 0;
            section.HorizontalAlignment = HorizontalAlignment.Stretch;
            if (divider is not null) divider.Width = 1;
        }
        foreach (var frame in _themeModuleFrames.Values)
        {
            frame.Padding = frame.Margin = frame.BorderThickness = new Thickness(0);
            frame.Background = Brushes.Transparent;
            frame.CornerRadius = new CornerRadius(0);
        }
        PanelContent.Height = compact ? 24 : 50;
        PanelContent.Margin = compact ? new Thickness(12, 2, 12, 2) : new Thickness(24, 8, 24, 8);
        PanelSurface.CornerRadius = new CornerRadius(compact ? 15 : 34);
        ArtworkFrame.Visibility = Visible(!compact);
        SetArtworkSize(48, 12);
        CpuIcon.Visibility = GpuIcon.Visibility = RamIcon.Visibility = NetworkIcon.Visibility = Visible(!compact);
        CpuIcon.FontSize = GpuIcon.FontSize = RamIcon.FontSize = 24;
        NetworkIcon.Width = NetworkIcon.Height = 22;
        VolumeSlider.Visibility = MuteButton.Visibility = VolumeControlsDivider.Visibility = Visible(!compact);

        TrackInfo.Width = compact ? 126 : 145;
        TrackInfo.Margin = compact ? new Thickness(0, 0, 5, 0) : new Thickness(12, 0, 9, 0);
        TrackTitle.FontSize = compact ? 10.5 : 14;
        TrackTitle.LineHeight = compact ? 12 : double.NaN;
        TrackTitle.LineStackingStrategy = compact ? LineStackingStrategy.BlockLineHeight : LineStackingStrategy.MaxHeight;
        TrackArtist.FontSize = compact ? 8.5 : 11;
        TrackArtist.LineHeight = compact ? 10 : double.NaN;
        TrackArtist.LineStackingStrategy = compact ? LineStackingStrategy.BlockLineHeight : LineStackingStrategy.MaxHeight;
        TrackArtist.Margin = new Thickness(0, compact ? 1 : 4, 0, 0);
        foreach (var button in new[] { PreviousButton, NextButton })
        {
            button.Width = compact ? 22 : 30;
            button.Height = compact ? 24 : 34;
            button.Padding = new Thickness(compact ? 4 : 5);
        }
        PlayButton.Width = compact ? 26 : 40;
        PlayButton.Height = compact ? 24 : 40;
        PlaybackCircle.Width = PlaybackCircle.Height = compact ? 20 : 32;
        PlaybackCircle.CornerRadius = new CornerRadius(compact ? 10 : 16);
        PlaybackGlyph.Width = compact ? 9 : 11;
        PlaybackGlyph.Height = compact ? 11 : 13;
        VolumeValue.Width = compact ? 27 : 33;
        VolumeValue.FontSize = compact ? 9 : 11;
        VolumeValue.Margin = compact ? new Thickness(2, 0, 0, 0) : new Thickness(0);

        FpsContent.Width = compact ? 37 : 57;
        CpuContent.Width = GpuContent.Width = compact ? 66 : 90;
        RamContent.Width = compact ? 106 : 134;
        foreach (var label in new[] { FpsLabel, CpuLabel, GpuLabel, RamLabel })
        {
            label.Visibility = Visibility.Visible;
            label.FontSize = compact ? 7 : 10;
            label.LineHeight = compact ? 8 : double.NaN;
            label.LineStackingStrategy = compact ? LineStackingStrategy.BlockLineHeight : LineStackingStrategy.MaxHeight;
            label.Margin = new Thickness(0, 0, 0, compact ? 0 : 2);
        }
        FpsValue.FontSize = compact ? 14 : 28;
        FpsValue.LineHeight = compact ? 16 : 29;
        FpsValue.LineStackingStrategy = compact ? LineStackingStrategy.BlockLineHeight : LineStackingStrategy.MaxHeight;
        foreach (var value in new[] { CpuValue, GpuValue, RamValue })
        {
            value.FontSize = compact ? 13 : 22;
            value.Width = compact ? 35 : 57;
            value.LineHeight = compact ? 16 : double.NaN;
            value.LineStackingStrategy = compact ? LineStackingStrategy.BlockLineHeight : LineStackingStrategy.MaxHeight;
        }
        foreach (var temperature in new[] { CpuTemperature, GpuTemperature })
        {
            temperature.FontSize = compact ? 9 : 11;
            temperature.Width = compact ? 31 : 33;
            temperature.Margin = new Thickness(0, 0, 0, compact ? 0 : 5);
        }
        RamMemory.FontSize = compact ? 8.5 : 10;
        RamMemory.Width = compact ? 71 : 77;
        RamMemory.Margin = new Thickness(0, 0, 0, compact ? 0 : 5);
        foreach (var divider in new[] { MusicDivider, FpsDivider, CpuDivider, GpuDivider, RamDivider, NetworkDivider })
        {
            divider.Height = compact ? 14 : 32;
            divider.Margin = new Thickness(compact ? 7 : 13, 0, compact ? 7 : 13, 0);
        }

        NetworkContent.Orientation = compact ? Orientation.Horizontal : Orientation.Vertical;
        NetworkContent.Width = compact ? 114 : 99;
        NetworkLabel.FontSize = compact ? 7 : 10;
        NetworkLabel.Visibility = Visibility.Visible;
        NetworkLabel.LineHeight = double.NaN;
        NetworkLabel.LineStackingStrategy = LineStackingStrategy.MaxHeight;
        NetworkLabel.Margin = compact ? new Thickness(0, 0, 5, 0) : new Thickness(0, 0, 0, 2);
        NetworkLabel.VerticalAlignment = compact ? VerticalAlignment.Center : VerticalAlignment.Top;
        if (compact) NetworkLabel.Text = L10n.Text("NET", "СЕТЬ");
        else NetworkLabel.SetResourceReference(TextBlock.TextProperty, "Loc.NetworkModule");
        foreach (var value in new[] { NetworkDownloadValue, NetworkUploadValue, NetworkDownloadDirection, NetworkUploadDirection })
        {
            value.FontSize = compact ? 9.5 : 13;
            value.LineHeight = compact ? 11 : double.NaN;
            value.LineStackingStrategy = compact ? LineStackingStrategy.BlockLineHeight : LineStackingStrategy.MaxHeight;
        }
        NetworkDownloadDirection.Width = NetworkUploadDirection.Width = compact ? 10 : 13;
    }

    private (string Id, StackPanel Section, Border Divider)[] ThemeModules() =>
    [
        ("music", MusicSection, MusicDivider), ("fps", FpsSection, FpsDivider),
        ("cpu", CpuSection, CpuDivider), ("gpu", GpuSection, GpuDivider),
        ("ram", RamSection, RamDivider), ("network", NetworkSection, NetworkDivider)
    ];

    /// <summary>Theme documents supply bounded values only; the application owns all visuals and controls.</summary>
    private void ApplyThemeLayout()
    {
        _activeTheme = Settings.Theme is null ? null : HudThemeCatalog.Resolve(Settings);
        var modules = ThemeModules();
        if (_activeTheme is null)
        {
            // Returning to an older appearance also restores the original ordering and transparent wrappers.
            if (_themeModuleFrames.Count > 0)
            {
                ModuleHost.Children.Clear();
                foreach (var (id, section, _) in modules)
                {
                    var frame = _themeModuleFrames[id];
                    frame.Visibility = section.Visibility;
                    ModuleHost.Children.Add(frame);
                }
            }
            return;
        }

        var theme = _activeTheme;
        var compact = Settings.CompactMode;
        var factor = compact ? .68 : 1;
        var cards = theme.Layout == "cards";
        var vertical = theme.Orientation == "vertical";
        var paddingX = theme.PaddingHorizontal * factor;
        var paddingY = theme.PaddingVertical * factor;
        var gap = theme.ModuleGap * factor;
        var valueSize = Math.Max(10, theme.ValueFontSize * factor);
        var labelSize = Math.Max(7, theme.LabelFontSize * factor);
        var secondarySize = Math.Max(8, theme.SecondaryFontSize * factor);
        var lineHeight = valueSize * 1.35;
        var labelHeight = theme.ShowLabels ? labelSize * 1.4 + 2 : 0;
        var contentHeight = Math.Max(theme.ContentHeight * factor, Math.Max(lineHeight + labelHeight, secondarySize * 2.7 + labelHeight));

        FontFamily = new FontFamily(theme.FontFamily);
        Resources["ThemeButtonCornerRadius"] = new CornerRadius(Math.Min(theme.ModuleCornerRadius, 14));
        PanelSurface.CornerRadius = new CornerRadius(theme.CornerRadius * factor);
        PanelSurface.BorderThickness = new Thickness(cards ? 0 : theme.BorderThickness);
        if (cards) PanelSurface.Background = Brushes.Transparent;
        PanelContent.Height = double.NaN;
        PanelContent.Margin = cards ? new Thickness(0) : new Thickness(paddingX, paddingY, paddingX, paddingY);
        ModuleHost.Orientation = vertical ? Orientation.Vertical : Orientation.Horizontal;

        var showIcons = theme.ShowIcons && !compact;
        ArtworkFrame.Visibility = Visible(theme.ShowArtwork && !compact);
        var artworkSize = Math.Clamp(contentHeight, 24, 48);
        SetArtworkSize(artworkSize, Math.Min(theme.ModuleCornerRadius, artworkSize / 2));
        CpuIcon.Visibility = GpuIcon.Visibility = RamIcon.Visibility = NetworkIcon.Visibility = Visible(showIcons);
        CpuIcon.FontSize = GpuIcon.FontSize = RamIcon.FontSize = Math.Clamp(valueSize, 14, 24);
        NetworkIcon.Width = NetworkIcon.Height = Math.Clamp(valueSize, 14, 22);
        var showSlider = theme.ShowVolumeSlider && !compact;
        VolumeSlider.Visibility = MuteButton.Visibility = VolumeControlsDivider.Visibility = Visible(showSlider);

        foreach (var label in new[] { FpsLabel, CpuLabel, GpuLabel, RamLabel, NetworkLabel })
        {
            label.Visibility = Visible(theme.ShowLabels);
            label.FontSize = labelSize;
            label.LineHeight = labelSize * 1.4;
            label.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
            label.Margin = new Thickness(0, 0, 0, theme.ShowLabels ? 2 : 0);
        }
        NetworkLabel.VerticalAlignment = VerticalAlignment.Top;
        NetworkContent.Orientation = Orientation.Vertical;
        NetworkLabel.SetResourceReference(TextBlock.TextProperty, "Loc.NetworkModule");
        foreach (var value in new[] { FpsValue, CpuValue, GpuValue, RamValue })
        {
            value.FontSize = valueSize;
            value.LineHeight = lineHeight;
            value.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        }
        FpsContent.Width = MeasureThemeText("9999", valueSize, FontWeights.SemiBold) + 2;
        var percentWidth = MeasureThemeText("100%", valueSize, FontWeights.SemiBold) + 2;
        var temperatureWidth = MeasureThemeText("100°C", secondarySize, FontWeights.Normal) + 6;
        foreach (var value in new[] { CpuValue, GpuValue, RamValue }) value.Width = percentWidth;
        foreach (var temperature in new[] { CpuTemperature, GpuTemperature })
        {
            temperature.FontSize = secondarySize;
            temperature.Width = temperatureWidth;
            temperature.Margin = new Thickness(4, 0, 0, 2);
        }
        CpuContent.Width = GpuContent.Width = percentWidth + temperatureWidth + 4;
        RamMemory.FontSize = secondarySize;
        RamMemory.Width = Math.Max(MeasureThemeText("128.0 / 128.0 GB", secondarySize, FontWeights.Normal),
            MeasureThemeText("128,0 / 128,0 ГБ", secondarySize, FontWeights.Normal)) + 4;
        RamMemory.Margin = new Thickness(4, 0, 0, 2);
        RamContent.Width = percentWidth + RamMemory.Width + 4;

        foreach (var value in new[] { NetworkDownloadValue, NetworkUploadValue, NetworkDownloadDirection, NetworkUploadDirection })
        {
            value.FontSize = secondarySize;
            value.LineHeight = secondarySize * 1.35;
            value.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        }
        NetworkDownloadDirection.Width = NetworkUploadDirection.Width = secondarySize + 3;
        NetworkContent.Width = Math.Max(MeasureThemeText("999.9 MB/s", secondarySize, FontWeights.SemiBold) + secondarySize + 9,
            MeasureThemeText(L10n.Text("Network", "Сеть"), labelSize, FontWeights.SemiBold));

        TrackTitle.FontSize = Math.Max(10, valueSize * .68);
        TrackArtist.FontSize = secondarySize;
        TrackTitle.LineHeight = TrackTitle.FontSize * 1.35;
        TrackArtist.LineHeight = secondarySize * 1.35;
        TrackTitle.LineStackingStrategy = TrackArtist.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        TrackArtist.Margin = new Thickness(0, 1, 0, 0);
        TrackInfo.Width = Math.Clamp(145 * factor, 110, 210);
        TrackInfo.Margin = new Thickness(ArtworkFrame.Visibility == Visibility.Visible ? 10 : 0, 0, 7, 0);
        VolumeValue.FontSize = secondarySize;
        VolumeValue.Width = MeasureThemeText("100%", secondarySize, FontWeights.Normal) + 2;
        VolumeValue.Margin = new Thickness(3, 0, 0, 0);
        var controlSize = Math.Clamp(contentHeight, 24, 36);
        PreviousButton.Width = NextButton.Width = Math.Max(22, controlSize - 6);
        PreviousButton.Height = NextButton.Height = controlSize;
        PlayButton.Width = controlSize + 2;
        PlayButton.Height = controlSize;
        PlaybackCircle.Width = PlaybackCircle.Height = controlSize - 4;
        PlaybackCircle.CornerRadius = new CornerRadius(Math.Min(theme.ModuleCornerRadius, (controlSize - 4) / 2));
        PlaybackGlyph.Width = Math.Max(9, controlSize * .30);
        PlaybackGlyph.Height = Math.Max(11, controlSize * .36);

        // Create the frames once; controls keep their event handlers, media state and automation identities.
        if (_themeModuleFrames.Count == 0)
        {
            ModuleHost.Children.Clear();
            foreach (var (id, section, _) in modules)
                _themeModuleFrames[id] = new Border { Tag = id, Child = section };
        }
        ModuleHost.Children.Clear();
        var ordered = theme.ModuleOrder.Select(id => modules.First(module => module.Id == id)).ToArray();
        var visible = ordered.Where(module => module.Section.Visibility == Visibility.Visible).ToArray();
        foreach (var (id, section, _) in ordered)
        {
            var frame = _themeModuleFrames[id];
            var last = visible.Length > 0 && visible[^1].Id == id;
            frame.Visibility = section.Visibility;
            section.MinHeight = contentHeight;
            section.HorizontalAlignment = HorizontalAlignment.Left;
            frame.CornerRadius = new CornerRadius(theme.ModuleCornerRadius * factor);
            frame.Padding = cards ? new Thickness(paddingX, paddingY, paddingX, paddingY) : new Thickness(0);
            var horizontalGap = !cards && theme.ShowDividers ? gap / 2 : gap;
            frame.Margin = vertical ? new Thickness(0, 0, 0, last ? 0 : gap) : new Thickness(0, 0, last ? 0 : horizontalGap, 0);
            if (cards)
            {
                frame.SetResourceReference(Border.BackgroundProperty, "OverlayBackgroundBrush");
                frame.SetResourceReference(Border.BorderBrushProperty, "AccentPanelBorderBrush");
                frame.BorderThickness = new Thickness(theme.BorderThickness);
            }
            else if (vertical && theme.ShowDividers && !last)
            {
                frame.SetResourceReference(Border.BorderBrushProperty, "AccentDividerBrush");
                frame.BorderThickness = new Thickness(0, 0, 0, 1);
                frame.Padding = new Thickness(0, 0, 0, gap);
            }
            ModuleHost.Children.Add(frame);
        }
    }

    private void SetArtworkSize(double size, double radius)
    {
        ArtworkFrame.Width = ArtworkFrame.Height = size;
        ArtworkFrame.CornerRadius = new CornerRadius(radius);
        if (ArtworkFrame.Child is not Grid grid) return;
        grid.Width = grid.Height = size - 2;
        grid.Clip = new RectangleGeometry(new Rect(0, 0, size - 2, size - 2), Math.Max(0, radius - 1), Math.Max(0, radius - 1));
    }

    private double MeasureThemeText(string text, double size, FontWeight weight)
        => Math.Ceiling(new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(FontFamily, FontStyles.Normal, weight, FontStretches.Normal), size, Brushes.White,
            VisualTreeHelper.GetDpi(this).PixelsPerDip).WidthIncludingTrailingWhitespace);

    private bool CanCheckAutomatically => _initializeServices && _allowUpdateChecks && _loaded
        && !_closing && Settings.CheckForUpdates && AppUpdates.Snapshot.IsInstalled
        && !AppUpdates.Snapshot.IsPackaged;

    private void ConfigureAutomaticUpdates()
    {
        if (CanCheckAutomatically)
        {
            if (_updateCheckTask is not { IsCompleted: false }) _updateCheckTimer.Start();
        }
        else
        {
            _updateCheckTimer.Stop();
            _automaticUpdateCheck?.Cancel();
        }
    }

    private void BeginAutomaticUpdateCheck()
    {
        _updateCheckTimer.Stop();
        if (!CanCheckAutomatically || _updateCheckTask is { IsCompleted: false }) return;
        _updateCheckTask = CheckAutomaticallyAsync();
    }

    private async Task CheckAutomaticallyAsync()
    {
        using var check = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        _automaticUpdateCheck = check;
        try { await AppUpdates.CheckAsync(cancellationToken: check.Token); }
        catch (OperationCanceledException) { }
        catch (Exception error) { App.LogError(error); }
        finally
        {
            _automaticUpdateCheck = null;
            _updateCheckTimer.Interval = TimeSpan.FromHours(6);
            if (CanCheckAutomatically) _updateCheckTimer.Start();
        }
    }

    public async Task BeginUpdateInstallAsync()
    {
        if (_closing || _installingUpdate) return;
        _installingUpdate = true;
        try
        {
            var installer = await AppUpdates.PrepareInstallerLauncherAsync(_shutdown.Token);
            if (installer is null) throw new InvalidOperationException("The update could not be prepared for installation.");
            SaveSettings();
            _settingsWindow?.Close();
            await StopAsync();
            try
            {
                if (System.Windows.Application.Current is App app) app.ReleaseSingleInstanceForUpdate();
                if (!await installer.TryLaunchAsync())
                    throw new InvalidOperationException($"Windows could not hand off the verified installer: {installer.ErrorCode}.");
            }
            catch (Exception error)
            {
                App.LogError(error);
                var desktopFailure = installer.ErrorCode is "DesktopUnavailable" or "InstallerLaunchFailed" or "ElevatedInstall";
                System.Windows.MessageBox.Show(desktopFailure ? (string)FindResource("Loc.UpdateDesktopUnavailable") : L10n.Text(
                    "The installer could not start. Please restart GlimHUD and try again.",
                    "Не удалось запустить установщик. Перезапустите GlimHUD и повторите попытку."),
                    "GlimHUD", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally { System.Windows.Application.Current.Shutdown(); }
        }
        finally { _installingUpdate = false; }
    }

    private void UpdateModuleDividers()
    {
        // A module's separator belongs only between visible neighbors, never at an edge.
        // Keep each value column at a stable width while the common outer inset stays equal.
        var moduleDefinitions = ThemeModules();
        var modules = (_activeTheme is { } theme ? theme.ModuleOrder : moduleDefinitions.Select(module => module.Id))
            .Select(id => moduleDefinitions.First(module => module.Id == id)).ToArray();
        var visibleAfter = false;
        for (var index = modules.Length - 1; index >= 0; index--)
        {
            var (_, section, divider) = modules[index];
            var visible = section.Visibility == Visibility.Visible;
            var show = visible && visibleAfter && (_activeTheme is null
                || (_activeTheme.ShowDividers && _activeTheme.Layout == "bar" && _activeTheme.Orientation == "horizontal"));
            divider.Visibility = Visible(show);
            if (_activeTheme is { } active)
            {
                divider.Height = Math.Max(12, active.ContentHeight * (Settings.CompactMode ? .68 : 1) * .65);
                divider.Margin = new Thickness(active.ModuleGap * (Settings.CompactMode ? .68 : 1) / 2, 0, 0, 0);
            }
            visibleAfter |= visible;
        }
    }

    public void SaveSettings()
    {
        try
        {
            if (_loaded) { Settings.Left = Left; Settings.Top = Top; }
            SettingsStore.Save(Settings);
        }
        catch (Exception error) { App.LogError(error); }
    }

    public async Task<PawnIoInstallResult> InstallCpuDriverAsync()
    {
        if (!_initializeServices || _closing)
            return new(PawnIoInstallStatus.Unavailable, ErrorCode: "NotRunning");
        var result = await _cpuDriverInstaller.InstallAsync(_shutdown.Token);
        _lastCpuDriverInstallResult = result;
        if (!_closing && result.Status == PawnIoInstallStatus.Installed)
            await RefreshSnapshotsAsync();
        return result;
    }

    /// <summary>On-demand support data; no window titles, media or network identifiers.</summary>
    public string GetDiagnosticReport()
    {
        static string? Limit(string? value) => value is null ? null : value[..Math.Min(value.Length, 1024)];
        var hardware = TelemetrySnapshot;
        var fps = FpsSnapshot;
        return JsonSerializer.Serialize(new
        {
            Application = "GlimHUD",
            Version = typeof(App).Assembly.GetName().Version?.ToString(3),
            Windows = Environment.OSVersion.VersionString,
            Architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
            Administrator = AppRestartService.IsAdministrator,
            DriverInstallation = _lastCpuDriverInstallResult is { } installation ? new
            {
                Status = installation.Status.ToString(), installation.Win32Code, installation.ErrorCode
            } : null,
            Cpu = hardware is null ? null : new
            {
                Name = Limit(hardware.CpuName),
                TemperatureCelsius = hardware.CpuTemperatureCelsius,
                TemperatureState = hardware.CpuTemperatureState.ToString(),
                TemperatureSource = Limit(hardware.CpuTemperatureSource),
                Driver = hardware.CpuTemperatureAccess,
                Sensors = hardware.CpuTemperatureSensors.Take(64).Select(sensor => new
                {
                    Name = Limit(sensor.Name), Identifier = Limit(sensor.Identifier), sensor.Value,
                    sensor.IsAbsoluteTemperature, sensor.IsSelected
                }).ToArray()
            },
            Fps = fps is null ? null : new
            {
                fps.ProcessId, ProcessName = Limit(fps.ProcessName), fps.IsPinned,
                fps.FramesPerSecond, fps.IsAvailable, fps.IsRunning, fps.RequiresElevation,
                fps.ObservedProcessCount, fps.ParsedFrameCount,
                fps.RejectedFrameRowCount, SelectedSwapChain = Limit(fps.SelectedSwapChain),
                fps.ObservedSwapChainCount, fps.FrameDataAgeMilliseconds
            }
        }, new JsonSerializerOptions { WriteIndented = true });
    }

    public IReadOnlyList<FpsProcessTarget> GetFpsTargets() => _fps.GetAvailableTargets();
    public void SetFpsTarget(int? processId)
    {
        _fps.SetTargetProcess(processId);
        UpdateFps();
    }

    public void RetryFps()
    {
        _fps.Start();
        UpdateFps();
        SnapshotsUpdated?.Invoke(this, EventArgs.Empty);
    }

    private void BeginSample()
    {
        if (!_initializeServices || _sampling || _closing) return;
        _sampleTask = SampleAsync();
    }

    public async Task RefreshSnapshotsAsync()
    {
        if (!_sampling) _sampleTask = SampleAsync();
        if (!_telemetrySampling) _telemetryTask = SampleTelemetryAsync();
        BeginNetworkSample();
        await Task.WhenAll(new[] { _sampleTask, _telemetryTask, _networkTask }.OfType<Task>());
    }

    public void SetNetworkSettingsVisible(bool visible)
    {
        if (visible && _loaded) BeginNetworkSample();
    }

    private void BeginNetworkSample()
    {
        if (!_initializeServices || _networkSampling || _closing) return;
        _networkTask = SampleNetworkAsync();
    }

    private async Task SampleNetworkAsync()
    {
        if (_networkSampling || _closing) return;
        _networkSampling = true;
        var revision = _networkSelectionRevision;
        try
        {
            var snapshot = await _network.SampleAsync(_shutdown.Token);
            if (_closing || revision != _networkSelectionRevision) return;
            NetworkSnapshot = snapshot;
            RenderNetwork();
            SnapshotsUpdated?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { App.LogError(error); }
        finally
        {
            _networkSampling = false;
            if (!_closing && revision != _networkSelectionRevision) BeginNetworkSample();
        }
    }

    private void RenderNetwork()
    {
        NetworkDownloadValue.Text = NetworkValueFormatter.FormatRate(NetworkSnapshot?.DownloadBytesPerSecond);
        NetworkUploadValue.Text = NetworkValueFormatter.FormatRate(NetworkSnapshot?.UploadBytesPerSecond);
        NetworkSection.ToolTip = NetworkSnapshot?.AdapterName ?? L10n.Text("Network adapter", "Сетевой адаптер");
    }

    internal void SetNetworkPreview(NetworkSnapshot snapshot)
    {
        if (_initializeServices) throw new InvalidOperationException("Preview data cannot replace live network measurements.");
        NetworkSnapshot = snapshot;
        RenderNetwork();
        SnapshotsUpdated?.Invoke(this, EventArgs.Empty);
    }

    internal void SetTelemetryPreview(TelemetrySnapshot snapshot)
    {
        if (_initializeServices) throw new InvalidOperationException("Preview data cannot replace live hardware measurements.");
        TelemetrySnapshot = snapshot;
        RenderTelemetry();
        SnapshotsUpdated?.Invoke(this, EventArgs.Empty);
    }

    internal void SetFpsPreview(FpsSnapshot snapshot)
    {
        if (_initializeServices) throw new InvalidOperationException("Preview data cannot replace live frame measurements.");
        FpsSnapshot = snapshot;
        SnapshotsUpdated?.Invoke(this, EventArgs.Empty);
    }

    private void BeginTelemetrySample()
    {
        if (!_initializeServices || _telemetrySampling || _closing) return;
        _telemetryTask = SampleTelemetryAsync();
    }

    private async Task SampleTelemetryAsync()
    {
        if (_telemetrySampling || _closing) return;
        _telemetrySampling = true;
        var languageRevision = _languageRevision;
        try
        {
            var hardware = await _telemetry.SampleAsync(_shutdown.Token);
            if (_closing || languageRevision != _languageRevision) return;
            TelemetrySnapshot = hardware;
            RenderTelemetry();
            SnapshotsUpdated?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { App.LogError(error); }
        finally
        {
            _telemetrySampling = false;
            if (!_closing && languageRevision != _languageRevision) BeginTelemetrySample();
        }
    }

    private async Task SampleAsync()
    {
        if (_sampling || _closing) return;
        _sampling = true;
        var languageRevision = _languageRevision;
        try
        {
            var token = _shutdown.Token;
            var media = await _media.GetSnapshotAsync(Settings.MediaSourceAppId, token);
            var audioVersion = _volumeChangeVersion;
            var audio = await _audio.GetSnapshotAsync(media.SourceAppId, Settings.AudioSessionId, token);
            if (_closing || languageRevision != _languageRevision) return;
            MediaSnapshot = media;
            AudioSnapshot = audio;
            _audioSnapshotVolumeVersion = audioVersion;
            ConfigureGlow();
            RenderSnapshots();
            UpdateFps();
            SnapshotsUpdated?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { App.LogError(error); }
        finally
        {
            _sampling = false;
            if (!_closing && languageRevision != _languageRevision) BeginSample();
        }
    }

    private void RenderSnapshots()
    {
        if (MediaSnapshot is { } media)
        {
            TrackTitle.Text = media.Title;
            TrackArtist.Text = media.Artist;
            TrackTitle.ToolTip = media.Title;
            TrackArtist.ToolTip = string.IsNullOrEmpty(media.Status) ? media.Artist : media.Status;
            PreviousButton.IsEnabled = media.IsAvailable && media.CanPrevious;
            PlayButton.IsEnabled = media.IsAvailable && media.CanTogglePlayback;
            NextButton.IsEnabled = media.IsAvailable && media.CanNext;
            PlaybackGlyph.Data = Geometry.Parse(media.IsPlaying ? "M 1,0 L 5,0 5,14 1,14 Z M 9,0 L 13,0 13,14 9,14 Z" : "M 2,0 L 12,7 2,14 Z");
            SetArtwork(media.ArtworkBytes);
        }
        if (AudioSnapshot is { } audio)
        {
            _applyingSnapshot = true;
            try
            {
                if (!VolumeSlider.IsMouseCaptureWithin && !_volumePending && !_volumeWriting
                    && _audioSnapshotVolumeVersion == _volumeChangeVersion)
                {
                    VolumeSlider.Value = audio.Volume * 100;
                    _volumeValueSessionId = audio.SessionId;
                }
                VolumeSlider.IsEnabled = MuteButton.IsEnabled = audio.IsAvailable;
                VolumeValue.Text = audio.IsAvailable ? $"{Math.Round(VolumeSlider.Value):0}%" : "—";
                VolumeGlyph.Text = audio.IsMuted ? "\uE74F" : "\uE767";
                var detail = audio.IsAvailable ? L10n.Text("Volume: {0}", "Громкость: {0}", audio.DisplayName) : audio.Status;
                VolumeSlider.ToolTip = detail;
                MuteButton.ToolTip = detail;
                UpdateWheelRegion();
            }
            finally { _applyingSnapshot = false; }
        }
    }

    private void RenderTelemetry()
    {
        if (TelemetrySnapshot is { } hardware)
        {
            CpuValue.Text = Percent(hardware.CpuLoadPercent);
            CpuTemperature.Text = Temperature(hardware.CpuTemperatureCelsius);
            GpuValue.Text = Percent(hardware.GpuLoadPercent);
            GpuTemperature.Text = Temperature(hardware.GpuTemperatureCelsius);
            RamValue.Text = Percent(hardware.MemoryLoadPercent);
            RamMemory.Text = hardware.MemoryUsedBytes.HasValue && hardware.MemoryTotalBytes.HasValue
                ? L10n.Text("{0:0.#}/{1:0.#} GB", "{0:0.#}/{1:0.#} ГБ", hardware.MemoryUsedBytes.Value / 1073741824d, hardware.MemoryTotalBytes.Value / 1073741824d)
                : "—";
            CpuSection.ToolTip = string.Join("\n", new[] { hardware.CpuName,
                hardware.CpuLoadUsesUtility
                    ? L10n.Text("CPU utility, adjusted for clock speed and boost", "Загрузка CPU с учётом частоты и турборежима")
                    : L10n.Text("CPU busy time (utility counter unavailable)", "Время занятости CPU (счётчик производительности недоступен)"),
                hardware.CpuTemperatureCelsius is null ? hardware.Status : null }.Where(text => !string.IsNullOrWhiteSpace(text)));
            GpuSection.ToolTip = string.Join("\n", new[] { hardware.GpuName,
                L10n.Text("Busiest GPU engine or GPU core, whichever is higher", "Максимум загрузки движков GPU и ядра GPU"),
                hardware.GpuTemperatureCelsius is null ? hardware.Status : null }.Where(text => !string.IsNullOrWhiteSpace(text)));
            RamSection.ToolTip = L10n.Text("Physical memory in use", "Занятая физическая оперативная память");
        }
    }

    internal void SetArtwork(byte[]? bytes)
    {
        if (ReferenceEquals(bytes, _lastArtwork)) return;
        // A failed decode must remain retryable when the provider returns the same array.
        _lastArtwork = null;
        Artwork.Source = null;
        Artwork.Visibility = Visibility.Collapsed;
        ArtworkPlaceholder.Visibility = Visibility.Visible;
        if (bytes is null) return;
        try
        {
            using var stream = new MemoryStream(bytes);
            var image = new BitmapImage();
            image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 128;
            image.StreamSource = stream; image.EndInit(); image.Freeze();
            _lastArtwork = bytes;
            Artwork.Source = image;
            Artwork.Visibility = Visibility.Visible;
            ArtworkPlaceholder.Visibility = Visibility.Collapsed;
        }
        catch (Exception error) when (error is NotSupportedException or IOException or ArgumentException
            or FormatException or InvalidOperationException or System.Runtime.InteropServices.COMException) { }
    }

    private void UpdateFps()
    {
        if (_closing) return;
        FpsSnapshot = _fps.GetSnapshot();
        FpsValue.Text = FpsSnapshot.FramesPerSecond is { } fps && double.IsFinite(fps)
            ? Math.Round(fps).ToString("0", CultureInfo.InvariantCulture)
            : "—";
        FpsSection.ToolTip = FpsSnapshot.Status;
    }

    private void RefreshVisibility()
    {
        if (!_initializeServices || !_loaded || _closing) return;
        GameActivitySnapshot = _gameActivity.GetSnapshot(Settings.GameExecutablePaths);
        var shouldShow = _visibility.Evaluate(Settings.AutoShowInGames,
            GameActivitySnapshot.IsGameActive, GameActivitySnapshot.IsOverlayFocused,
            _settingsWindow?.IsVisible == true);
        if (shouldShow && !IsVisible) Show();
        else if (!shouldShow && IsVisible) { CloseHudMenu(); Hide(); }
        UpdateWheelRegion();
        ConfigureGlow();
    }

    private void AccentThemeChanged(object? sender, EventArgs e)
    {
        if (_closing) return;
        MusicGlow.GlowColor = _accentTheme.CurrentColor;
        if (_glowWindow is { } glow) glow.Surface.GlowColor = _accentTheme.CurrentColor;
    }

    private bool IsMenuOpen => _hudMenuLayer?.IsOpen == true || _tray?.ContextMenuStrip?.Visible == true;

    private void UpdateGlowBounds()
    {
        if (!_closing && !IsMenuOpen && _glowWindow is { IsVisible: true } && IsVisible)
            _glowWindow.UpdateBounds(PanelSurface, PanelScale.ScaleX);
    }

    private void UpdateThemeCornerRadius()
    {
        if (_activeTheme is not { } theme || theme.Layout == "cards") return;
        var radius = theme.CornerRadius * (Settings.CompactMode ? .68 : 1);
        if (PanelSurface.ActualWidth > 0 && PanelSurface.ActualHeight > 0)
            radius = Math.Min(radius, Math.Min(PanelSurface.ActualWidth, PanelSurface.ActualHeight) / 2);
        // WPF independently squeezes oversized X/Y radii into ellipses. Bound the
        // uniform radius first so the panel border and cached glow share one shape.
        PanelSurface.CornerRadius = new CornerRadius(radius);
        MusicGlow.CornerRadius = radius;
        if (_glowWindow is { } glow) glow.Surface.CornerRadius = radius;
    }

    private void ConfigureGlow()
    {
        if (_closing) return;
        UpdateThemeCornerRadius();
        var active = Settings.GlowEnabled && Settings.GlowIntensity > 0 && _activeTheme?.Layout != "cards"
            && (!_initializeServices || IsVisible);
        MusicGlow.CornerRadius = _activeTheme is null ? double.NaN : PanelSurface.CornerRadius.TopLeft;
        MusicGlow.Layer = _initializeServices ? Controls.GlowLayer.Inner : Controls.GlowLayer.Both;
        MusicGlow.Visibility = Visible(active);
        if (_initializeServices)
        {
            if (active)
            {
                _glowWindow ??= new Controls.ReactiveGlowWindow(this);
                _glowWindow.Surface.Layer = Controls.GlowLayer.Outer;
                _glowWindow.Surface.CornerRadius = MusicGlow.CornerRadius;
                // A popup must retain its z-order while open. The halo needs no
                // repeated HWND_TOPMOST positioning behind an unchanged panel.
                if (!IsMenuOpen)
                    _glowWindow.UpdateBounds(PanelSurface, PanelScale.ScaleX);
            }
            else _glowWindow?.Hide();
        }
        MusicGlow.Intensity = Settings.GlowIntensity;
        _glowMotion.Sensitivity = Settings.GlowSensitivity;
        MusicGlow.EffectStyle = Settings.GlowStyle;
        if (_glowWindow is { } companion)
        {
            companion.Surface.Intensity = Settings.GlowIntensity;
            companion.Surface.EffectStyle = Settings.GlowStyle;
        }
        if (!active)
        {
            _audioLevels.Configure(false, null);
            _glowTimer.Stop();
            _glowMotion.ResetMusic();
            _glowSignalPeak = 0;
            _glowSessionId = null;
            SetGlowVisuals(0);
            return;
        }
        var source = Settings.GlowReactive && AudioSnapshot is { IsAvailable: true } ? AudioSnapshot.SessionId : null;
        if (!Settings.GlowReactive || _glowSessionId != source)
        {
            _glowMotion.ResetMusic();
            _glowSignalPeak = 0;
            _glowSessionId = source;
        }
        _audioLevels.Configure(_initializeServices && source is not null, source);
        SetGlowVisuals(_glowMotion.Level);
        if (!_initializeServices)
        {
            _glowTimer.Stop();
        }
        else if (!_glowTimer.IsEnabled)
        {
            _glowTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
            _glowTimer.Start();
        }
    }

    private void UpdateGlowFrame()
    {
        if (_closing || !Settings.GlowEnabled || Settings.GlowIntensity <= 0 || _activeTheme?.Layout == "cards" || !IsVisible)
        {
            ConfigureGlow();
            return;
        }
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(_glowTimestamp, now);
        _glowTimestamp = now;
        var meter = _audioLevels.ReadFrame();
        var available = Settings.GlowReactive && IsFreshGlowAudio(meter);
        _glowSignalPeak = available
            ? Math.Max(meter.Peak, _glowSignalPeak * Math.Exp(-elapsed.TotalSeconds / .22)) : 0;
        _glowMotion.Update(meter.Peak, available, elapsed);
        SetGlowVisuals(_glowMotion.Level);
    }

    private void SetGlowVisuals(double level)
    {
        MusicGlow.Level = level;
        MusicGlow.Phase = _glowMotion.Phase;
        MusicGlow.Pulse = level > 0 ? _glowMotion.Pulse : 0;
        MusicGlow.PulsePosition = _glowMotion.PulsePosition;
        if (_glowWindow is not { } glow) return;
        glow.Surface.Level = level;
        glow.Surface.Phase = MusicGlow.Phase;
        glow.Surface.Pulse = MusicGlow.Pulse;
        glow.Surface.PulsePosition = MusicGlow.PulsePosition;
    }

    private void UpdateWheelRegion()
    {
        if (!_initializeServices || !_allowWheelInterception || _native is null || _closing) return;
        if (!Settings.ScrollMusicVolume || !Settings.ShowMusic)
        {
            _wheel?.Dispose();
            _wheel = null;
            return;
        }
        _wheel ??= new MusicWheelInterceptor(TryAdjustMusicVolume);
        _wheel.Active = false;
        if (!IsVisible || !MusicSection.IsVisible || AudioSnapshot is not { IsAvailable: true }
            || MusicSection.ActualWidth <= 0 || PanelSurface.ActualHeight <= 0) return;
        try
        {
            var region = _activeTheme is not null && _themeModuleFrames.TryGetValue("music", out var musicFrame)
                ? (FrameworkElement)musicFrame : MusicSection;
            var left = region.PointToScreen(new Point(0, 0));
            var right = region.PointToScreen(new Point(region.ActualWidth, 0));
            // A vertical theme must not capture the wheel over unrelated modules below the player.
            var verticalBounds = _activeTheme is not null ? region : PanelSurface;
            var top = verticalBounds.PointToScreen(new Point(0, 0));
            var bottom = verticalBounds.PointToScreen(new Point(0, verticalBounds.ActualHeight));
            _wheel.Region = new Rect(new Point(left.X, top.Y), new Point(right.X, bottom.Y));
            _wheel.Active = true;
        }
        catch (InvalidOperationException) { }
    }

    private bool TryAdjustMusicVolume(int delta)
    {
        if (!_allowWheelInterception || _closing || !IsVisible || !Settings.ShowMusic || !Settings.ScrollMusicVolume
            || delta == 0) return false;
        return TryAdjustApplicationVolume(delta / 120d * 3d);
    }

    private bool TryAdjustApplicationVolume(double deltaPercent)
    {
        if (_closing || AudioSnapshot is not { IsAvailable: true, SessionId: { Length: > 0 } } audio) return false;
        var next = MusicVolumeStep.Next(audio.SessionId, _volumeValueSessionId,
            VolumeSlider.Value, audio.Volume * 100, deltaPercent);
        if (!next.HasValue) return false;
        var wasApplying = _applyingSnapshot;
        _applyingSnapshot = true;
        try
        {
            VolumeSlider.Value = next.Value;
        }
        finally { _applyingSnapshot = wasApplying; }
        QueueVolumeWrite(next.Value, audio.SessionId);
        return true;
    }

    private async void Previous_Click(object sender, RoutedEventArgs e) => await RunTrackedMusicCommandAsync(() => RunMediaCommandAsync(source => _media.PreviousAsync(source, _shutdown.Token)));
    private async void Play_Click(object sender, RoutedEventArgs e) => await RunTrackedMusicCommandAsync(() => RunMediaCommandAsync(source => _media.TogglePlaybackAsync(source, _shutdown.Token)));
    private async void Next_Click(object sender, RoutedEventArgs e) => await RunTrackedMusicCommandAsync(() => RunMediaCommandAsync(source => _media.NextAsync(source, _shutdown.Token)));

    private async Task RunTrackedMusicCommandAsync(Func<Task> command)
    {
        if (_closing) return;
        var task = command();
        _musicCommands.Add(task);
        try { await task; }
        finally { _musicCommands.Remove(task); }
    }

    private async Task RunMediaCommandAsync(Func<string, Task<bool>> command)
    {
        var displayedSource = MediaSnapshot?.SourceAppId;
        if (_closing || MediaSnapshot is not { IsAvailable: true } || string.IsNullOrWhiteSpace(displayedSource)) return;
        try
        {
            if (!await command(displayedSource)) TrackArtist.ToolTip = L10n.Text("The player does not support this command", "Плеер не поддерживает эту команду");
            BeginSample();
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { App.LogError(error); }
    }

    private async void Mute_Click(object sender, RoutedEventArgs e) => await RunTrackedMusicCommandAsync(ToggleMusicMuteAsync);

    private async Task ToggleMusicMuteAsync()
    {
        if (_closing || AudioSnapshot is not { IsAvailable: true, SessionId: { Length: > 0 } } audio) return;
        try
        {
            await _audio.ToggleMutedAsync(null, audio.SessionId, _shutdown.Token);
            BeginSample();
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { App.LogError(error); }
    }

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_loaded || _applyingSnapshot || AudioSnapshot is not { IsAvailable: true }) return;
        var sessionId = _volumeGestureSessionId ?? AudioSnapshot.SessionId;
        if (string.IsNullOrWhiteSpace(sessionId)) return;
        QueueVolumeWrite(e.NewValue, sessionId);
    }

    private void QueueVolumeWrite(double percent, string sessionId)
    {
        _volumeChangeVersion++;
        _volumePendingSessionId = sessionId;
        _volumeValueSessionId = sessionId;
        _volumePendingPercent = percent;
        VolumeValue.Text = $"{Math.Round(percent):0}%";
        _volumePending = true;
        // Throttle rather than debounce: held hotkeys must affect audio before release.
        if (!_volumeTimer.IsEnabled) _volumeTimer.Start();
    }

    private async void VolumeSlider_MouseUp(object sender, MouseButtonEventArgs e)
    {
        _volumeTimer.Stop();
        await ScheduleVolumeWriteAsync();
        _volumeGestureSessionId = null;
    }

    private Task ScheduleVolumeWriteAsync()
    {
        if (_volumeWriting) return _volumeTask ?? Task.CompletedTask;
        _volumeTask = WriteVolumeAsync();
        return _volumeTask;
    }

    private async Task WriteVolumeAsync()
    {
        if (!_volumePending || _volumeWriting || _closing) return;
        _volumeWriting = true;
        try
        {
            while (_volumePending && !_closing)
            {
                _volumePending = false;
                var value = (float)(_volumePendingPercent / 100);
                var targetSession = _volumePendingSessionId;
                var success = !string.IsNullOrWhiteSpace(targetSession)
                    && await _audio.SetVolumeAsync(value, null, targetSession, _shutdown.Token);
                if (!success) VolumeSlider.ToolTip = L10n.Text("Audio session unavailable. Choose an app in settings.", "Аудиосессия недоступна. Выберите приложение в настройках.");
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { App.LogError(error); }
        finally { _volumeWriting = false; }
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (Settings.Locked || IsInteractiveChild(e.OriginalSource as DependencyObject)) return;
        try
        {
            using var alignment = OverlayAlignmentGuide.BeginDrag(this, Settings.SnapToGrid, PanelSurface);
            DragMove();
            alignment?.CompleteDrag();
            SaveSettings();
        }
        catch (InvalidOperationException) { }
    }

    private static bool IsInteractiveChild(DependencyObject? item)
    {
        while (item is not null)
        {
            if (item is ButtonBase or Slider or Thumb) return true;
            item = item is Visual ? VisualTreeHelper.GetParent(item) : LogicalTreeHelper.GetParent(item);
        }
        return false;
    }

    private void Window_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (Settings.Locked) return;
        var menu = new ContextMenu();
        var settingsItem = new MenuItem { Header = L10n.Text("Settings…    Ctrl+Alt+S", "Настройки…    Ctrl+Alt+S") };
        settingsItem.Click += (_, _) => OpenSettings();
        var lockItem = new MenuItem { Header = L10n.Text("Lock overlay    Ctrl+Alt+G", "Закрепить панель    Ctrl+Alt+G") };
        lockItem.Click += (_, _) => ToggleLock();
        var hideItem = new MenuItem { Header = L10n.Text("Hide    Ctrl+Alt+H", "Скрыть    Ctrl+Alt+H") };
        hideItem.Click += (_, _) => { _visibility.Hide(); RefreshVisibility(); };
        menu.Items.Add(settingsItem); menu.Items.Add(lockItem); menu.Items.Add(hideItem);
        menu.PlacementTarget = PanelSurface;
        CloseHudMenu();
        _hudMenu = menu;
        _hudMenuLayer = new OverlayMenuInterop(menu);
        menu.Closed += (_, _) =>
        {
            if (!ReferenceEquals(_hudMenu, menu)) return;
            _hudMenuLayer?.Dispose();
            _hudMenuLayer = null;
            _hudMenu = null;
            ConfigureGlow();
        };
        menu.IsOpen = true;
        e.Handled = true;
    }

    private void CloseHudMenu()
    {
        var menu = _hudMenu;
        _hudMenu = null;
        _hudMenuLayer?.Dispose();
        _hudMenuLayer = null;
        if (menu is not null) menu.IsOpen = false;
    }

    private void ResetPosition()
    {
        var work = SystemParameters.WorkArea;
        Left = work.Left + Math.Max(0, (work.Width - ActualWidth) / 2);
        Top = work.Top + 24;
        _visibility.Show();
        RefreshVisibility(); SaveSettings();
    }

    private void EnsureVisiblePosition()
    {
        var visible = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        var panel = new Rect(Left, Top, Math.Max(ActualWidth, 1), Math.Max(ActualHeight, 1));
        var intersection = Rect.Intersect(visible, panel);
        if (intersection.IsEmpty || intersection.Width < Math.Min(120, panel.Width) || intersection.Height < Math.Min(40, panel.Height))
        {
            var work = SystemParameters.WorkArea;
            Left = work.Left + Math.Max(0, (work.Width - ActualWidth) / 2);
            Top = work.Top + 24;
        }
    }

    private async void ExitApplication()
    {
        SaveSettings();
        _settingsWindow?.Close();
        await StopAsync();
        Close();
        System.Windows.Application.Current.Shutdown();
    }

    private async void Window_Closed(object? sender, EventArgs e)
    {
        await StopAsync();
    }

    /// <summary>Stop capture and await in-flight work before the dispatcher shuts down.</summary>
    public Task StopAsync() => _stopTask ??= StopCoreAsync();

    private async Task StopCoreAsync()
    {
        _closing = true;
        CloseHudMenu();
        _sampleTimer.Stop(); _telemetryTimer.Stop(); _fpsTimer.Stop(); _volumeTimer.Stop(); _glowTimer.Stop();
        _networkTimer.Stop();
        _volumePending = false;
        _updateCheckTimer.Stop();
        _shutdown.Cancel();
        var updatesStopped = AppUpdates.StopAsync();
        _wheel?.Dispose();
        _wheel = null;
        _accentTheme.AccentChanged -= AccentThemeChanged;
        _accentTheme.Dispose();
        _glowWindow?.Close();
        _glowWindow = null;
        _audioLevels.Dispose();
        _gameActivity.Dispose();
        _native?.Dispose();
        _native = null;
        if (_tray is not null) { _tray.Visible = false; _tray.Icon?.Dispose(); _tray.Dispose(); }
        _tray = null;
        // Kill our PresentMon child before the first await, even if an external caller
        // closes this window immediately before the dispatcher begins shutdown.
        _fps.Dispose();
        try
        {
            var pending = new[] { _sampleTask, _telemetryTask, _networkTask, _volumeTask, _updateCheckTask, updatesStopped }.OfType<Task>().Concat(_musicCommands.ToArray());
            await Task.WhenAll(pending);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { App.LogError(error); }
        _media.Dispose(); _audio.Dispose(); _telemetry.Dispose();
        _network.Dispose();
        _shutdown.Dispose();
    }

    private static string Percent(double? value) => value is { } number && double.IsFinite(number) ? $"{Math.Round(number):0}%" : "—";
    private static string Temperature(double? value) => value is { } number && double.IsFinite(number) ? $"{Math.Round(number):0}°C" : "—";
    private static Visibility Visible(bool value) => value ? Visibility.Visible : Visibility.Collapsed;
}
