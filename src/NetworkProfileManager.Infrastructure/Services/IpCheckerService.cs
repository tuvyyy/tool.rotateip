using System.Diagnostics;
using System.Net;
using System.Text.Json;
using NetworkProfileManager.Core.Models;
using NetworkProfileManager.Core.Services;
using NetworkProfileManager.Infrastructure.ProxyAdapters;

namespace NetworkProfileManager.Infrastructure.Services;

public sealed class IpCheckerService(UpstreamProxyConnector connector, IProxySessionProvider sessions, Func<HttpClientHandler>? handlerFactory = null)
{
    public async Task<IpTestResult> CheckGatewayAsync(ProxyConfiguration proxy, AppSettings settings, string profile, CancellationToken ct)
    {
        var resolved = sessions.Resolve(proxy, profile);
        await using var bridge = new LocalProxyBridge(resolved, connector, settings.TimeoutSeconds); bridge.Start();
        var result = await CheckAsync(settings, profile, resolved.Endpoint, bridge.Port, ct);
        return !result.Success && bridge.LastError is not null ? result with { Error = bridge.LastError } : result;
    }
    public async Task<IpTestResult> CheckAsync(AppSettings settings, string profile, string endpoint, int? localPort, CancellationToken ct)
    {
        settings.Validate(); var watch = Stopwatch.StartNew();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(settings.TimeoutSeconds));
            using var handler = handlerFactory?.Invoke() ?? new HttpClientHandler();
            handler.UseProxy = localPort.HasValue;
            handler.Proxy = localPort.HasValue ? new WebProxy($"http://127.0.0.1:{localPort}", false) : null;
            handler.AllowAutoRedirect = false; handler.UseCookies = false;
            using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            using var response = await client.GetAsync(settings.EchoEndpoint, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode) throw new IOException($"HTTP {(int)response.StatusCode}; proxy hoặc dịch vụ IP echo từ chối yêu cầu.");
            await using var body = await response.Content.ReadAsStreamAsync(timeout.Token);
            var buffer = new byte[4097]; var length = 0;
            while (length < buffer.Length)
            {
                var n = await body.ReadAsync(buffer.AsMemory(length), timeout.Token); if (n == 0) break; length += n;
            }
            if (length > 4096) throw new IOException("Dịch vụ IP echo trả về dữ liệu quá lớn.");
            var raw = System.Text.Encoding.UTF8.GetString(buffer, 0, length).Trim();
            if (raw.StartsWith('{'))
            { using var doc = JsonDocument.Parse(raw); raw = doc.RootElement.GetProperty("ip").GetString() ?? ""; }
            if (!IPAddress.TryParse(raw, out var ip)) throw new IOException("Dịch vụ IP echo không trả về địa chỉ IP hợp lệ.");
            return new(profile, ip.ToString(), true, watch.ElapsedMilliseconds, DateTimeOffset.UtcNow, endpoint, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            var error = ex switch
            {
                OperationCanceledException => "Hết thời gian chờ kiểm tra IP.",
                IOException io => io.Message,
                HttpRequestException => "Không truy cập được IP echo qua tuyến đã chọn; kiểm tra proxy, xác thực, DNS và TLS.",
                _ => "Phản hồi IP echo không hợp lệ."
            };
            return new(profile, null, false, watch.ElapsedMilliseconds, DateTimeOffset.UtcNow, endpoint, error);
        }
    }
}
