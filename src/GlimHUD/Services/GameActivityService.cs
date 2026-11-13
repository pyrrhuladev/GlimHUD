using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using GlimHUD.Localization;

namespace GlimHUD.Services;

public sealed record GameActivitySnapshot(
    bool IsGameActive,
    bool IsOverlayFocused,
    string? ProcessName,
    string? ExecutablePath,
    string Status);

/// <summary>
/// Samples the current foreground window independently of the FPS collector.
/// No game memory, rendering hooks or elevated process access are used.
/// </summary>
public sealed class GameActivityService : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<int, ProcessIdentity> _identities = new();
    private bool _disposed;
    private string? _lastExternalExecutablePath;

    /// <summary>The most recent non-GlimHUD executable, for an explicit game selection.</summary>
    public string? LastExternalExecutablePath
    {
        get { lock (_gate) return _lastExternalExecutablePath; }
    }

    public GameActivitySnapshot GetSnapshot(IReadOnlyCollection<string>? configuredExecutablePaths = null)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var foreground = GetForegroundWindow();
            if (foreground == IntPtr.Zero || GetWindowThreadProcessId(foreground, out var processId) == 0 || processId == 0)
                return new(false, false, null, null, L10n.Text("No active game window", "Нет активного окна игры"));

            if (processId == Environment.ProcessId)
                return new(false, true, "GlimHUD", Environment.ProcessPath, L10n.Text("GlimHUD controls are open", "Открыто управление GlimHUD"));

            var identity = ReadProcessIdentity(checked((int)processId));
            if (identity is null)
                return new(false, false, null, null, L10n.Text("Unable to identify the active app", "Активное приложение недоступно для определения"));

            _lastExternalExecutablePath = identity.ExecutablePath;
            var result = GameApplicationClassifier.Classify(identity.ExecutablePath, configuredExecutablePaths);
            return new(result.IsGame, false, identity.ProcessName, identity.ExecutablePath, result.Status);
        }
    }

    /// <summary>Returns null when GlimHUD itself currently owns the foreground window.</summary>
    public string? GetForegroundExecutablePath()
    {
        var current = GetSnapshot();
        return current.IsOverlayFocused ? null : current.ExecutablePath;
    }

    private ProcessIdentity? ReadProcessIdentity(int processId)
    {
        using var process = OpenProcess(0x1000, false, processId); // PROCESS_QUERY_LIMITED_INFORMATION
        if (process.IsInvalid) return null;
        var hasCreationTime = GetProcessTimes(process, out var creation, out _, out _, out _);
        var creationStamp = ((ulong)creation.High << 32) | creation.Low;
        if (hasCreationTime && _identities.TryGetValue(processId, out var cached) && cached.CreationStamp == creationStamp)
            return cached;

        var buffer = new StringBuilder(32768);
        var length = buffer.Capacity;
        if (!QueryFullProcessImageName(process, 0, buffer, ref length)) return null;
        var path = GameApplicationClassifier.NormalizeExecutablePath(buffer.ToString());
        if (path is null) return null;
        var result = new ProcessIdentity(creationStamp, path, Path.GetFileNameWithoutExtension(path));
        if (hasCreationTime)
        {
            // Query creation time before cache lookup: PID reuse cannot inherit a game's identity.
            if (_identities.Count >= 64 && !_identities.ContainsKey(processId))
                _identities.Clear();
            _identities[processId] = result;
        }
        return result;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _identities.Clear();
            _lastExternalExecutablePath = null;
        }
    }

    private sealed record ProcessIdentity(ulong CreationStamp, string ExecutablePath, string ProcessName);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeFileTime { public uint Low; public uint High; }
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int processId);
    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process, int flags, StringBuilder path, ref int size);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessTimes(SafeProcessHandle process, out NativeFileTime creation,
        out NativeFileTime exit, out NativeFileTime kernel, out NativeFileTime user);
}

internal sealed record GameApplicationClassification(bool IsGame, string Status);

