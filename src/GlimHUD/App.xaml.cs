using System.IO;
using System.Threading;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GlimHUD.Localization;

namespace GlimHUD;

public partial class App : System.Windows.Application
{
    private Mutex? _instance;
    internal void ReleaseSingleInstanceForUpdate()
    {
        _instance?.ReleaseMutex();
        _instance?.Dispose();
        _instance = null;
    }
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Length >= 2 && e.Args[0] is "--render-preview" or "--smoke-test" or "--theme-smoke")
        {
            var outputPath = Path.GetFullPath(e.Args[1]);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                Environment.SetEnvironmentVariable("GLIMHUD_DATA_DIR", Path.Combine(Path.GetDirectoryName(outputPath)!,
                    "diagnostic-settings-" + Guid.NewGuid().ToString("N")));
                if (e.Args[0] == "--render-preview") RenderPreview(outputPath, e.Args.ElementAtOrDefault(2), e.Args.ElementAtOrDefault(3));
                else if (e.Args[0] == "--theme-smoke") RunThemeSmokeTest(outputPath);
                else await RunSmokeTestAsync(outputPath);
                Shutdown(0);
            }
            catch (Exception error)
            {
                try { File.WriteAllText(outputPath + ".error.txt", error.ToString()); } catch { }
                Shutdown(1);
            }
            return;
        }
        try
        {
            if (!await Services.AppRestartService.WaitForRestartParentAsync(e.Args))
            {
                Shutdown(1);
                return;
            }
        }
        catch (ArgumentException)
        {
            Shutdown(1);
            return;
        }
        bool created;
        try { _instance = new Mutex(true, "Local\\GlimHUD.Desktop", out created); }
        catch (UnauthorizedAccessException)
        {
            // An existing instance may restrict its mutex. Never bypass that
            // boundary by launching a duplicate or another consent request.
            Shutdown();
            return;
        }
        if (!created) { Shutdown(); return; }
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => LogError(args.ExceptionObject as Exception);
        // Diagnostics above never enter this path. An accepted child waits for
        // this process to exit before claiming the same instance mutex.
        var startupSettings = Services.SettingsStore.Load();
        if (Services.AppRestartService.RequestStartupElevation(startupSettings.RequestAdministratorOnStartup,
                e.Args, LogError) == Services.StartupElevationOutcome.RestartStarted)
        {
            Shutdown(0);
            return;
        }
        // A declined or unavailable consent request continues normally; sensor/FPS
        // availability and the existing manual recovery action explain access.
        var overlay = new OverlayWindow();
        MainWindow = overlay;
        overlay.Show();
        if (e.Args.Contains("--settings", StringComparer.OrdinalIgnoreCase))
            overlay.OpenSettings();
    }

    private static void RenderPreview(string outputPath, string? variant = null, string? language = null)
    {
        var overlay = new OverlayWindow(false);
        // A render fixture must not inherit a theme left by another diagnostic run.
        overlay.Settings.Theme = null;
        overlay.Settings.Language = L10n.NormalizeLanguage(language);
        overlay.Settings.BackgroundOpacity = .30;
        overlay.Settings.Scale = 1;
        overlay.Settings.TextColor = "#F2F8FC";
        overlay.Settings.BackgroundColor = "#07121D";
        overlay.Settings.CompactMode = variant is "slim" or "slim-neon" or "slim-network" or "slim-stress" or "slim-no-music";
        var themePreview = variant?.StartsWith("theme-", StringComparison.Ordinal) == true;
        var networkPreview = themePreview || variant is "network" or "network-neon" or "network-only" or "network-idle" or "slim-network" or "slim-stress";
        var neonPreview = variant is "neon" or "neon-low" or "neon-pulse" or "compact-neon" or "neon-light" or "network-neon" or "slim-neon";
        overlay.Settings.GlowEnabled = neonPreview || variant is "glow" or "glow-low" or "glow-peak" or "compact-glow" or "glow-light";
        overlay.Settings.GlowStyle = neonPreview ? Models.GlowStyles.NeonFlow : Models.GlowStyles.RainbowWave;
        overlay.Settings.GlowReactive = true;
        overlay.Settings.GlowIntensity = .75;
        overlay.Settings.ShowMusic = variant is not ("compact" or "compact-light" or "compact-glow" or "compact-neon" or "slim-no-music");
        overlay.Settings.ShowFps = overlay.Settings.ShowCpu = overlay.Settings.ShowGpu = overlay.Settings.ShowRam = true;
        overlay.Settings.ShowNetwork = networkPreview;
        if (variant is "fps-only" or "cpu-only" or "ram-only" or "music-only" or "network-only")
        {
            overlay.Settings.ShowMusic = variant == "music-only";
            overlay.Settings.ShowFps = variant == "fps-only";
            overlay.Settings.ShowCpu = variant == "cpu-only";
            overlay.Settings.ShowGpu = false;
            overlay.Settings.ShowRam = variant == "ram-only";
        }
        if (variant is "light" or "compact-light" or "glow-light" or "neon-light")
        {
            overlay.Settings.TextColor = "#111820";
            overlay.Settings.BackgroundColor = "#FFFFFF";
            overlay.Settings.BackgroundOpacity = .75;
        }
        overlay.Settings.UseWindowsAccent = !Services.AccentThemeService.TryParseHex(variant, out _);
        if (!overlay.Settings.UseWindowsAccent) overlay.Settings.CustomAccentColor = variant!;
        if (themePreview)
        {
            var themeId = variant![6..];
            var stress = themeId.EndsWith("-stress", StringComparison.Ordinal);
            if (stress) themeId = themeId[..^7];
            Services.HudThemeCatalog.Apply(overlay.Settings, Services.HudThemeCatalog.GetPreset(themeId));
        }
        overlay.ApplySettings();
        // Explicit design preview only; live mode always uses service measurements.
        if (overlay.Settings.GlowEnabled)
        {
            overlay.MusicGlow.Level = variant is "glow-low" or "neon-low" ? .72 : 1;
            overlay.MusicGlow.Phase = .12;
            overlay.MusicGlow.Pulse = variant is "neon-pulse" or "glow-peak" ? 1 : 0;
            overlay.MusicGlow.PulsePosition = .3;
        }
        overlay.TrackTitle.Text = "Night Drive";
        overlay.TrackArtist.Text = "Echosphere";
        overlay.FpsValue.Text = "144";
        overlay.CpuValue.Text = "32%";
        overlay.CpuTemperature.Text = "52°C";
        overlay.GpuValue.Text = "78%";
        overlay.GpuTemperature.Text = "68°C";
        overlay.RamValue.Text = "54%";
        overlay.RamMemory.Text = L10n.Text("{0:0.#}/{1:0.#} GB", "{0:0.#}/{1:0.#} ГБ", 8.6, 16);
        overlay.VolumeSlider.Value = 60;
        overlay.VolumeValue.Text = "60%";
        if (networkPreview) overlay.SetNetworkPreview(CreateNetworkPreview(variant == "network-idle"));
        if (variant is "cpu-driver-missing" or "cpu-access-denied")
            overlay.SetTelemetryPreview(CreateCpuTemperaturePreview(variant == "cpu-access-denied"));
        if (variant == "slim-stress" || themePreview && variant!.EndsWith("-stress", StringComparison.Ordinal))
        {
            overlay.TrackTitle.Text = "A very long track title that must remain inside its column";
            overlay.TrackArtist.Text = "An artist with a similarly long name";
            overlay.FpsValue.Text = "999";
            overlay.CpuValue.Text = overlay.GpuValue.Text = overlay.RamValue.Text = "100%";
            overlay.CpuTemperature.Text = overlay.GpuTemperature.Text = "100°C";
            overlay.RamMemory.Text = L10n.Text("{0:0.#}/{1:0.#} GB", "{0:0.#}/{1:0.#} ГБ", 128, 128);
            overlay.VolumeSlider.Value = 100;
            overlay.VolumeValue.Text = "100%";
        }
        // A square, high-contrast fixture exercises the real decoder and rounded clip.
        var coverDrawing = new DrawingVisual();
        using (var drawing = coverDrawing.RenderOpen())
        {
            drawing.DrawRectangle(new LinearGradientBrush(Color.FromRgb(241, 136, 82), Color.FromRgb(48, 88, 157), 45), null, new Rect(0, 0, 96, 96));
            drawing.DrawEllipse(new SolidColorBrush(Color.FromRgb(235, 220, 176)), null, new Point(67, 27), 14, 14);
            drawing.DrawRectangle(new SolidColorBrush(Color.FromArgb(180, 22, 42, 69)), null, new Rect(0, 64, 96, 32));
        }
        var coverBitmap = new RenderTargetBitmap(96, 96, 96, 96, PixelFormats.Pbgra32);
        coverBitmap.Render(coverDrawing);
        using (var coverStream = new MemoryStream())
        {
            var coverEncoder = new PngBitmapEncoder();
            coverEncoder.Frames.Add(BitmapFrame.Create(coverBitmap));
            coverEncoder.Save(coverStream);
            // Invalid payload followed by the same now-complete buffer must be retryable.
            var coverBytes = coverStream.ToArray();
            var changingBuffer = new byte[coverBytes.Length];
            overlay.SetArtwork(changingBuffer);
            coverBytes.CopyTo(changingBuffer, 0);
            overlay.SetArtwork(changingBuffer);
            if (overlay.Artwork.Source is null) throw new InvalidOperationException("Artwork decode did not recover.");
        }
        var content = (FrameworkElement)overlay.Content;
        var image = RenderElement(content);
        SavePng(image, outputPath);
        var playGeometry = overlay.PlaybackGlyph.Data;
        overlay.PlaybackGlyph.Data = Geometry.Parse("M 1,0 L 5,0 5,14 1,14 Z M 9,0 L 13,0 13,14 9,14 Z");
        SavePng(RenderElement(content), Path.ChangeExtension(outputPath, ".pause.png"));
        overlay.PlaybackGlyph.Data = playGeometry;

        var scene = new DrawingVisual();
        var width = image.PixelWidth + 64;
        var sceneHalfHeight = Math.Max(150, image.PixelHeight + 64);
        var sceneHeight = sceneHalfHeight * 2;
        using (var drawing = scene.RenderOpen())
        {
            drawing.DrawRectangle(new LinearGradientBrush(Color.FromRgb(10, 24, 42), Color.FromRgb(71, 98, 119), 0), null, new Rect(0, 0, width, sceneHalfHeight));
            drawing.DrawRectangle(new LinearGradientBrush(Color.FromRgb(155, 183, 199), Color.FromRgb(66, 100, 121), 0), null, new Rect(0, sceneHalfHeight, width, sceneHalfHeight));
            for (var x = 0; x < width; x += 80)
            {
                drawing.DrawRectangle(new SolidColorBrush(Color.FromArgb(45, 188, 225, 239)), null, new Rect(x, 0, 27, sceneHeight));
                drawing.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(35, 255, 255, 255)), 1), new Point(x, 0), new Point(x + 130, sceneHeight));
            }
            var imageTop = (sceneHalfHeight - image.PixelHeight) / 2d;
            drawing.DrawImage(image, new Rect(32, imageTop, image.PixelWidth, image.PixelHeight));
            drawing.DrawImage(image, new Rect(32, imageTop + sceneHalfHeight, image.PixelWidth, image.PixelHeight));
        }
        var comparison = new RenderTargetBitmap(width, sceneHeight, 96, 96, PixelFormats.Pbgra32);
        comparison.Render(scene);
        SavePng(comparison, Path.ChangeExtension(outputPath, ".backgrounds.png"));

        var settings = new SettingsWindow(overlay);
        var settingsContent = (FrameworkElement)settings.Content;
        if (themePreview && settings.FindName("ThemesCard") is FrameworkElement themeCard)
        {
            RenderElement(settingsContent, new Size(640, 790));
            themeCard.BringIntoView();
            settingsContent.UpdateLayout();
        }
        else if (networkPreview)
        {
            RenderElement(settingsContent, new Size(640, 790));
            settings.NetworkCard.BringIntoView();
            settingsContent.UpdateLayout();
        }
        else if (variant is "cpu-driver-missing" or "cpu-access-denied")
        {
            RenderElement(settingsContent, new Size(640, 790));
            settings.HardwareCard.BringIntoView();
            settingsContent.UpdateLayout();
        }
        else if (variant == "updates")
        {
            RenderElement(settingsContent, new Size(640, 790));
            settings.UpdatesCard.BringIntoView();
            settingsContent.UpdateLayout();
        }
        else if (variant is "hotkeys" or "support")
        {
            RenderElement(settingsContent, new Size(640, 790));
            FindVisualChild<ScrollViewer>(settingsContent)?.ScrollToEnd();
            settingsContent.UpdateLayout();
        }
        else if (overlay.Settings.GlowEnabled)
        {
            RenderElement(settingsContent, new Size(640, 790));
            // Bring the real Glow section into view for the diagnostic screenshot.
            if (settings.GlowEnabledCheck.Parent is FrameworkElement { Parent: FrameworkElement card })
                card.BringIntoView();
            settingsContent.UpdateLayout();
        }
        SavePng(RenderElement(settingsContent, new Size(640, 790)), Path.ChangeExtension(outputPath, ".settings.png"));
        settings.Close();
        overlay.Close();
    }

    private static void RunThemeSmokeTest(string outputPath)
    {
        // Pure presentation fixtures: no HWND hooks, sensor sampling, media commands,
        // update checks, elevation requests, or changes to the user's settings.
        var preview = new OverlayWindow(initializeServices: false, allowWheelInterception: false,
            allowHotkeyRegistration: false, allowUpdateChecks: false);
        var reports = new List<object>();
        var pictures = new List<(string Label, BitmapSource Image)>();
        var directory = Path.GetDirectoryName(outputPath)!;
        var assertions = 0;
        void Require(bool condition, string message)
        {
            assertions++;
            if (!condition) throw new InvalidOperationException("Theme presentation: " + message);
        }
        try
        {
            preview.Settings.Theme = null;
            preview.Settings.Scale = 1;
            preview.Settings.GlowEnabled = false;
            preview.Settings.CompactMode = false;
            preview.Settings.UseWindowsAccent = false;
            preview.Settings.CustomAccentColor = "#65DDF2";
            preview.Settings.Language = "en";
            preview.Settings.ShowMusic = preview.Settings.ShowFps = preview.Settings.ShowCpu
                = preview.Settings.ShowGpu = preview.Settings.ShowRam = preview.Settings.ShowNetwork = true;
            preview.ApplySettings();
            SetThemePreviewValues(preview);
            var content = (FrameworkElement)preview.Content;
            var legacy = Services.HudThemeCatalog.Resolve(preview.Settings);
            var regular = RenderElement(content);
            var legacySize = content.DesiredSize;
            var musicVolume = preview.Settings.ScrollMusicVolume;
            var elevated = preview.Settings.RequestAdministratorOnStartup;
            var updateChecks = preview.Settings.CheckForUpdates;
            var modules = new Dictionary<string, FrameworkElement>
            {
                ["music"] = preview.MusicSection, ["fps"] = preview.FpsSection,
                ["cpu"] = preview.CpuSection, ["gpu"] = preview.GpuSection,
                ["ram"] = preview.RamSection, ["network"] = preview.NetworkSection
            };
            foreach (var language in new[] { "en", "ru" })
            foreach (var id in new[] { "classic", "capsule", "slate", "terminal", "paper" })
            foreach (var compact in new[] { false, true })
            {
                var name = $"{id}-{language}-{(compact ? "compact" : "regular")}";
                preview.Settings.Language = language;
                Services.HudThemeCatalog.Apply(preview.Settings, Services.HudThemeCatalog.GetPreset(id));
                preview.Settings.CompactMode = compact;
                preview.ApplySettings();
                SetThemePreviewValues(preview);
                var theme = Services.HudThemeCatalog.Resolve(preview.Settings);
                var image = RenderElement(content);
                Require(content.DesiredSize.Width is > 0 and < 4096
                    && content.DesiredSize.Height is > 0 and < 4096, name + " has bounded nonempty dimensions.");
                Require(preview.Settings.ScrollMusicVolume == musicVolume
                    && preview.Settings.RequestAdministratorOnStartup == elevated
                    && preview.Settings.CheckForUpdates == updateChecks, name + " preserves volume, startup, and update preferences.");
                Require(preview.PlayButton.Visibility == Visibility.Visible
                    && preview.PlayButton.ActualWidth > 0 && preview.PlayButton.ActualHeight > 0,
                    name + " retains the playback action.");
                var circle = preview.PlaybackCircle.TransformToAncestor(preview.PlayButton)
                    .TransformBounds(new Rect(preview.PlaybackCircle.RenderSize));
                Require(new Rect(preview.PlayButton.RenderSize).Contains(circle), name + " playback button contains its glyph.");
                foreach (var block in new[] { preview.FpsValue, preview.CpuValue, preview.GpuValue,
                    preview.RamValue, preview.CpuTemperature, preview.GpuTemperature, preview.RamMemory,
                    preview.VolumeValue, preview.NetworkDownloadValue, preview.NetworkUploadValue })
                {
                    if (!IsThemeElementRendered(block, content)) continue;
                    var text = new FormattedText(block.Text, L10n.Culture, block.FlowDirection,
                        new Typeface(block.FontFamily, block.FontStyle, block.FontWeight, block.FontStretch),
                        block.FontSize, block.Foreground, 1);
                    Require(text.WidthIncludingTrailingWhitespace <= block.ActualWidth + 1.5,
                        $"{name} {block.Name} fits its numeric value ({text.WidthIncludingTrailingWhitespace:F1}/{block.ActualWidth:F1}).");
                    Require(text.Height <= block.ActualHeight + 2,
                        $"{name} {block.Name} fits its text height ({text.Height:F1}/{block.ActualHeight:F1}).");
                    var module = modules.First(pair => IsThemeDescendantOf(block, pair.Value)).Value;
                    var bounds = block.TransformToAncestor(module)
                        .TransformBounds(new Rect(block.RenderSize));
                    Require(bounds.X >= -1 && bounds.Y >= -1 && bounds.Right <= module.ActualWidth + 1
                        && bounds.Bottom <= module.ActualHeight + 1, name + " values remain within their module.");
                }
                var visible = theme.ModuleOrder.Select(moduleId => modules[moduleId]).Where(item => item.Visibility == Visibility.Visible).ToArray();
                for (var index = 1; index < visible.Length; index++)
                {
                    var previousBounds = visible[index - 1].TransformToAncestor(content)
                        .TransformBounds(new Rect(visible[index - 1].RenderSize));
                    var nextBounds = visible[index].TransformToAncestor(content)
                        .TransformBounds(new Rect(visible[index].RenderSize));
                    Require(theme.Orientation == "vertical"
                        ? nextBounds.Top >= previousBounds.Bottom - 1
                        : nextBounds.Left >= previousBounds.Right - 1, name + " applies ordered modules without overlap.");
                }
                SavePng(image, Path.Combine(directory, name + ".png"));
                if (language == "en") pictures.Add(($"{id} / {(compact ? "compact" : "regular")}", image));
                reports.Add(new { Theme = id, Language = language, Compact = compact,
                    Width = image.PixelWidth, Height = image.PixelHeight, theme.Layout, theme.Orientation });
            }
            // Exercise a theme with a nondefault order and both layout directions on
            // the same live control tree, rather than only creating fresh windows.
            foreach (var orientation in new[] { "horizontal", "vertical" })
            {
                var reordered = Services.HudThemeCatalog.GetPreset("slate");
                reordered.Orientation = orientation;
                reordered.ModuleOrder = ["network", "ram", "gpu", "cpu", "fps", "music"];
                Services.HudThemeCatalog.Apply(preview.Settings, reordered);
                preview.Settings.CompactMode = false;
                preview.ApplySettings();
                SetThemePreviewValues(preview);
                RenderElement(content);
                var first = preview.NetworkSection.TransformToAncestor(content).Transform(new Point());
                var last = preview.MusicSection.TransformToAncestor(content).Transform(new Point());
                Require(orientation == "horizontal" ? first.X < last.X : first.Y < last.Y,
                    "Switching direction retains a reversed module order.");
                preview.Settings.ShowCpu = preview.Settings.ShowGpu = false;
                preview.ApplySettings();
                RenderElement(content);
                Require(preview.CpuSection.Visibility == Visibility.Collapsed
                    && preview.GpuSection.Visibility == Visibility.Collapsed, "A theme preserves user-hidden modules.");
                var host = preview.FindName("ModuleHost") as StackPanel;
                Require(host is not null, "The live module host is present.");
                var hiddenFrames = host!.Children.OfType<Border>().Where(border => border.Tag is "cpu" or "gpu").ToArray();
                Require(hiddenFrames.Length == 2, "Each hidden module retains exactly one reusable card.");
                foreach (var hidden in hiddenFrames)
                    Require(hidden.Visibility == Visibility.Collapsed, "Hidden cards do not leave empty gaps.");
                preview.Settings.ShowCpu = preview.Settings.ShowGpu = true;
            }
            var editor = new SettingsWindow(preview);
            try
            {
                foreach (var language in new[] { "en", "ru" })
                {
                    editor.LanguageCombo.SelectedValue = language;
                    editor.ThemeCombo.SelectedValue = "paper";
                    Require(preview.Settings.Theme?.Id == "paper"
                        && editor.BackgroundColorHexBox.Text == "#F8EEDC"
                        && editor.TextColorHexBox.Text == "#272A31", "Preset selection synchronizes existing palette controls.");
                    editor.ThemeRadiusSlider.Value = 0;
                    Require(preview.Settings.Theme?.Id == "custom" && preview.PanelSurface.CornerRadius.TopLeft == 0,
                        "A geometry edit becomes a custom theme and updates the live panel.");
                    editor.ThemeNameBox.Text = "QA theme " + language;
                    editor.ThemeHeightSlider.Value = 28;
                    editor.ThemePaddingHorizontalSlider.Value = 0;
                    editor.ThemePaddingVerticalSlider.Value = 0;
                    editor.ThemeGapSlider.Value = 0;
                    editor.ThemeBorderSlider.Value = 4;
                    Require(preview.Settings.Theme?.Name == "QA theme " + language
                        && (string?)editor.ThemeCombo.SelectedValue == "custom",
                        "A custom theme retains its edited name and selection.");
                    editor.ThemeLayoutCombo.SelectedValue = "cards";
                    editor.ThemeOrientationCombo.SelectedValue = "vertical";
                    editor.ThemeFontCombo.SelectedValue = "Consolas";
                    editor.ThemeValueSizeSlider.Value = 32;
                    editor.ThemeLabelSizeSlider.Value = 16;
                    editor.ThemeSecondarySizeSlider.Value = 20;
                    editor.ThemeLabelsCheck.IsChecked = false;
                    editor.ThemeIconsCheck.IsChecked = false;
                    editor.ThemeArtworkCheck.IsChecked = false;
                    editor.ThemeVolumeCheck.IsChecked = false;
                    SetThemePreviewValues(preview);
                    var editorImage = RenderElement(content);
                    Require(preview.ModuleHost.Orientation == Orientation.Vertical
                        && preview.PanelSurface.BorderThickness.Left == 0
                        && preview.FontFamily.Source == "Consolas", "Layout direction, cards, and font edits reach the HUD.");
                    Require(preview.CpuLabel.Visibility == Visibility.Collapsed
                        && preview.NetworkLabel.Visibility == Visibility.Collapsed
                        && preview.CpuIcon.Visibility == Visibility.Collapsed
                        && preview.ArtworkFrame.Visibility == Visibility.Collapsed
                        && preview.VolumeSlider.Visibility == Visibility.Collapsed
                        && preview.PlayButton.Visibility == Visibility.Visible, "Optional decoration hides while playback remains available.");
                    foreach (var block in new[] { preview.FpsValue, preview.CpuValue, preview.RamValue,
                        preview.CpuTemperature, preview.RamMemory, preview.NetworkDownloadValue })
                    {
                        var text = new FormattedText(block.Text, L10n.Culture, block.FlowDirection,
                            new Typeface(block.FontFamily, block.FontStyle, block.FontWeight, block.FontStretch),
                            block.FontSize, block.Foreground, 1);
                        Require(text.WidthIncludingTrailingWhitespace <= block.ActualWidth + 1.5
                            && text.Height <= block.ActualHeight + 2, "Largest editable fonts fit numeric columns.");
                    }
                    editor.ThemeModuleOrderList.SelectedIndex = 5;
                    editor.ThemeModuleUpButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Require(Services.HudThemeCatalog.Resolve(preview.Settings).ModuleOrder[4] == "network",
                        "Moving a selected module updates the saved order.");
                    editor.CompactModeCheck.IsChecked = true;
                    var compactEditorImage = RenderElement(content);
                    Require(compactEditorImage.PixelHeight < editorImage.PixelHeight,
                        "Compact mode also reduces an edited vertical theme.");
                    editor.CompactModeCheck.IsChecked = false;
                    editor.TextColorHexBox.Text = "#E8EFDD";
                    var exported = Services.HudThemeCatalog.Import(Services.HudThemeCatalog.Export(preview.Settings));
                    Require(exported.TextColor == "#E8EFDD" && exported.Layout == "cards"
                        && exported.Orientation == "vertical" && exported.ValueFontSize == 32
                        && exported.ContentHeight == 28 && exported.PaddingHorizontal == 0
                        && exported.PaddingVertical == 0 && exported.ModuleGap == 0 && exported.BorderThickness == 4,
                        "Theme export contains geometry and the current palette.");
                    preview.SaveSettings();
                    var loaded = Services.SettingsStore.Load();
                    Require(loaded.Theme?.FontFamily == "Consolas" && loaded.TextColor == "#E8EFDD"
                        && !loaded.CompactMode, "The edited appearance survives a settings reload.");
                    editor.ThemeAdvanced.IsExpanded = true;
                    RenderElement((FrameworkElement)editor.Content, new Size(640, 790));
                    editor.ThemesCard.BringIntoView();
                    SavePng(RenderElement((FrameworkElement)editor.Content, new Size(640, 790)),
                        Path.Combine(directory, $"themes-editor-{language}.png"));
                }
            }
            finally { editor.Close(); }
            // Revert the exact window used for all preceding modes to the old panel.
            Services.HudThemeCatalog.Apply(preview.Settings, legacy);
            preview.Settings.Theme = null;
            preview.Settings.Language = "en";
            preview.ApplySettings();
            SetThemePreviewValues(preview);
            var restored = RenderElement(content);
            Require(content.DesiredSize == legacySize, "Returning to Classic restores legacy dimensions.");
            var stride = regular.PixelWidth * 4;
            var before = new byte[stride * regular.PixelHeight];
            var after = new byte[before.Length];
            regular.CopyPixels(before, stride, 0);
            restored.CopyPixels(after, stride, 0);
            Require(before.AsSpan().SequenceEqual(after), "Returning to Classic restores the exact legacy rendering.");
            SaveThemeContactSheet(pictures, Path.Combine(directory, "themes-contact-sheet.png"));
            File.WriteAllText(outputPath, JsonSerializer.Serialize(new
            {
                Success = true, Assertions = assertions, Cases = reports,
                LegacyRestored = true, ThemeEditorExercised = true, ServicesInitialized = false,
                HotkeyRegistrationEnabled = false, WheelInterceptionEnabled = false,
                AutomaticUpdateChecksEnabled = false
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally { preview.Close(); }
    }

    private static void SetThemePreviewValues(OverlayWindow preview)
    {
        // Deliberately long values reveal clipped columns and compact controls.
        preview.TrackTitle.Text = "A very long track title that must remain inside its column";
        preview.TrackArtist.Text = "An artist with a similarly long name";
        preview.FpsValue.Text = "999";
        preview.CpuValue.Text = preview.GpuValue.Text = preview.RamValue.Text = "100%";
        preview.CpuTemperature.Text = preview.GpuTemperature.Text = "100°C";
        preview.RamMemory.Text = L10n.Text("{0:0.#}/{1:0.#} GB", "{0:0.#}/{1:0.#} ГБ", 128, 128);
        preview.VolumeSlider.Value = 100;
        preview.VolumeValue.Text = "100%";
        preview.SetNetworkPreview(CreateNetworkPreview());
    }

    private static bool IsThemeElementRendered(FrameworkElement element, FrameworkElement root)
    {
        for (DependencyObject? item = element; item is not null; item = VisualTreeHelper.GetParent(item))
        {
            if (item is UIElement { Visibility: not Visibility.Visible }) return false;
            if (ReferenceEquals(item, root)) return true;
        }
        return false;
    }

    private static bool IsThemeDescendantOf(DependencyObject child, DependencyObject parent)
    {
        for (DependencyObject? item = child; item is not null; item = VisualTreeHelper.GetParent(item))
            if (ReferenceEquals(item, parent)) return true;
        return false;
    }

    private static void SaveThemeContactSheet(IReadOnlyList<(string Label, BitmapSource Image)> pictures, string path)
    {
        var width = Math.Max(700, pictures.Max(item => item.Image.PixelWidth) + 48);
        var height = pictures.Sum(item => item.Image.PixelHeight + 64) + 24;
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(new SolidColorBrush(Color.FromRgb(20, 27, 37)), null, new Rect(0, 0, width, height));
            double top = 24;
            foreach (var (label, image) in pictures)
            {
                drawing.DrawText(new FormattedText(label, System.Globalization.CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, new Typeface("Segoe UI"), 14, Brushes.White, 1), new Point(24, top));
                drawing.DrawImage(image, new Rect(24, top + 28, image.PixelWidth, image.PixelHeight));
                top += image.PixelHeight + 64;
            }
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        SavePng(bitmap, path);
    }

    private static Models.NetworkSnapshot CreateNetworkPreview(bool idle = false)
    {
        // Explicit render diagnostics only: never inserted into the live sampler.
        var now = DateTimeOffset.UtcNow;
        var adapter = new Models.NetworkAdapterInfo("preview-adapter",
            L10n.Text("Ethernet (preview)", "Ethernet (пример)"), "Design preview", true, true, true, 1_000_000_000);
        var history = Enumerable.Range(0, 60).Where(index => index is not (40 or 41)).Select(index => new Models.NetworkHistorySample(
            now.AddSeconds(index - 59),
            index is 22 or 23 ? null : idle ? 0 : 2_000_000 + 9_000_000 * (.5 + .5 * Math.Sin(index * .26)),
            index is 22 or 23 ? null : idle ? 0 : 150_000 + 950_000 * (.5 + .5 * Math.Cos(index * .18)))).ToArray();
        history[^1] = new Models.NetworkHistorySample(now, idle ? 0 : 12_400_000, idle ? 0 : 850_000);
        return new Models.NetworkSnapshot(Models.NetworkStatus.Ready, adapter.Id, adapter.Name,
            adapter.LinkSpeedBitsPerSecond, idle ? 0 : 12_400_000, idle ? 0 : 850_000,
            2_450_000_000, 180_000_000, [adapter], history, now);
    }

    private static Services.TelemetrySnapshot CreateCpuTemperaturePreview(bool accessDenied = false)
        => new(32, null, 78, 68, 8_600_000_000, 16_000_000_000, 54,
            "Intel Core i5-10400F", "NVIDIA GeForce (preview)", L10n.Text("CPU temperature unavailable", "Температура CPU недоступна"))
        {
            CpuTemperatureState = accessDenied ? Services.CpuTemperatureState.AccessDenied : Services.CpuTemperatureState.DriverMissing,
            CpuTemperatureAccess = new(accessDenied, accessDenied ? "2.2.0" : null, false, accessDenied ? 5 : 2)
        };

    private static RenderTargetBitmap RenderElement(FrameworkElement element, Size? fixedSize = null)
    {
        element.Measure(fixedSize ?? new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = fixedSize ?? element.DesiredSize;
        element.Arrange(new Rect(size));
        element.UpdateLayout();
        var bitmap = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(size.Width)), Math.Max(1, (int)Math.Ceiling(size.Height)), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        return bitmap;
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T found) return found;
            if (FindVisualChild<T>(child) is { } nested) return nested;
        }
        return null;
    }

    private static void SavePng(BitmapSource bitmap, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private async Task RunSmokeTestAsync(string outputPath)
    {
        var overlay = new OverlayWindow(allowWheelInterception: false, allowHotkeyRegistration: false, allowUpdateChecks: false);
        MainWindow = overlay;
        try
        {
            overlay.Show();
            overlay.ToggleVisibility();
            await overlay.RefreshSnapshotsAsync();
            await Task.Delay(1500);
            await overlay.RefreshSnapshotsAsync();
            var telemetryIndependentOfMedia = await CheckIndependentTelemetryAsync(overlay);
            var settings = new SettingsWindow(overlay);
            settings.Measure(new Size(640, 790));
            settings.LanguageCombo.SelectedValue = "en";
            var englishUi = settings.Title == (string)Current.Resources["Loc.SettingsTitle"] && L10n.Language == "en";
            var englishTitle = settings.Title;
            settings.LanguageCombo.SelectedValue = "ru";
            overlay.SaveSettings();
            var russianUi = L10n.Language == "ru"
                && settings.Title == (string)Current.Resources["Loc.SettingsTitle"]
                && settings.Title != englishTitle
                && (string)settings.LanguageCombo.SelectedValue == "ru"
                && Services.SettingsStore.Load().Language == "ru";
            var russianTitle = settings.Title;
            var russianLanguage = L10n.Language;
            settings.LanguageCombo.SelectedValue = "en";
            var englishRestored = settings.Title == englishTitle && L10n.Language == "en";
            if (!englishUi || !russianUi || !englishRestored)
                throw new InvalidOperationException($"The settings language did not switch and persist correctly: English={englishUi} ({englishTitle}), Russian={russianUi} ({russianLanguage}, {russianTitle}), restored={englishRestored} ({settings.Title}).");
            var overlayColors = CheckOverlayColors(overlay, settings);
            var glowPresentation = CheckGlowPresentation(overlay, settings);
            var contextMenuPresentation = await CheckContextMenuPresentationAsync(overlay, settings);
            var networkPresentation = CheckNetworkPresentation();
            var compactPresentation = CheckCompactPresentation();
            var cpuTemperaturePresentation = CheckCpuTemperaturePresentation();
            settings.Close();
            overlay.Settings.Locked = true;
            overlay.ApplySettings();
            var handle = new WindowInteropHelper(overlay).Handle;
            var clickThroughEnabled = (GetWindowLongPtr(handle, -20).ToInt64() & 0x20) != 0;
            overlay.Settings.Locked = false;
            overlay.ApplySettings();
            var interactiveModeRestored = (GetWindowLongPtr(handle, -20).ToInt64() & 0x20) == 0;
            if (!clickThroughEnabled || !interactiveModeRestored)
                throw new InvalidOperationException("Overlay click-through state did not round-trip.");
            overlay.SaveSettings();
            File.WriteAllText(outputPath, JsonSerializer.Serialize(new
            {
                Success = true,
                Width = overlay.ActualWidth,
                Height = overlay.ActualHeight,
                overlay.Settings.BackgroundOpacity,
                overlay.FailedHotkeys,
                HotkeyRegistrationEnabled = false,
                ClickThroughEnabled = clickThroughEnabled,
                InteractiveModeRestored = interactiveModeRestored,
                overlay.CurrentAccentHex,
                overlay.WindowsAccentHex,
                PlayerVisible = overlay.Settings.ShowMusic,
                ScrollMusicVolumeEnabled = overlay.Settings.ScrollMusicVolume,
                SettingsPersisted = File.Exists(Services.SettingsStore.FilePath),
                Localization = new { EnglishUi = englishUi, RussianUi = russianUi, EnglishRestored = englishRestored },
                TelemetryIndependentOfMedia = telemetryIndependentOfMedia,
                OverlayColors = overlayColors,
                GlowPresentation = glowPresentation,
                ContextMenuPresentation = contextMenuPresentation,
                NetworkPresentation = networkPresentation,
                CompactPresentation = compactPresentation,
                CpuTemperaturePresentation = cpuTemperaturePresentation,
                Network = new
                {
                    Status = overlay.NetworkSnapshot?.Status.ToString(),
                    overlay.NetworkSnapshot?.AdapterName,
                    overlay.NetworkSnapshot?.DownloadBytesPerSecond,
                    overlay.NetworkSnapshot?.UploadBytesPerSecond,
                    overlay.NetworkSnapshot?.SessionReceivedBytes,
                    overlay.NetworkSnapshot?.SessionSentBytes,
                    AdapterCount = overlay.NetworkSnapshot?.AvailableAdapters.Count,
                    HistorySamples = overlay.NetworkSnapshot?.History.Count
                },
                Media = new { overlay.MediaSnapshot?.IsAvailable, SourceCount = overlay.MediaSnapshot?.Sessions.Count, overlay.MediaSnapshot?.Status },
                Audio = new { overlay.AudioSnapshot?.IsAvailable, SourceCount = overlay.AudioSnapshot?.Sessions.Count, overlay.AudioSnapshot?.Status },
                Telemetry = overlay.TelemetrySnapshot,
                Fps = overlay.FpsSnapshot
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally
        {
            await overlay.StopAsync();
            overlay.Close();
        }
    }

    private static bool CheckCpuTemperaturePresentation()
    {
        var preview = new OverlayWindow(initializeServices: false);
        SettingsWindow? details = null;
        try
        {
            preview.SetTelemetryPreview(CreateCpuTemperaturePreview());
            preview.SetFpsPreview(new(120, 42, "example-game", @"Failure at C:\Users\private-name\Game\capture.exe", true, true, false)
            {
                ParsedFrameCount = 120, SelectedSwapChain = "0x1234", ObservedSwapChainCount = 1,
                FrameDataAgeMilliseconds = 30, LastCaptureDiagnostic = @"Error at C:\Users\private-name\trace.etl"
            });
            details = new SettingsWindow(preview);
            RenderElement((FrameworkElement)details.Content, new Size(640, 790));
            if (details.PawnIoButton.Visibility != Visibility.Visible
                || details.PawnIoWebsiteButton.Visibility != Visibility.Visible
                || details.CpuTemperatureValue.Text != "-"
                || details.CpuTemperatureRestartButton.Visibility != Visibility.Collapsed)
                throw new InvalidOperationException("Missing CPU driver did not expose the bundled installer and accurate temperature status.");
            using (var report = JsonDocument.Parse(preview.GetDiagnosticReport()))
            {
                var cpu = report.RootElement.GetProperty("Cpu");
                if (cpu.GetProperty("TemperatureState").GetString() != "DriverMissing"
                    || cpu.GetProperty("TemperatureCelsius").ValueKind != JsonValueKind.Null
                    || cpu.GetProperty("Driver").GetProperty("DeviceOpenError").GetInt32() != 2
                    || report.RootElement.EnumerateObject().Any(property => property.Name is "Media" or "Audio" or "Network"))
                    throw new InvalidOperationException("CPU support diagnostics lost the actual missing-driver state.");
                if (preview.GetDiagnosticReport().Contains("private-name", StringComparison.Ordinal)
                    || report.RootElement.GetProperty("Fps").GetProperty("ParsedFrameCount").GetInt64() != 120)
                    throw new InvalidOperationException("Support diagnostics leaked free-form paths or dropped typed frame data.");
            }
            preview.SetTelemetryPreview(CreateCpuTemperaturePreview(accessDenied: true));
            var shouldOfferElevation = Services.AppRestartService.CanRequestElevation;
            if ((details.CpuTemperatureRestartButton.Visibility == Visibility.Visible) != shouldOfferElevation)
                throw new InvalidOperationException("The CPU permission recovery action does not match the current ability to elevate.");
            var sensor = new Services.CpuTemperatureSensorInfo("/intelcpu/0", "Intel Core i5-10400F",
                "/intelcpu/0/temperature/6", "CPU Package", 59, true, true);
            preview.SetTelemetryPreview(CreateCpuTemperaturePreview() with
            {
                CpuTemperatureCelsius = 59,
                CpuTemperatureState = Services.CpuTemperatureState.Ready,
                CpuTemperatureSource = sensor.Identifier,
                CpuTemperatureSensors = [sensor]
            });
            if (details.PawnIoButton.Visibility != Visibility.Collapsed
                || !details.CpuTemperatureDetails.Text.Contains("CPU Package", StringComparison.Ordinal)
                || details.CpuTemperatureDetails.Text.Contains(sensor.Identifier, StringComparison.Ordinal))
                throw new InvalidOperationException("A measured CPU package temperature did not replace driver recovery with its readable source.");
            return true;
        }
        finally { details?.Close(); preview.Close(); }
    }

    private static bool CheckCompactPresentation()
    {
        // Design-only controls: never register hooks, write audio volume or ask for UAC.
        var preview = new OverlayWindow(initializeServices: false);
        SettingsWindow? details = null;
        try
        {
            preview.Settings.CompactMode = false;
            preview.Settings.GlowEnabled = false;
            preview.Settings.Scale = 1;
            preview.Settings.ScrollMusicVolume = true;
            preview.Settings.ShowMusic = preview.Settings.ShowFps = preview.Settings.ShowCpu
                = preview.Settings.ShowGpu = preview.Settings.ShowRam = preview.Settings.ShowNetwork = true;
            preview.ApplySettings();
            preview.SetNetworkPreview(CreateNetworkPreview());
            var content = (FrameworkElement)preview.Content;
            var regular = RenderElement(content);
            var regularSize = content.DesiredSize;
            details = new SettingsWindow(preview);
            for (var pass = 0; pass < 2; pass++)
            {
                details.CompactModeCheck.IsChecked = true;
                preview.SaveSettings(); // Settings normally debounce writes for 450 ms.
                RenderElement(content);
                if (!preview.Settings.CompactMode || Services.SettingsStore.Load().CompactMode != true
                    || preview.PanelSurface.ActualHeight != 30 || content.DesiredSize.Width >= regularSize.Width * .7
                    || content.DesiredSize.Height >= regularSize.Height * .6
                    || preview.ArtworkFrame.Visibility != Visibility.Collapsed
                    || preview.CpuIcon.Visibility != Visibility.Collapsed || preview.GpuIcon.Visibility != Visibility.Collapsed
                    || preview.RamIcon.Visibility != Visibility.Collapsed || preview.NetworkIcon.Visibility != Visibility.Collapsed
                    || preview.VolumeSlider.Visibility != Visibility.Collapsed || preview.MuteButton.Visibility != Visibility.Collapsed
                    || preview.PlayButton.Visibility != Visibility.Visible || !preview.Settings.ScrollMusicVolume
                    || preview.MusicSection.ActualWidth <= 0)
                    throw new InvalidOperationException("Compact layout did not reduce the panel or retain its music control area.");
                var circleBounds = preview.PlaybackCircle.TransformToAncestor(preview.PlayButton)
                    .TransformBounds(new Rect(preview.PlaybackCircle.RenderSize));
                if (!new Rect(preview.PlayButton.RenderSize).Contains(circleBounds)
                    || preview.PlaybackGlyph.ActualHeight > preview.PlaybackCircle.ActualHeight - 2)
                    throw new InvalidOperationException("Compact playback controls are clipped.");
                // Changing the preference only persists it; it never invokes a launcher.
                details.RequestAdministratorCheck.IsChecked = false;
                preview.SaveSettings();
                if (Services.SettingsStore.Load().RequestAdministratorOnStartup)
                    throw new InvalidOperationException("The administrator startup opt-out did not persist.");
                details.RequestAdministratorCheck.IsChecked = true;
                preview.SaveSettings();
                if (!Services.SettingsStore.Load().RequestAdministratorOnStartup)
                    throw new InvalidOperationException("The administrator startup preference did not persist.");
                details.CompactModeCheck.IsChecked = false;
                var restored = RenderElement(content);
                if (content.DesiredSize != regularSize || preview.ArtworkFrame.Visibility != Visibility.Visible
                    || preview.VolumeSlider.Visibility != Visibility.Visible || preview.CpuIcon.Visibility != Visibility.Visible
                    || preview.PanelContent.Height != 50 || preview.NetworkContent.Orientation != Orientation.Vertical)
                    throw new InvalidOperationException("The regular layout did not restore after compact mode.");
                var stride = regular.PixelWidth * 4;
                var expected = new byte[stride * regular.PixelHeight];
                var actual = new byte[expected.Length];
                regular.CopyPixels(expected, stride, 0);
                restored.CopyPixels(actual, stride, 0);
                if (!expected.AsSpan().SequenceEqual(actual))
                    throw new InvalidOperationException("A compact-mode round trip changed the regular presentation.");
            }
            return true;
        }
        finally { details?.Close(); preview.Close(); }
    }

    private static bool CheckNetworkPresentation()
    {
        var preview = new OverlayWindow(initializeServices: false);
        try
        {
            preview.Settings.ShowMusic = preview.Settings.ShowFps = preview.Settings.ShowCpu
                = preview.Settings.ShowGpu = preview.Settings.ShowRam = false;
            preview.Settings.ShowNetwork = true;
            preview.ApplySettings();
            preview.SetNetworkPreview(CreateNetworkPreview());
            var content = (FrameworkElement)preview.Content;
            RenderElement(content);
            if (preview.NetworkSection.Visibility != Visibility.Visible || preview.FpsSection.Visibility != Visibility.Collapsed
                || preview.RamDivider.Visibility != Visibility.Collapsed || content.DesiredSize.Width is < 120 or > 260
                || preview.NetworkDownloadValue.Text != Services.NetworkValueFormatter.FormatRate(12_400_000))
                throw new InvalidOperationException("The network-only overlay layout or rate formatting is invalid.");
            preview.SetNetworkPreview(CreateNetworkPreview(idle: true));
            if (preview.NetworkDownloadValue.Text != Services.NetworkValueFormatter.FormatRate(0)
                || preview.NetworkUploadValue.Text != Services.NetworkValueFormatter.FormatRate(0))
                throw new InvalidOperationException("A measured idle network must remain a valid zero.");
            var details = new SettingsWindow(preview);
            try
            {
                RenderElement((FrameworkElement)details.Content, new Size(640, 790));
                var downFigures = PathGeometry.CreateFromGeometry(details.NetworkDownloadHistory.Data).Figures.Count;
                var upFigures = PathGeometry.CreateFromGeometry(details.NetworkUploadHistory.Data).Figures.Count;
                if (downFigures != 3 || upFigures != 3)
                    throw new InvalidOperationException($"Network history must preserve missing measurements and missing seconds: down={downFigures}, up={upFigures}.");
            }
            finally { details.Close(); }
            return true;
        }
        finally { preview.Close(); }
    }

    private static bool CheckOverlayColors(OverlayWindow overlay, SettingsWindow settings)
    {
        var originalText = overlay.Settings.TextColor;
        var originalBackground = overlay.Settings.BackgroundColor;
        var alpha = ((SolidColorBrush)overlay.PanelSurface.Background).Color.A;
        try
        {
            settings.TextColorPresetCombo.SelectedValue = "black";
            if (overlay.Settings.TextColor != "#000000"
                || ((SolidColorBrush)Current.Resources["OverlayTextBrush"]).Color != Colors.Black)
                throw new InvalidOperationException("The text color preset did not update the overlay.");
            settings.BackgroundColorPresetCombo.SelectedValue = "white";
            var background = ((SolidColorBrush)overlay.PanelSurface.Background).Color;
            if (overlay.Settings.BackgroundColor != "#FFFFFF" || background.R != 255
                || background.G != 255 || background.B != 255 || background.A != alpha)
                throw new InvalidOperationException("The background preset did not preserve opacity.");
            settings.TextColorHexBox.Text = "#D45566";
            overlay.SaveSettings();
            var saved = Services.SettingsStore.Load();
            if (saved.TextColor != "#D45566" || saved.BackgroundColor != "#FFFFFF")
                throw new InvalidOperationException("Overlay colors did not persist.");
            settings.TextColorHexBox.Text = "oops";
            if (overlay.Settings.TextColor != "#D45566" || settings.TextColorError.Visibility != Visibility.Visible)
                throw new InvalidOperationException("An invalid text color changed the saved color.");
            return true;
        }
        finally
        {
            settings.TextColorHexBox.Text = originalText;
            settings.BackgroundColorHexBox.Text = originalBackground;
            overlay.SaveSettings();
        }
    }

    private static bool CheckGlowPresentation(OverlayWindow overlay, SettingsWindow settings)
    {
        var originalEnabled = overlay.Settings.GlowEnabled;
        var originalReactive = overlay.Settings.GlowReactive;
        var originalIntensity = overlay.Settings.GlowIntensity;
        var originalSensitivity = overlay.Settings.GlowSensitivity;
        var originalStyle = overlay.Settings.GlowStyle;
        var originallyVisible = overlay.IsVisible;
        try
        {
            settings.GlowReactiveCheck.IsChecked = false;
            settings.GlowEnabledCheck.IsChecked = false;
            if (!originallyVisible) overlay.ToggleVisibility();
            overlay.UpdateLayout();
            var origin = overlay.PanelSurface.PointToScreen(new Point());
            var size = new Size(overlay.ActualWidth, overlay.ActualHeight);
            settings.GlowIntensitySlider.Value = .8;
            settings.GlowEnabledCheck.IsChecked = true;
            overlay.UpdateLayout();
            var glow = Current.Windows.OfType<Controls.ReactiveGlowWindow>().Single();
            var handle = new WindowInteropHelper(glow).Handle;
            var styles = GetWindowLongPtr(handle, -20).ToInt64();
            var position = overlay.PanelSurface.PointToScreen(new Point());
            if (!glow.IsVisible || glow.Surface.Level <= 0 || (styles & 0x080800A0) != 0x080800A0
                || (position - origin).Length > 1 || size != new Size(overlay.ActualWidth, overlay.ActualHeight))
                throw new InvalidOperationException("Glow changed the panel bounds or lost its non-activating, click-through native style.");
            foreach (var style in new[] { Models.GlowStyles.NeonFlow, Models.GlowStyles.RainbowWave, Models.GlowStyles.NeonFlow })
            {
                settings.GlowStyleCombo.SelectedValue = style;
                if (overlay.Settings.GlowStyle != style || overlay.MusicGlow.EffectStyle != style || glow.Surface.EffectStyle != style)
                    throw new InvalidOperationException("The selected glow style did not update both live layers.");
            }
            settings.GlowIntensitySlider.Value = 0;
            if (glow.IsVisible || glow.Surface.Level != 0)
                throw new InvalidOperationException("Zero glow intensity left an active halo.");
            settings.GlowIntensitySlider.Value = .8;
            settings.GlowReactiveCheck.IsChecked = true;
            settings.GlowSensitivitySlider.Value = .92;
            if (glow.Surface.Level < .7 || overlay.MusicGlow.Level != glow.Surface.Level
                || overlay.MusicGlow.Layer != Controls.GlowLayer.Inner || glow.Surface.Layer != Controls.GlowLayer.Outer)
                throw new InvalidOperationException("The inner and outer rainbow baseline did not remain visible without a music boost.");
            // A diagnostic-only signal exercises the production controller-to-layer wiring.
            // No playback, volume, microphone or external input is changed by this fixture.
            const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            var motion = typeof(OverlayWindow).GetField("_glowMotion", flags)?.GetValue(overlay) as Services.GlowMusicMotion
                ?? throw new InvalidOperationException("The glow motion controller was not found.");
            if (Math.Abs(motion.Sensitivity - .92) > .001)
                throw new InvalidOperationException("The music sensitivity control did not update the detector.");
            motion.ResetMusic();
            motion.Update(1, true, TimeSpan.FromMilliseconds(33));
            typeof(OverlayWindow).GetMethod("SetGlowVisuals", flags)!.Invoke(overlay, [motion.Level]);
            if (overlay.MusicGlow.Pulse < .2 || overlay.MusicGlow.Pulse != glow.Surface.Pulse
                || overlay.MusicGlow.PulsePosition != glow.Surface.PulsePosition)
                throw new InvalidOperationException("The music pulse did not reach both glow layers.");
            settings.GlowReactiveCheck.IsChecked = false;
            if (overlay.MusicGlow.Pulse != 0 || glow.Surface.Pulse != 0)
                throw new InvalidOperationException("Disabling music response left a stale pulse.");
            settings.GlowReactiveCheck.IsChecked = true;
            settings.GlowEnabledCheck.IsChecked = false;
            if (glow.IsVisible || glow.Surface.Level != 0
                || (overlay.PanelSurface.PointToScreen(new Point()) - origin).Length > 1)
                throw new InvalidOperationException("Disabling glow did not restore the original panel presentation.");
            overlay.SaveSettings();
            var saved = Services.SettingsStore.Load();
            if (saved.GlowEnabled || !saved.GlowReactive || Math.Abs(saved.GlowIntensity - .8) > .001
                || saved.GlowStyle != Models.GlowStyles.NeonFlow || Math.Abs(saved.GlowSensitivity - .92) > .001)
                throw new InvalidOperationException("Glow settings did not persist.");
            return true;
        }
        finally
        {
            settings.GlowReactiveCheck.IsChecked = originalReactive;
            settings.GlowIntensitySlider.Value = originalIntensity;
            settings.GlowSensitivitySlider.Value = originalSensitivity;
            settings.GlowStyleCombo.SelectedValue = originalStyle;
            settings.GlowEnabledCheck.IsChecked = originalEnabled;
            if (!originallyVisible && overlay.IsVisible) overlay.ToggleVisibility();
            overlay.ApplySettings();
            overlay.SaveSettings();
        }
    }

    private static async Task<bool> CheckContextMenuPresentationAsync(OverlayWindow overlay, SettingsWindow existingSettings)
    {
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var originallyVisible = overlay.IsVisible;
        var enabled = overlay.Settings.GlowEnabled;
        var reactive = overlay.Settings.GlowReactive;
        var intensity = overlay.Settings.GlowIntensity;
        var effectStyle = overlay.Settings.GlowStyle;
        ContextMenu? menu = null;
        try
        {
            if (!originallyVisible) overlay.ToggleVisibility();
            overlay.Settings.GlowEnabled = true;
            overlay.Settings.GlowReactive = false;
            overlay.Settings.GlowIntensity = .8;
            overlay.Settings.GlowStyle = Models.GlowStyles.NeonFlow;
            overlay.ApplySettings();
            overlay.UpdateLayout();
            var handler = typeof(OverlayWindow).GetMethod("Window_MouseRightButtonUp", flags)
                ?? throw new InvalidOperationException("The HUD context-menu handler was not found.");
            handler.Invoke(overlay, [overlay, new System.Windows.Input.MouseButtonEventArgs(
                System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Right)
                { RoutedEvent = UIElement.MouseRightButtonUpEvent }]);
            menu = typeof(OverlayWindow).GetField("_hudMenu", flags)?.GetValue(overlay) as ContextMenu
                ?? throw new InvalidOperationException("Right-click did not create the HUD context menu.");
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Relative;
            menu.HorizontalOffset = 24;
            menu.VerticalOffset = 4;
            await Task.Delay(100);
            var menuSource = PresentationSource.FromVisual(menu) as HwndSource
                ?? throw new InvalidOperationException("The context menu has no native popup.");
            var glow = Current.Windows.OfType<Controls.ReactiveGlowWindow>().Single();
            var phase = glow.Surface.Phase;
            // Allow several real FPS/visibility refreshes, which previously raised the halo over the popup.
            await Task.Delay(1100);
            if (!menu.IsOpen || !IsNativeWindowAbove(menuSource.Handle, new WindowInteropHelper(overlay).Handle)
                || !IsNativeWindowAbove(menuSource.Handle, new WindowInteropHelper(glow).Handle))
                throw new InvalidOperationException("The HUD or glow rose above the open context menu.");
            if (Math.Abs(glow.Surface.Phase - phase) < .01 || overlay.MusicGlow.Phase != glow.Surface.Phase)
                throw new InvalidOperationException("The inner and outer rainbow did not animate together while the menu was open.");
            ((MenuItem)menu.Items[0]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            menu.IsOpen = false;
            await Task.Delay(40);
            var opened = Current.Windows.OfType<SettingsWindow>().SingleOrDefault(window => window != existingSettings && window.IsVisible);
            if (opened is null) throw new InvalidOperationException("The context-menu Settings action did not open Settings.");
            opened.Close();
            return true;
        }
        finally
        {
            if (menu is not null) menu.IsOpen = false;
            overlay.Settings.GlowEnabled = enabled;
            overlay.Settings.GlowReactive = reactive;
            overlay.Settings.GlowIntensity = intensity;
            overlay.Settings.GlowStyle = effectStyle;
            if (!originallyVisible && overlay.IsVisible) overlay.ToggleVisibility();
            overlay.ApplySettings();
            overlay.SaveSettings();
        }
    }

    private static bool IsNativeWindowAbove(IntPtr upper, IntPtr lower)
    {
        for (var candidate = GetWindow(lower, 3); candidate != IntPtr.Zero; candidate = GetWindow(candidate, 3))
            if (candidate == upper) return true;
        return false;
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr GetWindow(IntPtr window, uint command);

    private static async Task<bool> CheckIndependentTelemetryAsync(OverlayWindow overlay)
    {
        // Hold the real media semaphore to reproduce a slow player/artwork provider without
        // adding test-only branches to either production sampler.
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var media = typeof(OverlayWindow).GetField("_media", flags)?.GetValue(overlay)
            ?? throw new InvalidOperationException("The media service was not found.");
        var gate = media.GetType().GetField("_gate", flags)?.GetValue(media) as SemaphoreSlim
            ?? throw new InvalidOperationException("The media semaphore was not found.");
        await gate.WaitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Task? pendingRefresh = null;
        var previous = overlay.TelemetrySnapshot;
        var updates = 0;
        void OnUpdated(object? sender, EventArgs args) => updates++;
        overlay.SnapshotsUpdated += OnUpdated;
        try
        {
            pendingRefresh = overlay.RefreshSnapshotsAsync();
            var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
            while (ReferenceEquals(previous, overlay.TelemetrySnapshot) && DateTimeOffset.UtcNow < deadline)
                await Task.Delay(40);
            if (ReferenceEquals(previous, overlay.TelemetrySnapshot) || updates == 0 || pendingRefresh.IsCompleted)
                throw new InvalidOperationException("Telemetry waited for the blocked media provider.");
            return true;
        }
        finally
        {
            overlay.SnapshotsUpdated -= OnUpdated;
            gate.Release();
            if (pendingRefresh is not null) await pendingRefresh.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);

    internal async Task<bool> RestartWithAdministratorAccessAsync(OverlayWindow overlay)
    {
        if (!Services.AppRestartService.CanRequestElevation) return false;
        overlay.SaveSettings();
        if (!Services.AppRestartService.RequestElevation()) return false;
        // The accepted child waits for our process to exit before acquiring the instance mutex.
        await overlay.StopAsync();
        foreach (Window window in Windows.Cast<Window>().ToArray()) window.Close();
        Shutdown(0);
        return true;
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogError(e.Exception);
        e.Handled = true;
        System.Windows.MessageBox.Show(L10n.Text("The action could not be completed. Details were saved to the GlimHUD log.", "Не удалось выполнить действие. Подробности сохранены в журнале GlimHUD."),
            "GlimHUD", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    internal static void LogError(Exception? error)
    {
        if (error is null) return;
        try
        {
            Directory.CreateDirectory(Services.SettingsStore.DirectoryPath);
            File.AppendAllText(Path.Combine(Services.SettingsStore.DirectoryPath, "error.log"),
                $"{DateTimeOffset.Now:O}\n{error}\n\n");
        }
        catch { }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _instance?.Dispose();
        base.OnExit(e);
    }
}
