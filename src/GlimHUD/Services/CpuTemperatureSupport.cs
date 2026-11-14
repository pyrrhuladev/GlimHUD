using System.Runtime.InteropServices;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using LibreHardwareMonitor.Hardware;

namespace GlimHUD.Services;

public enum CpuTemperatureState
{
    Ready,
    Disabled,
    DriverMissing,
    AccessDenied,
    DriverUnavailable,
    SensorUnavailable
}

/// <summary>Read-only observation of the actual PawnIO device, not an installation attempt.</summary>
public sealed record CpuTemperatureAccess(bool? DriverInstalled, string? DriverVersion,
    bool DeviceAccessible, int? DeviceOpenError);

public sealed record CpuTemperatureSensorInfo(string HardwareIdentifier, string HardwareName,
    string Identifier, string Name, double? Value, bool IsAbsoluteTemperature, bool IsSelected = false);

internal static class CpuTemperatureSupport
{
    private static readonly string[] PreferredNames =
    [
        "CPU Package", "Core (Tctl/Tdie)", "Core (Tdie)", "CPU (Tctl/Tdie)",
        "CPU (Tdie)", "CPU Die (average)", "Core Max", "Core Average"
    ];

    internal static CpuTemperatureState GetState(bool enabled, bool hasTemperature, CpuTemperatureAccess? access)
    {
        if (!enabled) return CpuTemperatureState.Disabled;
        if (hasTemperature) return CpuTemperatureState.Ready;
        if (access?.DeviceOpenError == 5) return CpuTemperatureState.AccessDenied;
        if (access is { DriverInstalled: false, DeviceAccessible: false }) return CpuTemperatureState.DriverMissing;
        if (access is { DeviceAccessible: false }) return CpuTemperatureState.DriverUnavailable;
        return CpuTemperatureState.SensorUnavailable;
    }

    internal static CpuTemperatureSensorInfo? Select(IReadOnlyList<CpuTemperatureSensorInfo> sensors)
    {
        // IntelCpu.Update in LHM 0.9.6 clears failed per-core values but retains
        // Core Max/Average from the preceding poll if every MSR read failed.
        var individualCores = sensors.Where(sensor => IsIndividualCoreName(sensor.Name)
            && sensor.IsAbsoluteTemperature).ToArray();
        var staleSummary = individualCores.Length > 0 && individualCores.All(sensor => !sensor.Value.HasValue);
        bool Usable(CpuTemperatureSensorInfo sensor) => sensor.IsAbsoluteTemperature && sensor.Value.HasValue
            && !(staleSummary && IsCoreSummary(sensor.Name));
        foreach (var name in PreferredNames)
        {
            var selected = sensors.FirstOrDefault(sensor => Usable(sensor)
                && sensor.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (selected is not null) return selected;
        }
        return sensors.Where(Usable)
            .OrderByDescending(sensor => sensor.Value).FirstOrDefault();
    }

    private static bool IsCoreSummary(string name) => name.Equals("Core Max", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Core Average", StringComparison.OrdinalIgnoreCase);
    private static bool IsIndividualCoreName(string name) => name.StartsWith("CPU Core", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("Core #", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("P-Core #", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("E-Core #", StringComparison.OrdinalIgnoreCase);

    internal static bool IsAbsoluteName(string name)
    {
        if (name.Contains("Distance", StringComparison.OrdinalIgnoreCase)
            || name.Contains("TjMax", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Limit", StringComparison.OrdinalIgnoreCase)) return false;
        return PreferredNames.Any(item => name.Equals(item, StringComparison.OrdinalIgnoreCase))
            || name.Equals("CPU", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("CPU Core", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("Core #", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("P-Core #", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("E-Core #", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("CCD", StringComparison.OrdinalIgnoreCase);
    }

    internal static IReadOnlyList<CpuTemperatureSensorInfo> ReadSensors(IHardware cpu, ISet<IHardware> updated)
    {
        var result = new List<CpuTemperatureSensorInfo>();
        void Visit(IHardware hardware)
        {
            if (updated.Contains(hardware))
            {
                result.AddRange(hardware.Sensors.Where(sensor => sensor.SensorType == SensorType.Temperature)
                    .Select(sensor => new CpuTemperatureSensorInfo(hardware.Identifier.ToString(), hardware.Name,
                        sensor.Identifier.ToString(), sensor.Name, ValidTemperature(sensor.Value), IsAbsoluteName(sensor.Name))));
            }
            foreach (var child in hardware.SubHardware) Visit(child);
        }
        Visit(cpu);
        return result;
    }

    internal static double? ValidTemperature(float? value)
        => value is float measured && float.IsFinite(measured) && measured is > -20 and < 150 ? measured : null;

    internal static CpuTemperatureAccess ProbeAccess()
    {
        bool? installed = false;
        string? version = null;
        try
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                using var hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var key = hive.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO");
                if (key is null) continue;
                installed = true;
                if (Version.TryParse(key.GetValue("DisplayVersion") as string, out var parsed)) version = parsed.ToString();
                break;
            }
        }
        catch (Exception error) when (error is UnauthorizedAccessException or System.Security.SecurityException or System.IO.IOException)
        {
            installed = null;
        }

        // Same device/access mode used by LHM 0.9.6. Opening/closing a handle does not
        // install/start a driver, load a module, issue an IOCTL, or change sensor values.
        using var device = CreateFile(@"\\?\GLOBALROOT\Device\PawnIO", 3, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        var openError = device.IsInvalid ? Marshal.GetLastPInvokeError() : (int?)null;
        var accessible = !device.IsInvalid;
        return new(accessible ? true : installed, version, accessible, openError);
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share,
        IntPtr securityAttributes, uint creation, uint flags, IntPtr template);
}
