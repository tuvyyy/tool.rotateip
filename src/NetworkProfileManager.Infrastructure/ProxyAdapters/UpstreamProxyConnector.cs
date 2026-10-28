using System.Net;
using System.Net.Sockets;
using System.Text;
using NetworkProfileManager.Core.Models;

namespace NetworkProfileManager.Infrastructure.ProxyAdapters;

public sealed class UpstreamProxyConnector
{
    public async Task<TcpClient> ConnectGatewayAsync(ProxyConfiguration proxy, CancellationToken ct)
    {
        var client = new TcpClient { NoDelay = true };
        try { await client.ConnectAsync(proxy.Host, proxy.Port, ct); return client; }
        catch (OperationCanceledException) { client.Dispose(); throw; }
        catch { client.Dispose(); throw new IOException("Không kết nối được proxy gateway (offline, DNS hoặc port)."); }
    }
    public async Task<TcpClient> OpenTunnelAsync(ProxyConfiguration proxy, string host, int port, CancellationToken ct)
    {
        var client = await ConnectGatewayAsync(proxy, ct);
        try
        {
            var stream = client.GetStream();
            if (proxy.Protocol == ProxyProtocol.Http)
            {
                var authority = ProxyWire.Authority(host, port);
                await ProxyWire.WriteAsync(stream, $"CONNECT {authority} HTTP/1.1\r\nHost: {authority}\r\n{Authorization(proxy)}\r\n", ct);
                var header = await ProxyWire.ReadHeaderAsync(stream, ct);
                var parts = header.Split("\r\n")[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2 || !int.TryParse(parts[1], out var code)) throw new IOException("HTTP proxy phản hồi không hợp lệ.");
                if (code == 407) throw new IOException("Proxy từ chối xác thực (HTTP 407).");
                if (code is < 200 or > 299) throw new IOException($"Proxy từ chối CONNECT (HTTP {code}).");
            }
            else await SocksHandshakeAsync(stream, proxy, host, port, ct);
            return client;
        }
        catch { client.Dispose(); throw; }
    }
    public static string Authorization(ProxyConfiguration proxy) => string.IsNullOrEmpty(proxy.Username) ? "" :
        $"Proxy-Authorization: Basic {Convert.ToBase64String(Encoding.UTF8.GetBytes(proxy.Username + ":" + proxy.Password))}\r\n";

    private static async Task SocksHandshakeAsync(Stream stream, ProxyConfiguration proxy, string host, int port, CancellationToken ct)
    {
        var method = (byte)(string.IsNullOrEmpty(proxy.Username) ? 0 : 2);
        await stream.WriteAsync(new byte[] { 5, 1, method }, ct);
        var reply = new byte[2]; await stream.ReadExactlyAsync(reply, ct);
        if (reply[0] != 5 || reply[1] != method) throw new IOException("SOCKS5 không chấp nhận phương thức xác thực.");
        if (method == 2)
        {
            var user = Encoding.UTF8.GetBytes(proxy.Username); var pass = Encoding.UTF8.GetBytes(proxy.Password);
            var auth = new byte[3 + user.Length + pass.Length];
            auth[0] = 1; auth[1] = (byte)user.Length; user.CopyTo(auth, 2);
            auth[2 + user.Length] = (byte)pass.Length; pass.CopyTo(auth, 3 + user.Length);
            try { await stream.WriteAsync(auth, ct); }
            finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(auth); System.Security.Cryptography.CryptographicOperations.ZeroMemory(pass); }
            await stream.ReadExactlyAsync(reply, ct);
            if (reply[0] != 1 || reply[1] != 0) throw new IOException("SOCKS5 từ chối username/password.");
        }
        byte type; byte[] address;
        if (IPAddress.TryParse(host, out var ip)) { type = (byte)(ip.AddressFamily == AddressFamily.InterNetwork ? 1 : 4); address = ip.GetAddressBytes(); }
        else
        {
            type = 3; var name = Encoding.ASCII.GetBytes(new System.Globalization.IdnMapping().GetAscii(host));
            if (name.Length > 255) throw new IOException("Tên miền quá dài cho SOCKS5.");
            address = [(byte)name.Length, .. name];
        }
        await stream.WriteAsync(new byte[] { 5, 1, 0, type }.Concat(address).Concat(new byte[] { (byte)(port >> 8), (byte)port }).ToArray(), ct);
        var response = new byte[4]; await stream.ReadExactlyAsync(response, ct);
        if (response[0] != 5 || response[2] != 0) throw new IOException("SOCKS5 phản hồi không hợp lệ.");
        if (response[1] != 0) throw new IOException($"SOCKS5 từ chối kết nối (mã {response[1]}).");
        var length = response[3] switch { 1 => 4, 4 => 16, 3 => await ReadByteAsync(stream, ct), _ => throw new IOException("SOCKS5 address type không hợp lệ.") };
        await stream.ReadExactlyAsync(new byte[length + 2], ct);
    }
    private static async Task<int> ReadByteAsync(Stream stream, CancellationToken ct)
    { var b = new byte[1]; await stream.ReadExactlyAsync(b, ct); return b[0]; }
}
