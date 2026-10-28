using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using NetworkProfileManager.Core.Models;
using NetworkProfileManager.Core.Services;

namespace NetworkProfileManager.Infrastructure.NetworkServices;

public sealed class NetworkInformationService : INetworkInformationService
{
    public Task<NetworkSnapshot> GetAsync(CancellationToken ct) => Task.Run(() =>
    {
        ct.ThrowIfCancellationRequested();
        var adapter = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .OrderByDescending(n => n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
            .ThenByDescending(n => n.GetIPProperties().GatewayAddresses.Count > 0).FirstOrDefault();
        if (adapter is null) return new NetworkSnapshot("Không kết nối", "—", "—", "—", "—", "—", "—");
        var ip = adapter.GetIPProperties();
        return new NetworkSnapshot(GetSsid(), adapter.Description,
            string.Join(", ", ip.UnicastAddresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork).Select(a => a.Address)),
            string.Join(":", adapter.GetPhysicalAddress().GetAddressBytes().Select(b => b.ToString("X2"))),
            string.Join(", ", ip.GatewayAddresses.Select(a => a.Address)), string.Join(", ", ip.DnsAddresses),
            ip.GetIPv4Properties()?.IsDhcpEnabled == true ? "Đang bật" : "Tắt / tĩnh");
    }, ct);

    private static string GetSsid()
    {
        if (WlanOpenHandle(2, IntPtr.Zero, out _, out var handle) != 0) return "Wi-Fi không khả dụng";
        try
        {
            if (WlanEnumInterfaces(handle, IntPtr.Zero, out var list) != 0) return "Không đọc được Wi-Fi";
            try
            {
                var count = Marshal.ReadInt32(list);
                for (var i = 0; i < count; i++)
                {
                    // WLAN_INTERFACE_INFO = GUID + WCHAR[256] + enum (532 bytes).
                    var item = IntPtr.Add(list, 8 + i * 532);
                    var guid = Marshal.PtrToStructure<Guid>(item);
                    if (WlanQueryInterface(handle, ref guid, 7, IntPtr.Zero, out _, out var data, out _) != 0) continue;
                    try
                    {
                        // WLAN_CONNECTION_ATTRIBUTES: state + mode + profile[256], then DOT11_SSID.
                        var length = Math.Clamp(Marshal.ReadInt32(data, 520), 0, 32);
                        if (length == 0) continue;
                        var bytes = new byte[length]; Marshal.Copy(IntPtr.Add(data, 524), bytes, 0, length);
                        return Encoding.UTF8.GetString(bytes);
                    }
                    finally { WlanFreeMemory(data); }
                }
            }
            finally { WlanFreeMemory(list); }
            return "Chưa kết nối Wi-Fi";
        }
        finally { WlanCloseHandle(handle, IntPtr.Zero); }
    }
    [DllImport("wlanapi.dll")] private static extern uint WlanOpenHandle(uint version, IntPtr reserved, out uint negotiated, out IntPtr handle);
    [DllImport("wlanapi.dll")] private static extern uint WlanCloseHandle(IntPtr handle, IntPtr reserved);
    [DllImport("wlanapi.dll")] private static extern uint WlanEnumInterfaces(IntPtr handle, IntPtr reserved, out IntPtr list);
    [DllImport("wlanapi.dll")] private static extern uint WlanQueryInterface(IntPtr handle, ref Guid guid, int opcode, IntPtr reserved, out int size, out IntPtr data, out int type);
    [DllImport("wlanapi.dll")] private static extern void WlanFreeMemory(IntPtr data);
}
