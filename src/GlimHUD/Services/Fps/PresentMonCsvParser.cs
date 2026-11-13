using System.Globalization;
using System.Text;

namespace GlimHUD.Services.Fps;

internal readonly record struct PresentMonSample(int ProcessId, string SwapChain, double TimeMilliseconds, double FrameMilliseconds);

/// <summary>Header-driven reader for v1_metrics CSV; understands quoted application names.</summary>
internal sealed class PresentMonCsvParser
{
    private int _processIndex = -1;
    private int _swapChainIndex = -1;
    private int _timeIndex = -1;
    private int _frameTimeIndex = -1;
    private bool _timeIsSeconds;

    public bool TryParse(string line, out PresentMonSample sample)
    {
        sample = default;
        if (string.IsNullOrWhiteSpace(line)) return false;
        var values = Split(line);
        var processHeader = Array.FindIndex(values, value => value.Equals("ProcessID", StringComparison.OrdinalIgnoreCase));
        if (processHeader >= 0)
        {
            _processIndex = processHeader;
            _swapChainIndex = Array.FindIndex(values, value => value.Equals("SwapChainAddress", StringComparison.OrdinalIgnoreCase));
            _frameTimeIndex = Array.FindIndex(values, value => value.Equals("MsBetweenPresents", StringComparison.OrdinalIgnoreCase));
            _timeIndex = Array.FindIndex(values, value => value.Equals("TimeInSeconds", StringComparison.OrdinalIgnoreCase));
            _timeIsSeconds = _timeIndex >= 0;
            if (_timeIndex < 0)
                _timeIndex = Array.FindIndex(values, value => value.Equals("CPUStartTime", StringComparison.OrdinalIgnoreCase));
            return false;
        }
        var largestIndex = Math.Max(Math.Max(_processIndex, _swapChainIndex), Math.Max(_timeIndex, _frameTimeIndex));
        if (_processIndex < 0 || _timeIndex < 0 || _frameTimeIndex < 0 || largestIndex >= values.Length) return false;
        if (!int.TryParse(values[_processIndex], NumberStyles.Integer, CultureInfo.InvariantCulture, out var processId)) return false;
        if (!double.TryParse(values[_timeIndex], NumberStyles.Float, CultureInfo.InvariantCulture, out var time)) return false;
        if (!double.TryParse(values[_frameTimeIndex], NumberStyles.Float, CultureInfo.InvariantCulture, out var frameTime)) return false;
        if (!double.IsFinite(time) || !double.IsFinite(frameTime) || frameTime <= 0 || frameTime > 10000) return false;
        sample = new(processId, _swapChainIndex >= 0 ? values[_swapChainIndex] : "default", _timeIsSeconds ? time * 1000 : time, frameTime);
        return true;
    }

    private static string[] Split(string line)
    {
        var values = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var character = line[i];
            if (character == '"')
            {
                if (quoted && i + 1 < line.Length && line[i + 1] == '"') { current.Append('"'); i++; }
                else quoted = !quoted;
            }
            else if (character == ',' && !quoted) { values.Add(current.ToString().Trim().TrimStart('\uFEFF')); current.Clear(); }
            else current.Append(character);
        }
        values.Add(current.ToString().Trim());
        return values.ToArray();
    }
}
