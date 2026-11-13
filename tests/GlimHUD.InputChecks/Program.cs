using System.Text.Json;
using System.Windows;
using GlimHUD.Native;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args is ["--hook-smoke", var path])
        {
            // Inactive for its entire lifetime: install/uninstall only, with no
            // synthetic input and no interception of the user's actual wheel.
            using var hook = new MusicWheelInterceptor(_ => throw new InvalidOperationException("Inactive hook callback must not run."));
            var installed = hook.IsInstalled;
            var error = hook.InstallationError;
            hook.Dispose();
            hook.Dispose();
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
            System.IO.File.WriteAllText(path, JsonSerializer.Serialize(new { Installed = installed, Error = error, Disposed = !hook.IsInstalled, Active = hook.Active }));
            return 0;
        }

        var passed = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            Console.WriteLine("PASS " + message);
            passed++;
        }
        static uint Data(int delta) => unchecked((uint)(ushort)(short)delta << 16);
        var deltas = new List<int>();
        using var interceptor = new MusicWheelInterceptor(delta => { deltas.Add(delta); return true; }, installHook: false)
        { Region = new Rect(-100, 20, 200, 60) };
        Check(!interceptor.TryHandleWheelMessage(0x020A, 0, 30, Data(120)), "Inactive player passes wheel through.");
        interceptor.Active = true;
        Check(interceptor.TryHandleWheelMessage(0x020A, -99, 30, Data(120)) && deltas[^1] == 120,
            "Positive wheel reaches a player on a negative-coordinate monitor.");
        Check(interceptor.TryHandleWheelMessage(0x020A, 0, 30, Data(-120)) && deltas[^1] == -120,
            "Signed negative high word is decoded correctly.");
        Check(interceptor.TryHandleWheelMessage(0x020A, 0, 30, Data(30)) && deltas[^1] == 30,
            "High-resolution wheel deltas are preserved.");
        var before = deltas.Count;
        Check(!interceptor.TryHandleWheelMessage(0x0200, 0, 30, Data(120))
            && !interceptor.TryHandleWheelMessage(0x0201, 0, 30, Data(120))
            && !interceptor.TryHandleWheelMessage(0x020E, 0, 30, Data(120)) && deltas.Count == before,
            "Movement, buttons and horizontal wheel never reach the callback.");
        Check(!interceptor.TryHandleWheelMessage(0x020A, 100, 30, Data(120))
            && !interceptor.TryHandleWheelMessage(0x020A, 0, 80, Data(120))
            && !interceptor.TryHandleWheelMessage(0x020A, -101, 30, Data(120)),
            "Outside and adjacent boundary events pass through.");
        Check(!interceptor.TryHandleWheelMessage(0x020A, 0, 30, Data(0)), "Zero-delta messages pass through.");
        interceptor.Region = new Rect(0, 0, double.PositiveInfinity, 20);
        Check(!interceptor.TryHandleWheelMessage(0x020A, 0, 1, Data(120)), "Invalid geometry disables interception.");
        using var declined = new MusicWheelInterceptor(_ => false, installHook: false) { Active = true, Region = new Rect(0, 0, 20, 20) };
        Check(!declined.TryHandleWheelMessage(0x020A, 1, 1, Data(120)), "Unavailable audio target can decline without swallowing the wheel.");
        using var failure = new MusicWheelInterceptor(_ => throw new InvalidOperationException(), installHook: false) { Active = true, Region = new Rect(0, 0, 20, 20) };
        Check(!failure.TryHandleWheelMessage(0x020A, 1, 1, Data(120)), "Callback failures cannot block user input.");
        interceptor.Region = new Rect(0, 0, 20, 20);
        interceptor.Dispose(); interceptor.Dispose(); interceptor.Active = true;
        Check(!interceptor.Active && !interceptor.IsInstalled && !interceptor.TryHandleWheelMessage(0x020A, 1, 1, Data(120)),
            "Repeated disposal and late activation cannot revive interception.");
        Console.WriteLine($"{passed} input checks passed.");
        return 0;
    }
}
