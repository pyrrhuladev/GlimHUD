using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using GlimHUD.Services.Fps;
using GlimHUD.Localization;

namespace GlimHUD.Services;

public sealed record FpsSnapshot(
    double? FramesPerSecond,
    int? ProcessId,
    string ProcessName,
    string Status,
    bool IsAvailable,
    bool IsRunning,
    bool IsPinned)
{
    /// <summary>The last capture attempt failed because Windows denied ETW access.</summary>
    public bool RequiresElevation { get; init; }
    public int ObservedProcessCount { get; init; }
    public long ParsedFrameCount { get; init; }
    public long RejectedFrameRowCount { get; init; }
}

public sealed record FpsProcessTarget(int ProcessId, string ProcessName, string WindowTitle)
{
    public string DisplayName => $"{ProcessName} — {WindowTitle}";
}

/// <summary>
/// Collects Presented FPS from PresentMon's ETW console stream. No DLL is injected
/// into a target process. The UI's rendering frequency is never used as a metric.
/// </summary>
public sealed class PresentMonService : IDisposable
{
    private static readonly HashSet<string> IgnoredProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "GlimHUD", "dwm", "explorer", "ShellExperienceHost", "StartMenuExperienceHost",
        "SearchHost", "SearchApp", "TextInputHost", "ApplicationFrameHost", "LockApp",
        "Taskmgr", "SystemSettings", "steam", "steamwebhelper", "EpicGamesLauncher",
        "EpicWebHelper", "Battle.net", "Agent", "EADesktop", "EABackgroundService"
    };

    private readonly object _gate = new();
    private readonly string? _executablePath;
    private readonly string _sessionName = $"GlimHUD-{Environment.ProcessId}-{Guid.NewGuid():N}";
    private readonly PresentMonFrameAccumulator _frames = new();
    private CancellationTokenSource? _cancellation;
    private Process? _capture;
    private Task? _captureTask;
    private int? _pinnedProcessId;
    private int? _automaticProcessId;
    private string _targetName = string.Empty;
    private Func<string> _status = () => L10n.Text("Waiting to start", "Ожидание запуска");
    private string? _lastError;
    private bool _etwAccessDenied;
    private bool _running;
    private bool _disposed;
    private readonly Dictionary<int, long> _observedProcesses = [];
    private long _captureStarted;
    private long _parsedFrameCount;
    private long _rejectedFrameRowCount;
    private bool _headerSeen;
    private bool _schemaSupported;

    public PresentMonService(string? executablePath = null)
    {
        _executablePath = ResolveExecutable(executablePath);
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_gate)
        {
            if (_captureTask is { IsCompleted: false }) return;
            if (_executablePath is null)
            {
                SetStatus("PresentMon is not installed — run scripts/Get-PresentMon.ps1", "PresentMon не установлен — запустите scripts/Get-PresentMon.ps1");
                return;
            }
            _cancellation?.Dispose();
            _cancellation = new CancellationTokenSource();
            SetStatus("Starting the frame counter…", "Запуск счётчика кадров…");
            _lastError = null;
            _etwAccessDenied = false;
            _frames.Clear();
            _observedProcesses.Clear();
            _captureStarted = 0;
            _parsedFrameCount = _rejectedFrameRowCount = 0;
            _headerSeen = _schemaSupported = false;
            var cancellationToken = _cancellation.Token;
            _captureTask = Task.Run(() => CaptureAsync(cancellationToken));
        }
    }

    /// <summary>Pass null for foreground mode, or an explicit PID to keep one game selected.</summary>
    public void SetTargetProcess(int? processId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (processId <= 0) throw new ArgumentOutOfRangeException(nameof(processId));
        if (processId == Environment.ProcessId)
            throw new ArgumentException(L10n.Text("The overlay itself cannot be selected as the game FPS source.", "Сам оверлей нельзя выбрать источником игрового FPS."), nameof(processId));
        lock (_gate)
        {
            _pinnedProcessId = processId;
            if (processId.HasValue)
                _targetName = TryGetProcessName(processId.Value) ?? $"PID {processId.Value}";
        }
    }

    public FpsSnapshot GetSnapshot()
    {
        lock (_gate)
        {
            if (!_pinnedProcessId.HasValue) UpdateForegroundTarget();
            var processId = _pinnedProcessId ?? _automaticProcessId;
            var name = processId.HasValue
                ? TryGetProcessName(processId.Value)
                : null;
            // A collector failure is actionable even when a previously selected game
            // has exited. Do not mask permission errors behind stale target state.
            if (!_running)
                return new(null, processId, name ?? _targetName, _status(), _executablePath is not null, false, _pinnedProcessId.HasValue)
                { RequiresElevation = _etwAccessDenied };
            if (processId.HasValue && name is null)
            {
                if (!_pinnedProcessId.HasValue) _automaticProcessId = null;
                return new(null, processId, _targetName, L10n.Text("The selected process has exited", "Выбранный процесс завершён"), _executablePath is not null, _running, _pinnedProcessId.HasValue);
            }

            if (name is not null) _targetName = name;
            if (!processId.HasValue)
                return new(null, null, string.Empty, L10n.Text("Open a game or select a process in Settings", "Откройте игру или выберите процесс в настройках"), true, true, false);

            var fps = _frames.GetFramesPerSecond(processId.Value, Stopwatch.GetTimestamp());
            return new(fps, processId, _targetName,
                fps.HasValue ? L10n.Text("{0} · Presented FPS", "{0} · FPS приложения", _targetName) : GetWaitingStatus(processId.Value),
                true, true, _pinnedProcessId.HasValue)
            {
                ObservedProcessCount = _observedProcesses.Count,
                ParsedFrameCount = _parsedFrameCount,
                RejectedFrameRowCount = _rejectedFrameRowCount
            };
        }
    }

    public IReadOnlyList<FpsProcessTarget> GetAvailableTargets()
    {
        var targets = new List<FpsProcessTarget>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (process.Id == Environment.ProcessId || IgnoredProcesses.Contains(process.ProcessName)) continue;
                    if (process.MainWindowHandle == IntPtr.Zero || string.IsNullOrWhiteSpace(process.MainWindowTitle)) continue;
                    targets.Add(new(process.Id, process.ProcessName, process.MainWindowTitle));
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
                {
                    // A process may exit or deny inspection while this list is being built.
                }
            }
        }
        return targets.OrderBy(target => target.ProcessName, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    private async Task CaptureAsync(CancellationToken cancellationToken)
    {
        Process? process = null;
        try
        {
            var startInfo = new ProcessStartInfo(_executablePath!)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(_executablePath!)!
            };
            foreach (var argument in GetCaptureArguments(_sessionName)) startInfo.ArgumentList.Add(argument);

            process = new Process { StartInfo = startInfo };
            if (!process.Start()) throw new InvalidOperationException(L10n.Text("PresentMon did not start.", "PresentMon не запустился."));
            lock (_gate)
            {
                _capture = process;
                _running = true;
                _captureStarted = Stopwatch.GetTimestamp();
                SetStatus("Waiting for frames", "Ожидание кадров");
            }
            var errorsTask = ReadErrorsAsync(process, cancellationToken);
            var parser = new PresentMonCsvParser();
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null) break;
                var parsed = parser.TryParse(line, out var sample);
                lock (_gate)
                {
                    _headerSeen = parser.HasHeader;
                    _schemaSupported = parser.SupportsSchema;
                    if (!parsed && parser.SupportsSchema && !parser.LastLineWasHeader && line.Contains(','))
                        _rejectedFrameRowCount++;
                }
                if (parsed)
                {
                    if (sample.ProcessId == Environment.ProcessId) continue;
                    lock (_gate)
                    {
                        var received = Stopwatch.GetTimestamp();
                        _parsedFrameCount++;
                        if (!_observedProcesses.ContainsKey(sample.ProcessId) && _observedProcesses.Count >= 64)
                            _observedProcesses.Remove(_observedProcesses.MinBy(item => item.Value).Key);
                        _observedProcesses[sample.ProcessId] = received;
                        // The collector remains alive across target switches, but
                        // retain frame windows only for the selected application.
                        if (sample.ProcessId == (_pinnedProcessId ?? _automaticProcessId))
                            _frames.Add(sample, received);
                    }
                }
                else if (line.Contains("error", StringComparison.OrdinalIgnoreCase) || line.Contains("privilege", StringComparison.OrdinalIgnoreCase))
                {
                    RecordDiagnostic(line);
                }
            }
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await errorsTask.ConfigureAwait(false);
            lock (_gate)
            {
                if (process.ExitCode == 5 || _etwAccessDenied)
                {
                    _etwAccessDenied = true;
                    SetStatus("Windows denied FPS capture access. Use Restart as administrator in Settings, then return to your game.", "Windows запретила захват FPS. В настройках выберите перезапуск от администратора, затем вернитесь в игру.");
                }
                else if (process.ExitCode == 0)
                    SetStatus("Frame capture stopped", "Сбор кадров остановлен");
                else
                    SetStatus("PresentMon exited (code {0}). {1}", "PresentMon завершился (код {0}). {1}", process.ExitCode, _lastError ?? string.Empty);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            lock (_gate)
            {
                _etwAccessDenied |= ex is UnauthorizedAccessException || ex is System.ComponentModel.Win32Exception { NativeErrorCode: 5 };
                if (_etwAccessDenied)
                    SetStatus("Windows denied FPS capture access. Use Restart as administrator in Settings, then return to your game.", "Windows запретила захват FPS. В настройках выберите перезапуск от администратора, затем вернитесь в игру.");
                else
                    SetStatus("FPS unavailable: {0}", "FPS недоступен: {0}", ex.Message);
            }
        }
        finally
        {
            if (process is not null)
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
                process.Dispose();
            }
            lock (_gate)
            {
                _running = false;
                _capture = null;
                _frames.Clear();
            }
            StopOwnEtwSession();
        }
    }

    internal static IReadOnlyList<string> GetCaptureArguments(string sessionName) =>
    [
        "--output_stdout", "--no_console_stats", "--v1_metrics",
        "--no_track_gpu", "--no_track_input",
        // Keep display/kernel present tracking enabled. Disabling it removes
        // Flip/Present events needed by non-DXGI runtimes such as native Vulkan.
        "--session_name", sessionName,
        "--exclude", "GlimHUD.exe", "--exclude", "dwm.exe"
    ];

    private string GetWaitingStatus(int processId)
    {
        if (_headerSeen && !_schemaSupported)
            return L10n.Text("The frame collector returned an unsupported CSV schema. Retry FPS capture.",
                "Счётчик кадров вернул неподдерживаемый формат CSV. Повторите захват FPS.");
        var now = Stopwatch.GetTimestamp();
        var warmedUp = _captureStarted != 0 && (double)(now - _captureStarted) / Stopwatch.Frequency >= 5;
        if (warmedUp && _observedProcesses.Any(item => item.Key != processId &&
            (double)(now - item.Value) / Stopwatch.Frequency <= 3))
            return L10n.Text("{0}: no frame events for this process; other processes are producing frames. Check the FPS source in Settings or retry capture.",
                "{0}: от этого процесса кадры не поступают, хотя другие процессы их передают. Проверьте источник FPS в настройках или повторите захват.", _targetName);
        if (warmedUp && _parsedFrameCount == 0)
            return L10n.Text("{0}: capture is running but no frame events have arrived. Retry FPS capture in Settings, then return to the game.",
                "{0}: захват запущен, но события кадров не поступают. Повторите захват FPS в настройках и вернитесь в игру.", _targetName);
        return L10n.Text("{0} · waiting for frames", "{0} · ожидание кадров", _targetName);
    }

    // Store the templates so a language change also updates a stopped collector's status.
    private void SetStatus(string english, string russian, params object[] arguments)
        => _status = () => L10n.Text(english, russian, arguments);

    private async Task ReadErrorsAsync(Process process, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await process.StandardError.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null) break;
                if (!string.IsNullOrWhiteSpace(line)) RecordDiagnostic(line);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (IOException) { }
    }

    private void RecordDiagnostic(string line)
    {
        lock (_gate)
        {
            // PresentMon follows its root cause with several help/readme lines.
            // Preserve permission classification regardless of later diagnostics.
            _etwAccessDenied |= line.Contains("access denied", StringComparison.OrdinalIgnoreCase)
                || line.Contains("access is denied", StringComparison.OrdinalIgnoreCase)
                || line.Contains("privilege", StringComparison.OrdinalIgnoreCase)
                || line.Contains("0x80070005", StringComparison.OrdinalIgnoreCase)
                || line.Contains("ERROR_ACCESS_DENIED", StringComparison.OrdinalIgnoreCase);
            var message = line.Trim();
            if (message.Length > 512) message = message[..512];
            _lastError = string.IsNullOrEmpty(_lastError) ? message : $"{_lastError}\n{message}";
            if (_lastError.Length > 1536) _lastError = _lastError[^1536..];
        }
    }

    private void UpdateForegroundTarget()
    {
        var window = GetForegroundWindow();
        if (window == IntPtr.Zero) return;
        GetWindowThreadProcessId(window, out var processId);
        if (processId == 0 || processId == Environment.ProcessId) return;
        var name = TryGetProcessName((int)processId);
        if (name is null || IgnoredProcesses.Contains(name)) return;
        _automaticProcessId = (int)processId;
        _targetName = name;
    }

    private static string? TryGetProcessName(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.HasExited ? null : process.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return null; }
    }

    private static string? ResolveExecutable(string? requestedPath)
    {
        if (!string.IsNullOrWhiteSpace(requestedPath))
            return File.Exists(requestedPath) ? Path.GetFullPath(requestedPath) : null;
        var baseDirectory = AppContext.BaseDirectory;
        foreach (var folder in new[]
        {
            Path.Combine(baseDirectory, "tools", "PresentMon"),
            Path.Combine(baseDirectory, "PresentMon"),
            Path.GetFullPath(Path.Combine(baseDirectory, "..", "..", "..", "..", "..", "tools", "PresentMon")),
            Path.Combine(Environment.CurrentDirectory, "tools", "PresentMon")
        })
        {
            if (!Directory.Exists(folder)) continue;
            var candidates = Directory.GetFiles(folder, "PresentMon*.exe")
                .Where(path => !Path.GetFileName(path).Contains("service", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(Path.GetFileName, StringComparer.OrdinalIgnoreCase);
            var found = candidates.FirstOrDefault();
            if (found is not null) return found;
        }
        return null;
    }

    private void StopOwnEtwSession()
    {
        if (_executablePath is null) return;
        try
        {
            var info = new ProcessStartInfo(_executablePath) { UseShellExecute = false, CreateNoWindow = true };
            info.ArgumentList.Add("--session_name");
            info.ArgumentList.Add(_sessionName);
            info.ArgumentList.Add("--terminate_existing_session");
            using var cleanup = Process.Start(info);
            if (cleanup is not null && !cleanup.WaitForExit(1500)) cleanup.Kill();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cancellation?.Cancel();
        Process? capture;
        lock (_gate) capture = _capture;
        try { if (capture is not null && !capture.HasExited) capture.Kill(entireProcessTree: true); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        // CaptureAsync owns and disposes the process and closes only our named ETW session.
        try { _captureTask?.Wait(TimeSpan.FromSeconds(2)); }
        catch (AggregateException) { /* Shutdown must not surface a cancelled reader as an application crash. */ }
        _cancellation?.Dispose();
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
}
