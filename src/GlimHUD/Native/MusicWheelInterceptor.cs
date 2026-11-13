using System.Runtime.InteropServices;
using System.Windows;

namespace GlimHUD.Native;

/// <summary>
/// Receives wheel messages over the visible player even when its window passes
/// clicks through. The hook runs in this process, on its installing UI thread;
/// its callback must only perform quick local work and must never wait on audio IO.
/// </summary>
internal sealed class MusicWheelInterceptor : IDisposable
{
    private const int WhMouseLowLevel = 14;
    private const uint WmMouseWheel = 0x020A;
    private readonly Func<int, bool> _onWheel;
    // Keep the managed delegate alive for the complete lifetime of the native hook.
    private readonly LowLevelMouseProc _callback;
    private IntPtr _hook;
    private RegionSnapshot _region = new(Rect.Empty);
    private int _active;
    private int _disposed;

    public MusicWheelInterceptor(Func<int, bool> onWheel) : this(onWheel, installHook: true) { }

    internal MusicWheelInterceptor(Func<int, bool> onWheel, bool installHook)
    {
        ArgumentNullException.ThrowIfNull(onWheel);
        _onWheel = onWheel;
        _callback = HookCallback;
        if (!installHook) return;
        try
        {
            _hook = SetWindowsHookEx(WhMouseLowLevel, _callback, GetModuleHandle(null), 0);
            if (_hook == IntPtr.Zero) InstallationError = Marshal.GetLastPInvokeError();
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException
                                      or BadImageFormatException or PlatformNotSupportedException)
        {
            // Restricted or unsupported hosts retain the ordinary WPF wheel fallback.
            InstallationError = 50; // ERROR_NOT_SUPPORTED
        }
    }

    /// <summary>Player bounds in per-monitor-aware screen/device pixels.</summary>
    public Rect Region
    {
        get => Volatile.Read(ref _region).Value;
        set
        {
            var valid = !value.IsEmpty && value.Width > 0 && value.Height > 0
                && double.IsFinite(value.X) && double.IsFinite(value.Y)
                && double.IsFinite(value.Right) && double.IsFinite(value.Bottom);
            Volatile.Write(ref _region, new RegionSnapshot(valid ? value : Rect.Empty));
        }
    }

    /// <summary>Enable only while the player is visible and has a writable audio target.</summary>
    public bool Active
    {
        get => Volatile.Read(ref _active) != 0 && Volatile.Read(ref _disposed) == 0;
        set => Volatile.Write(ref _active, value ? 1 : 0);
    }

    public bool IsInstalled => Volatile.Read(ref _hook) != IntPtr.Zero && Volatile.Read(ref _disposed) == 0;
    public int InstallationError { get; private set; }

    private IntPtr HookCallback(int code, IntPtr message, IntPtr data)
    {
        // Do not marshal or retain mouse movement, buttons, horizontal wheels, or
        // any other input. Unhandled messages continue through the native chain.
        if (code >= 0 && message.ToInt64() == WmMouseWheel && Active)
        {
            try
            {
                var mouse = Marshal.PtrToStructure<MouseHookData>(data);
                if (TryHandleWheelMessage(WmMouseWheel, mouse.Point.X, mouse.Point.Y, mouse.MouseData))
                    return new IntPtr(1);
            }
            catch
            {
                // A managed callback/teardown failure must never block user input.
            }
        }
        return CallNextHookEx(IntPtr.Zero, code, message, data);
    }

    /// <summary>Shared hit-testing/dispatch path; tests do not inject system input.</summary>
    internal bool TryHandleWheelMessage(uint message, int screenX, int screenY, uint mouseData)
    {
        if (message != WmMouseWheel || !Active) return false;
        var region = Volatile.Read(ref _region).Value;
        // Windows rectangles are half-open: touching the right/bottom edge does
        // not consume a wheel meant for the adjacent game area.
        if (region.IsEmpty || screenX < region.Left || screenX >= region.Right
            || screenY < region.Top || screenY >= region.Bottom) return false;
        var delta = unchecked((short)(mouseData >> 16));
        if (delta == 0) return false;
        try { return _onWheel(delta); }
        catch { return false; }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Volatile.Write(ref _active, 0);
        var handle = Interlocked.Exchange(ref _hook, IntPtr.Zero);
        if (handle != IntPtr.Zero)
        {
            try { UnhookWindowsHookEx(handle); }
            catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException) { }
        }
        GC.KeepAlive(_callback);
    }

    private sealed record RegionSnapshot(Rect Value);

    [StructLayout(LayoutKind.Sequential)]
    private struct MousePoint { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseHookData
    {
        public MousePoint Point;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr LowLevelMouseProc(int code, IntPtr message, IntPtr data);

    [DllImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int hookId, LowLevelMouseProc callback, IntPtr module, uint threadId);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? name);
}
