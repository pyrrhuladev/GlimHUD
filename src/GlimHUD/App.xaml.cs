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
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Length >= 2 && e.Args[0] is "--render-preview" or "--smoke-test")
        {
            var outputPath = Path.GetFullPath(e.Args[1]);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                Environment.SetEnvironmentVariable("GLIMHUD_DATA_DIR", Path.Combine(Path.GetDirectoryName(outputPath)!, "diagnostic-settings"));
                if (e.Args[0] == "--render-preview") RenderPreview(outputPath, e.Args.ElementAtOrDefault(2), e.Args.ElementAtOrDefault(3));
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
        _instance = new Mutex(true, "Local\\GlimHUD.Desktop", out var created);
        if (!created) { Shutdown(); return; }
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => LogError(args.ExceptionObject as Exception);
        var overlay = new OverlayWindow();
        MainWindow = overlay;
        overlay.Show();
        if (e.Args.Contains("--settings", StringComparer.OrdinalIgnoreCase))
            overlay.OpenSettings();
    }

    private static void RenderPreview(string outputPath, string? variant = null, string? language = null)
    {
        var overlay = new OverlayWindow(false);
        overlay.Settings.Language = L10n.NormalizeLanguage(language);
        overlay.Settings.BackgroundOpacity = .30;
        overlay.Settings.Scale = 1;
        overlay.Settings.ShowMusic = variant != "compact";
        overlay.Settings.UseWindowsAccent = !Services.AccentThemeService.TryParseHex(variant, out _);
        if (!overlay.Settings.UseWindowsAccent) overlay.Settings.CustomAccentColor = variant!;
        overlay.ApplySettings();
        // Explicit design preview only; live mode always uses service measurements.
        overlay.TrackTitle.Text = "Night Drive";
        overlay.TrackArtist.Text = "Echosphere";
        overlay.FpsValue.Text = "144";
        overlay.CpuValue.Text = "32%";
        overlay.CpuTemperature.Text = "52°C";
        overlay.GpuValue.Text = "78%";
        overlay.GpuTemperature.Text = "68°C";
        overlay.RamValue.Text = "54%";
        overlay.RamMemory.Text = "8.6/16 GB";
        overlay.VolumeSlider.Value = 60;
        overlay.VolumeValue.Text = "60%";
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
        using (var drawing = scene.RenderOpen())
        {
            drawing.DrawRectangle(new LinearGradientBrush(Color.FromRgb(10, 24, 42), Color.FromRgb(71, 98, 119), 0), null, new Rect(0, 0, width, 150));
            drawing.DrawRectangle(new LinearGradientBrush(Color.FromRgb(155, 183, 199), Color.FromRgb(66, 100, 121), 0), null, new Rect(0, 150, width, 150));
            for (var x = 0; x < width; x += 80)
            {
                drawing.DrawRectangle(new SolidColorBrush(Color.FromArgb(45, 188, 225, 239)), null, new Rect(x, 0, 27, 300));
                drawing.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(35, 255, 255, 255)), 1), new Point(x, 0), new Point(x + 130, 300));
            }
            drawing.DrawImage(image, new Rect(32, 42, image.PixelWidth, image.PixelHeight));
            drawing.DrawImage(image, new Rect(32, 192, image.PixelWidth, image.PixelHeight));
        }
        var comparison = new RenderTargetBitmap(width, 300, 96, 96, PixelFormats.Pbgra32);
        comparison.Render(scene);
        SavePng(comparison, Path.ChangeExtension(outputPath, ".backgrounds.png"));

        var settings = new SettingsWindow(overlay);
        var settingsContent = (FrameworkElement)settings.Content;
        SavePng(RenderElement(settingsContent, new Size(640, 790)), Path.ChangeExtension(outputPath, ".settings.png"));
        settings.Close();
        overlay.Close();
    }

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

    private static void SavePng(BitmapSource bitmap, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private async Task RunSmokeTestAsync(string outputPath)
    {
        var overlay = new OverlayWindow(allowWheelInterception: false);
        MainWindow = overlay;
        try
        {
            overlay.Show();
            overlay.ToggleVisibility();
            await overlay.RefreshSnapshotsAsync();
            await Task.Delay(1500);
            await overlay.RefreshSnapshotsAsync();
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
                ClickThroughEnabled = clickThroughEnabled,
                InteractiveModeRestored = interactiveModeRestored,
                overlay.CurrentAccentHex,
                overlay.WindowsAccentHex,
                PlayerVisible = overlay.Settings.ShowMusic,
                ScrollMusicVolumeEnabled = overlay.Settings.ScrollMusicVolume,
                SettingsPersisted = File.Exists(Services.SettingsStore.FilePath),
                Localization = new { EnglishUi = englishUi, RussianUi = russianUi, EnglishRestored = englishRestored },
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

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);

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