/// <summary>
/// Conservative install-path evidence plus user-selected executables. Window size or
/// fullscreen alone never identifies an application as a game.
/// </summary>
internal static class GameApplicationClassifier
{
    private static readonly HashSet<string> NonGameProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "GlimHUD", "explorer", "dwm", "ShellExperienceHost", "StartMenuExperienceHost", "SearchHost",
        "ApplicationFrameHost", "SystemSettings", "Taskmgr", "LockApp", "TextInputHost",
        "steam", "steamwebhelper", "EpicGamesLauncher", "EpicWebHelper", "EADesktop", "EALauncher",
        "EABackgroundService", "Battle.net", "Agent", "UbisoftConnect", "upc", "GalaxyClient",
        "RiotClientServices", "RiotClientUx", "RiotClientUxRender", "Playnite.DesktopApp", "Playnite.FullscreenApp",
        "wallpaper32", "wallpaper64", "losslessscaling", "displayfusion", "sharex", "obs64", "obs32",
        "rtss", "MSIAfterburner", "Unity", "UnityHub", "UnrealEditor", "blender", "krita"
    };
    private static readonly HashSet<string> Browsers = new(StringComparer.OrdinalIgnoreCase)
    { "chrome", "msedge", "firefox", "browser", "yandex", "brave", "opera", "vivaldi", "iexplore" };

    public static GameApplicationClassification Classify(string? executablePath,
        IReadOnlyCollection<string>? configuredExecutablePaths = null)
    {
        var path = NormalizeExecutablePath(executablePath);
        if (path is null) return new(false, L10n.Text("Unable to identify the executable", "Не удалось определить исполняемый файл"));
        var processName = Path.GetFileNameWithoutExtension(path);
        if (NonGameProcesses.Contains(processName)) return new(false, L10n.Text("The active app is not a game", "Активное приложение не является игрой"));
        if (configuredExecutablePaths?.Any(item => string.Equals(path, NormalizeExecutablePath(item), StringComparison.OrdinalIgnoreCase)) == true)
            return new(true, L10n.Text("Game from your custom list", "Игра из выбранного списка"));
        if (Browsers.Contains(processName)) return new(false, L10n.Text("A browser is active", "Активен браузер"));

        var parts = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Where(part => part.Length > 0).ToArray();
        if (parts.Any(part => part.Equals("_CommonRedist", StringComparison.OrdinalIgnoreCase)))
            return new(false, L10n.Text("A helper process is active", "Открыт вспомогательный процесс"));
        for (var index = 0; index < parts.Length - 2; index++)
        {
            if (parts[index].Equals("steamapps", StringComparison.OrdinalIgnoreCase)
                && parts[index + 1].Equals("common", StringComparison.OrdinalIgnoreCase) && index + 3 < parts.Length)
                return new(true, L10n.Text("Game from the Steam library", "Игра из библиотеки Steam"));
            if ((parts[index].Equals("Epic Games", StringComparison.OrdinalIgnoreCase)
                 || parts[index].Equals("EpicGames", StringComparison.OrdinalIgnoreCase))
                && !parts[index + 1].Equals("Launcher", StringComparison.OrdinalIgnoreCase)
                && !parts[index + 1].StartsWith("UE_", StringComparison.OrdinalIgnoreCase))
                return new(true, L10n.Text("Game from the Epic Games library", "Игра из библиотеки Epic Games"));
            if (parts[index].Equals("GOG Games", StringComparison.OrdinalIgnoreCase))
                return new(true, L10n.Text("Game from the GOG library", "Игра из библиотеки GOG"));
            if (parts[index].Equals("XboxGames", StringComparison.OrdinalIgnoreCase))
                return new(true, L10n.Text("Game from the Xbox library", "Игра из библиотеки Xbox"));
        }
        return new(false, L10n.Text("Game not recognized; add its EXE in Settings", "Игра не распознана; её EXE можно добавить в настройках"));
    }

    internal static string? NormalizeExecutablePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        try
        {
            var path = value.Trim().Trim('"');
            if (path.StartsWith("\\\\?\\", StringComparison.Ordinal)) path = path[4..];
            if (!Path.IsPathFullyQualified(path) || !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return null;
            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }
}
