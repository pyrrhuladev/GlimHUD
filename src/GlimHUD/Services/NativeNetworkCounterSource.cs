using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using GlimHUD.Models;

namespace GlimHUD.Services;

internal interface INetworkCounterSource
{
    IReadOnlyList<NetworkAdapterReading> Read(CancellationToken token);
}

internal sealed record NetworkAdapterReading(NetworkAdapterInfo Adapter, ulong? ReceivedBytes,
    ulong? SentBytes, uint? DefaultRouteMetric = null);

/// <summary>Reads local interface metadata/counters and the IPv4 route table. Never sends traffic.</summary>
internal sealed class NativeNetworkCounterSource : INetworkCounterSource
{
    private const int MaximumAdapters = 256;
    private const uint MaximumRouteBytes = 1024 * 1024;
    private const uint InsufficientBuffer = 122;
    private const int RouteRowSize = 14 * sizeof(uint);

    public IReadOnlyList<NetworkAdapterReading> Read(CancellationToken token)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows interface counters are required.");
        token.ThrowIfCancellationRequested();
        // A global enumeration failure is reported by the caller as unavailable,
        // rather than silently presenting an empty or stale adapter list.
        var interfaces = NetworkInterface.GetAllNetworkInterfaces();
        if (interfaces.Length > MaximumAdapters) throw new InvalidOperationException("Too many network adapters to enumerate safely.");
        var routes = ReadDefaultRoutes(token);
        var readings = new List<NetworkAdapterReading>(Math.Min(interfaces.Length, MaximumAdapters));
        foreach (var network in interfaces.Take(MaximumAdapters))
        {
            token.ThrowIfCancellationRequested();
            string id;
            string name;
            string description;
            bool operational;
            NetworkInterfaceType type;
            try
            {
                id = network.Id;
                name = network.Name;
                description = network.Description;
                operational = network.OperationalStatus == OperationalStatus.Up;
                type = network.NetworkInterfaceType;
            }
            catch (Exception error) when (IsReadFailure(error)) { continue; }
            if (string.IsNullOrWhiteSpace(id)) continue;

            long? speed = null;
            try { var reportedSpeed = network.Speed; if (reportedSpeed > 0) speed = reportedSpeed; }
            catch (Exception error) when (IsReadFailure(error)) { }

            int? ipv4Index = null;
            int? interfaceIndex = null;
            var hasGateway = false;
            try
            {
                var properties = network.GetIPProperties();
                hasGateway = properties.GatewayAddresses.Any(gateway =>
                    !gateway.Address.Equals(IPAddress.Any) && !gateway.Address.Equals(IPAddress.IPv6Any));
                try { ipv4Index = properties.GetIPv4Properties()?.Index; }
                catch (Exception error) when (IsReadFailure(error)) { }
                interfaceIndex = ipv4Index;
                if (interfaceIndex is null)
                {
                    try { interfaceIndex = properties.GetIPv6Properties()?.Index; }
                    catch (Exception error) when (IsReadFailure(error)) { }
                }
            }
            catch (Exception error) when (IsReadFailure(error)) { }

            uint? routeMetric = null;
            if (ipv4Index is > 0 && routes.TryGetValue((uint)ipv4Index.Value, out var metric))
            {
                routeMetric = metric;
                hasGateway = true;
            }
            var physical = IsEthernetOrWifi(type) && interfaceIndex is > 0 && IsHardwareInterface((uint)interfaceIndex.Value);
            ulong? received = null;
            ulong? sent = null;
            if (operational)
            {
                try
                {
                    var statistics = network.GetIPStatistics();
                    if (statistics.BytesReceived >= 0) received = (ulong)statistics.BytesReceived;
                    if (statistics.BytesSent >= 0) sent = (ulong)statistics.BytesSent;
                }
                catch (Exception error) when (IsReadFailure(error)) { }
            }
            readings.Add(new(new NetworkAdapterInfo(id, name, description, operational, physical, hasGateway, speed),
                received, sent, routeMetric));
        }
        token.ThrowIfCancellationRequested();
        return readings.AsReadOnly();
    }

    private static bool IsReadFailure(Exception error) => error is NetworkInformationException
        or NotSupportedException or InvalidOperationException or System.ComponentModel.Win32Exception
        or ArgumentException or UnauthorizedAccessException or System.Security.SecurityException;

    private static bool IsEthernetOrWifi(NetworkInterfaceType type) => type is NetworkInterfaceType.Ethernet
        or NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.GigabitEthernet
        or NetworkInterfaceType.FastEthernetT or NetworkInterfaceType.FastEthernetFx;

    private static bool IsHardwareInterface(uint index)
    {
        var row = new MibIfRow2 { InterfaceIndex = index };
        try
        {
            if (GetIfEntry2(ref row) != 0) return false;
            // HardwareInterface is the native classification. This policy does not
            // additionally require the descriptive ConnectorPresent flag.
            // Filter modules and endpoint-only devices are never Auto candidates.
            return (row.Flags & 0x01) != 0 && (row.Flags & (0x02 | 0x80)) == 0;
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        { return false; }
    }

    private static Dictionary<uint, uint> ReadDefaultRoutes(CancellationToken token)
    {
        var routes = new Dictionary<uint, uint>();
        uint required = 0;
        var result = GetIpForwardTable(IntPtr.Zero, ref required, false);
        if (result != InsufficientBuffer || required < sizeof(uint) || required > MaximumRouteBytes) return routes;
        // Retry once when routes change between the size query and the read.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            token.ThrowIfCancellationRequested();
            if (required < sizeof(uint) || required > MaximumRouteBytes) break;
            var allocated = checked((int)required);
            var table = Marshal.AllocHGlobal(allocated);
            try
            {
                result = GetIpForwardTable(table, ref required, false);
                if (result == InsufficientBuffer) continue;
                if (result != 0) break;
                var available = Math.Min(allocated, checked((int)required));
                if (available < sizeof(uint)) break;
                var count = unchecked((uint)Marshal.ReadInt32(table));
                if (count > (available - sizeof(uint)) / RouteRowSize) break;
                for (var rowNumber = 0; rowNumber < count; rowNumber++)
                {
                    token.ThrowIfCancellationRequested();
                    var row = IntPtr.Add(table, sizeof(uint) + rowNumber * RouteRowSize);
                    if (Marshal.ReadInt32(row, 0) != 0 || Marshal.ReadInt32(row, 4) != 0
                        || Marshal.ReadInt32(row, 20) == 2) continue;
                    var index = unchecked((uint)Marshal.ReadInt32(row, 16));
                    var metric = unchecked((uint)Marshal.ReadInt32(row, 36));
                    if (index == 0 || metric == uint.MaxValue) continue;
                    if (!routes.TryGetValue(index, out var previous) || metric < previous) routes[index] = metric;
                }
                break;
            }
            finally { Marshal.FreeHGlobal(table); }
        }
        return routes;
    }

    // MIB_IF_ROW2, Windows SDK netioapi.h. The app's release target is x64.
    // Native size 1352; uint InterfaceIndex follows NET_LUID; the flags byte
    // follows both WCHAR[257] names, physical addresses and interface enums.
    // https://learn.microsoft.com/windows/win32/api/netioapi/ns-netioapi-mib_if_row2
    [StructLayout(LayoutKind.Explicit, Size = 1352)]
    private struct MibIfRow2
    {
        [FieldOffset(8)] public uint InterfaceIndex;
        [FieldOffset(1152)] public byte Flags;
    }

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern uint GetIfEntry2(ref MibIfRow2 row);

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern uint GetIpForwardTable(IntPtr table, ref uint size,
        [MarshalAs(UnmanagedType.Bool)] bool order);
}

