using System.Net;
using System.Text;

namespace NetworkProfileManager.Infrastructure.ProxyAdapters;

public static class ProxyWire
{
    public static async Task<string> ReadHeaderAsync(Stream stream, CancellationToken ct)
    {
        var buffer = new byte[32768]; var count = 0;
        while (count < buffer.Length)
        {
            // Read exactly to the header boundary: never consume initial tunnel/body bytes.
            var read = await stream.ReadAsync(buffer.AsMemory(count, 1), ct);
            if (read == 0) throw new IOException("Kết nối đóng trước khi nhận đủ HTTP header.");
            count++;
            if (count >= 4 && buffer[count - 4] == 13 && buffer[count - 3] == 10 && buffer[count - 2] == 13 && buffer[count - 1] == 10)
                return Encoding.Latin1.GetString(buffer, 0, count);
        }
        throw new IOException("HTTP header vượt quá 32 KB.");
    }
    public static Task WriteAsync(Stream stream, string text, CancellationToken ct) => stream.WriteAsync(Encoding.Latin1.GetBytes(text), ct).AsTask();
    public static string Authority(string host, int port) => $"{(host.Contains(':') ? $"[{host}]" : host)}:{port}";
    public static (string Host, int Port) ParseAuthority(string authority)
    {
        if (!Uri.TryCreate("http://" + authority, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host) ||
            !string.IsNullOrEmpty(uri.UserInfo) || uri.AbsolutePath != "/" || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) ||
            !authority.EndsWith(":" + uri.Port, StringComparison.Ordinal) || uri.Port is < 1 or > 65535)
            throw new IOException("Địa chỉ CONNECT không hợp lệ.");
        return (uri.IdnHost.Trim('[', ']'), uri.Port);
    }
}
