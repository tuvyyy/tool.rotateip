using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using NetworkProfileManager.Core.Models;

namespace NetworkProfileManager.Infrastructure.ProxyAdapters;

public sealed class LocalProxyBridge : IAsyncDisposable
{
    private readonly ProxyConfiguration proxy;
    private readonly UpstreamProxyConnector connector;
    private readonly int timeoutSeconds;
    private readonly CancellationTokenSource lifetime = new();
    private readonly ConcurrentDictionary<long, Task> connections = new();
    private readonly SemaphoreSlim slots = new(128);
    private TcpListener? listener;
    private Task? accepting;
    private long sequence;
    public int Port { get; private set; }
    public string? LastError { get; private set; }
    public event Action<string>? ConnectionFailed;
    public LocalProxyBridge(ProxyConfiguration proxy, UpstreamProxyConnector connector, int timeoutSeconds = 20)
    { proxy.Validate(); this.proxy = proxy; this.connector = connector; this.timeoutSeconds = timeoutSeconds; }

    public void Start(int preferredPort = 0)
    {
        if (listener is not null || lifetime.IsCancellationRequested) throw new InvalidOperationException("Listener đã khởi tạo hoặc đã dừng.");
        listener = new TcpListener(IPAddress.Loopback, preferredPort);
        listener.Server.ExclusiveAddressUse = true;
        try { listener.Start(128); }
        catch (SocketException) when (preferredPort != 0)
        { listener.Stop(); listener = new TcpListener(IPAddress.Loopback, 0); listener.Server.ExclusiveAddressUse = true; listener.Start(128); }
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        accepting = AcceptAsync();
    }
    private async Task AcceptAsync()
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                await slots.WaitAsync(lifetime.Token);
                TcpClient client;
                try { client = await listener!.AcceptTcpClientAsync(lifetime.Token); }
                catch { slots.Release(); throw; }
                var id = Interlocked.Increment(ref sequence);
                var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                connections[id] = completion.Task;
                _ = CompleteConnectionAsync(id, client, completion);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException) { }
    }
    private async Task CompleteConnectionAsync(long id, TcpClient client, TaskCompletionSource completion)
    {
        try { await HandleAsync(client, lifetime.Token); }
        catch (Exception) { client.Dispose(); }
        finally
        {
            slots.Release(); connections.TryRemove(id, out _); completion.TrySetResult();
        }
    }
    private async Task HandleAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            var downstream = client.GetStream(); bool responseStarted = false;
            try
            {
                using var handshake = CancellationTokenSource.CreateLinkedTokenSource(ct);
                handshake.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
                var header = await ProxyWire.ReadHeaderAsync(downstream, handshake.Token);
                var lines = header.Split("\r\n", StringSplitOptions.None);
                var request = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (request.Length != 3 || request[2] is not ("HTTP/1.0" or "HTTP/1.1")) throw new IOException("Yêu cầu HTTP không hợp lệ.");
                TcpClient upstream;
                var isConnect = request[0].Equals("CONNECT", StringComparison.OrdinalIgnoreCase);
                if (isConnect)
                {
                    var (host, port) = ProxyWire.ParseAuthority(request[1]);
                    upstream = await connector.OpenTunnelAsync(proxy, host, port, handshake.Token);
                }
                else
                {
                    if (!Uri.TryCreate(request[1], UriKind.Absolute, out var target) || target.Scheme != "http" || !string.IsNullOrEmpty(target.UserInfo))
                        throw new IOException("Local proxy chỉ nhận HTTP absolute URI hoặc HTTPS CONNECT.");
                    upstream = proxy.Protocol == ProxyProtocol.Http
                        ? await connector.ConnectGatewayAsync(proxy, handshake.Token)
                        : await connector.OpenTunnelAsync(proxy, target.IdnHost, target.Port, handshake.Token);
                }
                using (upstream)
                {
                    var stream = upstream.GetStream();
                    if (isConnect)
                    {
                        await ProxyWire.WriteAsync(downstream, "HTTP/1.1 200 Connection Established\r\n\r\n", handshake.Token);
                        responseStarted = true;
                        await RelayAsync(downstream, stream, client.Client, upstream.Client, ct);
                    }
                    else
                    {
                        var uri = new Uri(request[1]);
                        var path = proxy.Protocol == ProxyProtocol.Http ? uri.AbsoluteUri : uri.PathAndQuery;
                        var outgoing = new StringBuilder($"{request[0]} {path} {request[2]}\r\n");
                        var remove = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Host", "Proxy-Authorization", "Proxy-Connection", "Connection", "Keep-Alive" };
                        foreach (var line in lines.Skip(1))
                        {
                            var colon = line.IndexOf(':');
                            if (colon > 0 && line[..colon].Equals("Connection", StringComparison.OrdinalIgnoreCase))
                                foreach (var name in line[(colon + 1)..].Split(',')) remove.Add(name.Trim());
                        }
                        foreach (var line in lines.Skip(1))
                        {
                            if (line.Length == 0) break;
                            var colon = line.IndexOf(':');
                            if (colon <= 0 || char.IsWhiteSpace(line[0])) throw new IOException("HTTP header không hợp lệ.");
                            if (!remove.Contains(line[..colon])) outgoing.Append(line).Append("\r\n");
                        }
                        outgoing.Append("Host: ").Append(uri.Authority).Append("\r\nConnection: close\r\n");
                        if (proxy.Protocol == ProxyProtocol.Http) outgoing.Append(UpstreamProxyConnector.Authorization(proxy));
                        outgoing.Append("\r\n");
                        await ProxyWire.WriteAsync(stream, outgoing.ToString(), handshake.Token);
                        using var httpRelay = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        var upload = PumpHalfCloseAsync(downstream, stream, upstream.Client, httpRelay.Token);
                        try
                        {
                            // Request bodies must flow while waiting for the upstream response (POST / Expect).
                            var response = await ProxyWire.ReadHeaderAsync(stream, handshake.Token);
                            while (response.StartsWith("HTTP/1.1 100 ", StringComparison.Ordinal))
                            {
                                await ProxyWire.WriteAsync(downstream, response, handshake.Token);
                                response = await ProxyWire.ReadHeaderAsync(stream, handshake.Token);
                            }
                            var status = response.Split("\r\n")[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                            if (status.Length < 2 || !int.TryParse(status[1], out var code)) throw new IOException("Proxy phản hồi HTTP không hợp lệ.");
                            if (code == 407) throw new IOException("Proxy từ chối xác thực (HTTP 407).");
                            await ProxyWire.WriteAsync(downstream, response, handshake.Token); responseStarted = true;
                            var download = PumpAsync(stream, downstream, httpRelay.Token);
                            var first = await Task.WhenAny(upload, download);
                            if (first == upload && upload.IsCompletedSuccessfully) await download;
                            await httpRelay.CancelAsync();
                            try { await Task.WhenAll(upload, download); }
                            catch (Exception error) when (error is OperationCanceledException or IOException or SocketException) { }
                        }
                        finally
                        {
                            await httpRelay.CancelAsync();
                            try { await upload; } catch (Exception error) when (error is OperationCanceledException or IOException or SocketException) { }
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException or ArgumentException)
            {
                if (!ct.IsCancellationRequested)
                {
                    LastError = ex is OperationCanceledException ? "Proxy quá thời gian chờ." : ex is IOException ? ex.Message : "Kết nối proxy thất bại; kiểm tra gateway và xác thực.";
                    ConnectionFailed?.Invoke(LastError);
                    if (!responseStarted)
                    {
                        try
                        {
                            using var send = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                            await ProxyWire.WriteAsync(downstream, "HTTP/1.1 502 Bad Gateway\r\nConnection: close\r\nContent-Length: 0\r\n\r\n", send.Token);
                        }
                        catch (Exception error) when (error is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
                    }
                }
            }
        }
    }
    private static async Task RelayAsync(Stream a, Stream b, Socket downstream, Socket upstream, CancellationToken ct)
    {
        using var relay = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var forward = PumpHalfCloseAsync(a, b, upstream, relay.Token);
        var reverse = PumpHalfCloseAsync(b, a, downstream, relay.Token);
        var first = await Task.WhenAny(forward, reverse);
        if (!first.IsCompletedSuccessfully) await relay.CancelAsync();
        try { await Task.WhenAll(forward, reverse); }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException) { }
    }
    private static async Task PumpHalfCloseAsync(Stream from, Stream to, Socket destination, CancellationToken ct)
    {
        await PumpAsync(from, to, ct);
        try { destination.Shutdown(SocketShutdown.Send); }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException) { }
    }
    private static async Task PumpAsync(Stream from, Stream to, CancellationToken ct)
    {
        var buffer = new byte[32768];
        while (true)
        {
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct); idle.CancelAfter(TimeSpan.FromMinutes(5));
            var read = await from.ReadAsync(buffer, idle.Token); if (read == 0) return;
            await to.WriteAsync(buffer.AsMemory(0, read), idle.Token);
        }
    }
    public async ValueTask DisposeAsync()
    {
        if (lifetime.IsCancellationRequested) return;
        await lifetime.CancelAsync(); listener?.Stop();
        if (accepting is not null) await accepting;
        await Task.WhenAll(connections.Values);
        lifetime.Dispose(); slots.Dispose();
    }
}
