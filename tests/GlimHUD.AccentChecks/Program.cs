using GlimHUD.Services;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        var checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            Console.WriteLine("PASS " + message);
            checks++;
        }

        try
        {
            var app = new System.Windows.Application();
            using var theme = new AccentThemeService();
            Check(AccentThemeService.TryParseHex(" #aB10fF ", out var parsed)
                  && parsed == Color.FromRgb(171, 16, 255), "Custom RGB accepts case and harmless surrounding whitespace.");
            foreach (var invalid in new[] { "#123", "123456", "#12345678", "#GG0000", "#12 456", "" })
                Check(!AccentThemeService.TryParseHex(invalid, out _), "Invalid or alpha-bearing RGB is rejected: " + invalid);

            theme.Apply(true, "#FF0000");
            Check(theme.CurrentColor == theme.SystemColor && theme.CurrentColor.A == 255,
                "System mode uses the actual opaque Windows accent instead of the stored custom color.");
            theme.Apply(false, "#C84090");
            Check(theme.CurrentHex == "#C84090", "Custom mode keeps the exact selected RGB.");
            Check(((SolidColorBrush)app.Resources["AccentBrush"]).Color == theme.CurrentColor,
                "Shared app resources receive the selected color.");
            Check(((SolidColorBrush)app.Resources["AccentPanelBorderBrush"]).Color.A == 20
                  && ((SolidColorBrush)app.Resources["AccentBorderBrush"]).Color.A == 68,
                "Changing accent preserves subtle overlay and control outline alpha.");

            var label = new TextBlock();
            label.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
            var window = new Window { Content = label, Width = 100, Height = 40, Opacity = 0,
                ShowInTaskbar = false, ShowActivated = false, WindowStyle = WindowStyle.None };
            window.Show();
            window.Hide();
            Check(((SolidColorBrush)label.Foreground).Color == Color.FromRgb(200, 64, 144),
                "A real DynamicResource consumer resolves the app's custom accent.");
            theme.Apply(false, "#00CC66");
            Check(((SolidColorBrush)label.Foreground).Color == Color.FromRgb(0, 204, 102),
                "An existing DynamicResource consumer updates without recreating the window.");
            theme.Apply(false, "#000000");
            Check(((SolidColorBrush)app.Resources["AccentOnBrush"]).Color == Colors.White,
                "Dark accent receives a readable white checkmark.");
            Check(((SolidColorBrush)app.Resources["AccentTextBrush"]).Color != Colors.Black,
                "Dark custom accents keep FPS and labels readable on the dark panel.");
            theme.Apply(false, "#FFFFFF");
            Check(((SolidColorBrush)app.Resources["AccentOnBrush"]).Color == Color.FromRgb(7, 18, 29),
                "Light accent receives a readable dark checkmark.");
            theme.Apply(false, "bad input");
            Check(theme.CurrentColor == AccentThemeService.DefaultColor, "An invalid persisted custom color falls back safely.");
            var previous = theme.CurrentColor;
            theme.Dispose();
            theme.Apply(false, "#FF0000");
            Check(theme.CurrentColor == previous, "A disposed observer cannot change the app's palette.");
            window.Close();
            Console.WriteLine($"{checks} accent checks passed; Windows accent {theme.SystemHex}.");
            app.Shutdown();
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("FAIL " + error);
            return 1;
        }
    }
}
