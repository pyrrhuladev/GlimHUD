using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using GlimHUD.Native;
using GlimHUD.Services;

internal static class Program
{
    private static int _passed;

    [STAThread]
    private static int Main()
    {
        try
        {
            var bindings = HotkeyBindings.All;
            var burst = 50d;
            for (var i = 0; i < 3; i++) burst = MusicVolumeStep.Next("A", "A", burst, 50, 3)!.Value;
            Check(burst == 59, "Held volume steps accumulate despite an old audio snapshot.");
            Check(MusicVolumeStep.Next("B", "A", 59, 20, 3) == 23,
                "Switching sources starts from the new app volume rather than another app's slider.");
            Check(MusicVolumeStep.Next("A", "A", 99, 50, 3) == 100
                && MusicVolumeStep.Next("A", "A", 1, 50, -3) == 0,
                "Volume steps stay within the selected app's zero-to-100 range.");
            Check(MusicVolumeStep.Next(null, "A", 50, 50, 3) is null
                && MusicVolumeStep.Next("A", "A", 50, 50, double.NaN) is null,
                "Missing targets and invalid changes cannot become volume commands.");
            Check(MusicVolumeStep.Next("A", "A", double.NaN, 30, 3) == 33,
                "An invalid displayed value falls back to the selected app snapshot.");
            Check(bindings.Count == 9, "The catalog contains existing and music shortcuts.");
            Check(bindings.Select(item => item.Id).Distinct().Count() == bindings.Count,
                "Native IDs are unique.");
            Check(bindings.Select(item => (item.VirtualKey, item.Modifiers & ~HotkeyBindings.NoRepeat)).Distinct().Count() == bindings.Count,
                "Registered key combinations are unique.");
            Check(bindings.All(item => (item.Modifiers & HotkeyBindings.ControlAlt) == HotkeyBindings.ControlAlt),
                "Every default shortcut requires both Control and Alt.");
            Check(bindings.Where(item => item.Id is 7 or 8).All(item => (item.Modifiers & HotkeyBindings.NoRepeat) == 0),
                "Volume keys allow hold-to-adjust repetition.");
            Check(bindings.Where(item => item.Id is not (7 or 8)).All(item => (item.Modifiers & HotkeyBindings.NoRepeat) != 0),
                "Toggle and track keys do not repeat while held.");
            Check(bindings.Single(item => item.Id == 4).VirtualKey == 0x20
                && bindings.Single(item => item.Id == 9).VirtualKey == 0x4D,
                "Space controls Play/Pause and M controls mute.");
            Check(bindings.All(item => item.Gesture.StartsWith("Ctrl + Alt + ", StringComparison.Ordinal)
                && item.LabelKey.StartsWith("Loc.Hotkey", StringComparison.Ordinal)),
                "UI gestures and localization keys are supplied by the registration catalog.");

            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var window = new Window { ShowActivated = false, ShowInTaskbar = false };
            var handle = new WindowInteropHelper(window).EnsureHandle();
            var foreground = GetForegroundWindow();
            var capture = GetCapture();
            var skipped = new MemoryRegistration([]);
            using (var diagnostic = new OverlayInterop(window,
                _ => throw new InvalidOperationException("A skipped diagnostic shortcut fired."), skipped, registerHotkeys: false))
            {
                SendMessageW(handle, 0x0312, new IntPtr(4), IntPtr.Zero);
                Check(skipped.Attempts.Count == 0 && diagnostic.FailedHotkeys.Count == 0,
                    "Diagnostic overlays do not register shortcuts or send media commands.");
            }
            var registration = new MemoryRegistration([5, 9]);
            var commands = new List<int>();
            var interop = new OverlayInterop(window, commands.Add, registration);
            Check(registration.Attempts.SequenceEqual(bindings), "Native registration tries every catalog entry.");
            Check(interop.FailedHotkeys.SequenceEqual(new[] { 5, 9 }),
                "Registration conflicts retain their exact IDs for localized UI reporting.");
            Check(!window.IsVisible, "Native routing checks do not display an application window.");

            SendMessageW(handle, 0x0312, new IntPtr(4), IntPtr.Zero);
            Check(commands.SequenceEqual(new[] { 4 }), "A registered Play/Pause ID routes even when the overlay is hidden.");
            interop.SetClickThrough(true);
            SendMessageW(handle, 0x0312, new IntPtr(7), IntPtr.Zero);
            SendMessageW(handle, 0x0312, new IntPtr(7), IntPtr.Zero);
            Check(commands.SequenceEqual(new[] { 4, 7, 7 }), "Volume repetition routes while the overlay passes clicks through.");
            SendMessageW(handle, 0x0312, new IntPtr(5), IntPtr.Zero);
            SendMessageW(handle, 0x0312, new IntPtr(9), IntPtr.Zero);
            SendMessageW(handle, 0x0312, new IntPtr(600), IntPtr.Zero);
            SendMessageW(handle, 0x0312, new IntPtr(long.MaxValue), IntPtr.Zero);
            Check(commands.Count == 3, "Conflicted, unknown and malformed IDs cannot invoke commands.");
            Check(GetForegroundWindow() == foreground && GetCapture() == capture && !window.IsVisible,
                "Hidden shortcut routing does not capture input or activate the window.");

            interop.Dispose();
            Check(registration.Released.Order().SequenceEqual(new[] { 1, 2, 3, 4, 6, 7, 8 }),
                "Disposal releases every successful registration and no conflicted entry.");
            interop.Dispose();
            Check(registration.Released.Count == 7, "Disposal is idempotent.");
            SendMessageW(handle, 0x0312, new IntPtr(4), IntPtr.Zero);
            Check(commands.Count == 3, "Disposed routing cannot invoke music commands.");
            window.Close();

            var closingWindow = new Window();
            _ = new WindowInteropHelper(closingWindow).EnsureHandle();
            var closingRegistration = new MemoryRegistration([]);
            var closingInterop = new OverlayInterop(closingWindow, _ => throw new InvalidOperationException("Unexpected command."), closingRegistration);
            closingWindow.Close();
            closingInterop.Dispose();
            Check(closingRegistration.Released.Count == 9, "A closed native source still releases all hotkey registrations safely.");
            app.Shutdown();
            Console.WriteLine($"{_passed} hotkey checks passed; no real global shortcuts or media commands were registered.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        _passed++;
    }

    private sealed class MemoryRegistration(IEnumerable<int> failures) : IHotkeyRegistration
    {
        private readonly HashSet<int> _failures = failures.ToHashSet();
        internal List<HotkeyBinding> Attempts { get; } = [];
        internal List<int> Released { get; } = [];
        public bool Register(IntPtr window, HotkeyBinding binding)
        {
            Attempts.Add(binding);
            return !_failures.Contains(binding.Id);
        }
        public void Unregister(IntPtr window, int id) => Released.Add(id);
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr SendMessageW(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr GetCapture();
}
