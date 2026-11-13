using System.ComponentModel;
using System.Globalization;
using System.IO;
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
    private readonly TelemetryService _telemetry = new();
    private readonly PresentMonService _fps = new();
    private readonly GameActivityService _gameActivity = new();
    private readonly AccentThemeService _accentTheme = new();
    private readonly OverlayVisibilityPolicy _visibility = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly DispatcherTimer _sampleTimer;
    private readonly DispatcherTimer _fpsTimer;
    private readonly DispatcherTimer _volumeTimer;
    private readonly bool _initializeServices;
    private readonly bool _allowWheelInterception;
    private OverlayInterop? _native;
    private MusicWheelInterceptor? _wheel;
    private Forms.NotifyIcon? _tray;
    private Forms.ToolStripMenuItem? _lockItem;
    private SettingsWindow? _settingsWindow;
    private bool _sampling;
    private bool _applyingSnapshot;
    private bool _loaded;
    private bool _closing;
    private bool _volumeWriting;
    private bool _volumePending;
    private string? _volumeGestureSessionId;
    private string? _volumePendingSessionId;
    private byte[]? _lastArtwork;
    private Task? _sampleTask;
    private Task? _volumeTask;
    private Task? _stopTask;

    public AppSettings Settings { get; } = SettingsStore.Load();
    public MediaSnapshot? MediaSnapshot { get; private set; }
    public AudioSnapshot? AudioSnapshot { get; private set; }
    public TelemetrySnapshot? TelemetrySnapshot { get; private set; }
    public FpsSnapshot? FpsSnapshot { get; private set; }
    public GameActivitySnapshot? GameActivitySnapshot { get; private set; }
    public string CurrentAccentHex => _accentTheme.CurrentHex;
    public string WindowsAccentHex => _accentTheme.SystemHex;
    public bool WheelHookInstalled => _wheel?.IsInstalled == true;
    public event EventHandler? SnapshotsUpdated;
    public IReadOnlyList<int> FailedHotkeys => _native?.FailedHotkeys ?? [];

    public OverlayWindow(bool initializeServices = true, bool allowWheelInterception = true)
    {
        _initializeServices = initializeServices;
        _allowWheelInterception = allowWheelInterception;
        LocalizationService.ApplyLanguage(Settings.Language);
        InitializeComponent();
        _sampleTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _sampleTimer.Tick += (_, _) => BeginSample();
        _fpsTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _fpsTimer.Tick += (_, _) => { UpdateFps(); RefreshVisibility(); UpdateWheelRegion(); };
        _volumeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _volumeTimer.Tick += async (_, _) => { _volumeTimer.Stop(); await ScheduleVolumeWriteAsync(); };
        VolumeSlider.PreviewMouseLeftButtonDown += (_, _) => _volumeGestureSessionId = AudioSnapshot?.SessionId;
        MusicSection.PreviewMouseWheel += (_, e) =>
        {
            if (_wheel?.IsInstalled != true && TryAdjustMusicVolume(e.Delta)) e.Handled = true;
        };
        LocationChanged += (_, _) => UpdateWheelRegion();
        SizeChanged += (_, _) => UpdateWheelRegion();
        ApplySettings();
    }

    private void Window_SourceInitialized(object? sender, EventArgs e)
    {
        if (!_initializeServices) return;
        _native = new OverlayInterop(this, OnHotkey);
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
        _fpsTimer.Start();
        BeginSample();
        RefreshVisibility();
        UpdateWheelRegion();
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

    private void OnHotkey(int id)
    {
        if (id == 1) ToggleLock();
        else if (id == 2) ToggleVisibility();
        else if (id == 3) OpenSettings();
    }

    public void ToggleLock()
    {
        Settings.Locked = !Settings.Locked;
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
        LocalizationService.ApplyLanguage(Settings.Language);
        if (PanelSurface is null) return;
        UpdateTrayText();
        _accentTheme.Apply(Settings.UseWindowsAccent, Settings.CustomAccentColor);
        var alpha = (byte)Math.Round(Math.Clamp(Settings.BackgroundOpacity, .05, .85) * 255);
        PanelSurface.Background = new SolidColorBrush(Color.FromArgb(alpha, 7, 18, 29));
        PanelScale.ScaleX = PanelScale.ScaleY = Math.Clamp(Settings.Scale, .65, 1.5);
        MusicSection.Visibility = Visible(Settings.ShowMusic);
        FpsSection.Visibility = Visible(Settings.ShowFps);
        CpuSection.Visibility = Visible(Settings.ShowCpu);
        GpuSection.Visibility = Visible(Settings.ShowGpu);
        RamSection.Visibility = Visible(Settings.ShowRam);
        // Always retain one visible module so the draggable overlay cannot become a zero-size window.
        if (!Settings.ShowMusic && !Settings.ShowFps && !Settings.ShowCpu && !Settings.ShowGpu && !Settings.ShowRam)
        {
            Settings.ShowFps = true;
            FpsSection.Visibility = Visibility.Visible;
        }
        _telemetry.PreferredGpuIdentifier = Settings.GpuIdentifier;
        _native?.SetClickThrough(Settings.Locked);
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
            RefreshVisibility();
            UpdateWheelRegion();
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

    public IReadOnlyList<FpsProcessTarget> GetFpsTargets() => _fps.GetAvailableTargets();
    public void SetFpsTarget(int? processId)
    {
        _fps.SetTargetProcess(processId);
        UpdateFps();
    }

    private void BeginSample()
    {
        if (!_initializeServices || _sampling || _closing) return;
        _sampleTask = SampleAsync();
    }

    public async Task RefreshSnapshotsAsync()
    {
        if (_sampling && _sampleTask is not null) await _sampleTask;
        else
        {
            _sampleTask = SampleAsync();
            await _sampleTask;
        }
    }

    private async Task SampleAsync()
    {
        if (_sampling || _closing) return;
        _sampling = true;
        try
        {
            var token = _shutdown.Token;
            var mediaTask = _media.GetSnapshotAsync(Settings.MediaSourceAppId, token);
            var hardwareTask = _telemetry.SampleAsync(token);
            await Task.WhenAll(mediaTask, hardwareTask);
            var media = await mediaTask;
            var hardware = await hardwareTask;
            var audio = await _audio.GetSnapshotAsync(media.SourceAppId, Settings.AudioSessionId, token);
            if (_closing) return;
            MediaSnapshot = media;
            TelemetrySnapshot = hardware;
            AudioSnapshot = audio;
            RenderSnapshots();
            UpdateFps();
            SnapshotsUpdated?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { App.LogError(error); }
        finally { _sampling = false; }
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
                if (!VolumeSlider.IsMouseCaptureWithin && !_volumePending && !_volumeWriting)
                    VolumeSlider.Value = audio.Volume * 100;
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
            CpuSection.ToolTip = hardware.CpuName ?? hardware.Status;
            GpuSection.ToolTip = hardware.GpuName ?? hardware.Status;
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
        else if (!shouldShow && IsVisible) Hide();
        UpdateWheelRegion();
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
            var left = MusicSection.PointToScreen(new Point(0, 0));
            var right = MusicSection.PointToScreen(new Point(MusicSection.ActualWidth, 0));
            var top = PanelSurface.PointToScreen(new Point(0, 0));
            var bottom = PanelSurface.PointToScreen(new Point(0, PanelSurface.ActualHeight));
            _wheel.Region = new Rect(new Point(left.X, top.Y), new Point(right.X, bottom.Y));
            _wheel.Active = true;
        }
        catch (InvalidOperationException) { }
    }

    private bool TryAdjustMusicVolume(int delta)
    {
        if (!_allowWheelInterception || _closing || !IsVisible || !Settings.ShowMusic || !Settings.ScrollMusicVolume
            || AudioSnapshot is not { IsAvailable: true, SessionId: { Length: > 0 } } audio || delta == 0) return false;
        var session = _volumeGestureSessionId;
        _volumeGestureSessionId = audio.SessionId;
        try
        {
            VolumeSlider.Value = Math.Clamp(VolumeSlider.Value + delta / 120d * 3d, 0, 100);
        }
        finally { _volumeGestureSessionId = session; }
        return true;
    }

    private async void Previous_Click(object sender, RoutedEventArgs e) => await RunMediaCommandAsync(source => _media.PreviousAsync(source, _shutdown.Token));
    private async void Play_Click(object sender, RoutedEventArgs e) => await RunMediaCommandAsync(source => _media.TogglePlaybackAsync(source, _shutdown.Token));
    private async void Next_Click(object sender, RoutedEventArgs e) => await RunMediaCommandAsync(source => _media.NextAsync(source, _shutdown.Token));

    private async Task RunMediaCommandAsync(Func<string, Task<bool>> command)
    {
        var displayedSource = MediaSnapshot?.SourceAppId;
        if (MediaSnapshot is not { IsAvailable: true } || string.IsNullOrWhiteSpace(displayedSource)) return;
        try
        {
            if (!await command(displayedSource)) TrackArtist.ToolTip = L10n.Text("The player does not support this command", "Плеер не поддерживает эту команду");
            BeginSample();
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { App.LogError(error); }
    }

    private async void Mute_Click(object sender, RoutedEventArgs e)
    {
        if (AudioSnapshot is not { IsAvailable: true } audio) return;
        try
        {
            await _audio.SetMutedAsync(!audio.IsMuted, null, audio.SessionId, _shutdown.Token);
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
        _volumePendingSessionId = sessionId;
        VolumeValue.Text = $"{Math.Round(e.NewValue):0}%";
        _volumePending = true;
        _volumeTimer.Stop(); _volumeTimer.Start();
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
                var value = (float)(VolumeSlider.Value / 100);
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
        try { DragMove(); SaveSettings(); }
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
        menu.IsOpen = true;
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
        _sampleTimer.Stop(); _fpsTimer.Stop(); _volumeTimer.Stop();
        _volumePending = false;
        _shutdown.Cancel();
        _wheel?.Dispose();
        _wheel = null;
        _accentTheme.Dispose();
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
            var pending = new[] { _sampleTask, _volumeTask }.Where(task => task is not null).Cast<Task>();
            await Task.WhenAll(pending);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { App.LogError(error); }
        _media.Dispose(); _audio.Dispose(); _telemetry.Dispose();
        _shutdown.Dispose();
    }

    private static string Percent(double? value) => value is { } number && double.IsFinite(number) ? $"{Math.Round(number):0}%" : "—";
    private static string Temperature(double? value) => value is { } number && double.IsFinite(number) ? $"{Math.Round(number):0}°C" : "—";
    private static Visibility Visible(bool value) => value ? Visibility.Visible : Visibility.Collapsed;
}
