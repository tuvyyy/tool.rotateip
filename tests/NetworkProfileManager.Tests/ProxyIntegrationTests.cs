using System.Net;
using System.Net.Sockets;
using System.Text;
using NetworkProfileManager.Core.Models;
using NetworkProfileManager.Infrastructure.ProxyAdapters;
using NetworkProfileManager.Infrastructure.Services;
using NetworkProfileManager.Infrastructure.SessionProviders;
using Xunit;

namespace NetworkProfileManager.Tests;
public class ProxyIntegrationTests
{
    private static readonly UpstreamProxyConnector Connector = new();
    [Theory] [InlineData(ProxyProtocol.Http)] [InlineData(ProxyProtocol.Socks5)]
    public async Task AuthenticatedTunnelForwardsBytes(ProxyProtocol protocol)
    {
        await using var mock = new MockProxy(protocol);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var tunnel = await Connector.OpenTunnelAsync(mock.Configuration, "echo.test", 443, timeout.Token);
        await tunnel.GetStream().WriteAsync("tunnel-payload"u8.ToArray(), timeout.Token);
        var received = new byte[14]; await tunnel.GetStream().ReadExactlyAsync(received, timeout.Token);
        Assert.Equal("tunnel-payload", Encoding.UTF8.GetString(received));
    }
    [Theory] [InlineData(ProxyProtocol.Http)] [InlineData(ProxyProtocol.Socks5)]
    public async Task WrongCredentialsRejected(ProxyProtocol protocol)
    {
        await using var mock = new MockProxy(protocol);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var ex = await Assert.ThrowsAsync<IOException>(() => Connector.OpenTunnelAsync(mock.Configuration with { Password = "wrong" }, "echo.test", 443, timeout.Token));
        Assert.DoesNotContain("wrong", ex.Message); Assert.Contains(protocol == ProxyProtocol.Http ? "407" : "username/password", ex.Message);
    }
    [Theory] [InlineData(ProxyProtocol.Http)] [InlineData(ProxyProtocol.Socks5)]
    public async Task TwoIndependentListenersUseSameGateway(ProxyProtocol protocol)
    {
        await using var mock = new MockProxy(protocol);
        await using var a = new LocalProxyBridge(mock.Configuration, Connector); a.Start();
        await using var b = new LocalProxyBridge(mock.Configuration, Connector); b.Start(); Assert.NotEqual(a.Port, b.Port);
        using var clientA = Client(a.Port); using var clientB = Client(b.Port);
        var results = await Task.WhenAll(clientA.GetStringAsync("http://echo.test/a"), clientB.GetStringAsync("http://echo.test/b"));
        Assert.All(results, ip => Assert.Equal("203.0.113.7", ip)); Assert.True(mock.ConnectionCount >= 2);
        await a.DisposeAsync(); Assert.Equal("203.0.113.7", await clientB.GetStringAsync("http://echo.test/still-alive"));
    }
    [Fact] public async Task HttpPostBodyIsForwarded()
    {
        await using var mock = new MockProxy(); await using var bridge = new LocalProxyBridge(mock.Configuration, Connector); bridge.Start();
        using var client = Client(bridge.Port); using var response = await client.PostAsync("http://echo.test/post", new StringContent("request-body"));
        Assert.Equal("request-body", await response.Content.ReadAsStringAsync());
    }
    [Fact] public async Task HttpConnectThroughBridgePreservesTunnel()
    {
        await using var mock = new MockProxy(); await using var bridge = new LocalProxyBridge(mock.Configuration, Connector); bridge.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8)); using var local = new TcpClient();
        await local.ConnectAsync(IPAddress.Loopback, bridge.Port, timeout.Token);
        await ProxyWire.WriteAsync(local.GetStream(), "CONNECT echo.test:443 HTTP/1.1\r\nHost: echo.test:443\r\n\r\n", timeout.Token);
        Assert.StartsWith("HTTP/1.1 200", await ProxyWire.ReadHeaderAsync(local.GetStream(), timeout.Token));
        await local.GetStream().WriteAsync("hello"u8.ToArray(), timeout.Token); var bytes = new byte[5]; await local.GetStream().ReadExactlyAsync(bytes, timeout.Token);
        Assert.Equal("hello", Encoding.ASCII.GetString(bytes));
    }
    [Fact] public async Task PortConflictFallsBackAndCleanupClosesSocket()
    {
        using var occupied = new TcpListener(IPAddress.Loopback, 0); occupied.Server.ExclusiveAddressUse = true; occupied.Start();
        var port = ((IPEndPoint)occupied.LocalEndpoint).Port;
        await using var mock = new MockProxy(); var bridge = new LocalProxyBridge(mock.Configuration, Connector); bridge.Start(port);
        Assert.NotEqual(port, bridge.Port); var assigned = bridge.Port; await bridge.DisposeAsync();
        using var client = new TcpClient(); await Assert.ThrowsAsync<SocketException>(() => client.ConnectAsync(IPAddress.Loopback, assigned));
    }
    [Fact] public async Task OfflineProxyDoesNotFallBackToDirect()
    {
        using var unused = new TcpListener(IPAddress.Loopback, 0); unused.Start(); var port = ((IPEndPoint)unused.LocalEndpoint).Port; unused.Stop();
        await using var bridge = new LocalProxyBridge(new() { Host = "127.0.0.1", Port = port }, Connector, 3); bridge.Start();
        using var client = Client(bridge.Port); using var response = await client.GetAsync("http://echo.test/");
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode); Assert.Contains("gateway", bridge.LastError);
    }
    [Fact] public async Task StalledHandshakeHasTimeout()
    {
        await using var mock = new MockProxy { Stall = true }; await using var bridge = new LocalProxyBridge(mock.Configuration, Connector, 1); bridge.Start();
        using var client = Client(bridge.Port);
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("https://echo.test/"));
        Assert.Equal("Proxy quá thời gian chờ.", bridge.LastError);
    }
    [Theory] [InlineData(ProxyProtocol.Http, false)] [InlineData(ProxyProtocol.Socks5, false)] [InlineData(ProxyProtocol.Http, true)]
    public async Task IpCheckerHandlesRealTlsEchoAndRotation(ProxyProtocol protocol, bool rotating)
    {
        await using var mock = new MockProxy(protocol) { TlsEcho = true, Ips = rotating ? ["203.0.113.7", "203.0.113.8", "203.0.113.9"] : ["203.0.113.7"] };
        // Trust exactly the ephemeral fixture certificate, never disable TLS validation in the application.
        var checker = new IpCheckerService(Connector, new StandardSessionProvider(), () => new HttpClientHandler
        { ServerCertificateCustomValidationCallback = (_, cert, _, _) => cert?.GetCertHashString() == mock.Certificate.GetCertHashString() });
        var results = new List<IpTestResult>();
        for (var i = 0; i < 3; i++) results.Add(await checker.CheckGatewayAsync(mock.Configuration, new() { EchoEndpoint = "https://echo.test", TimeoutSeconds = 5 }, "A", CancellationToken.None));
        Assert.All(results, r => Assert.True(r.Success, r.Error + "\n" + string.Join("\n", mock.Errors)));
        Assert.Equal(rotating ? "Phát hiện IP thay đổi" : "Ba lần kiểm tra cùng IP", RotationAnalysis.Describe(results));
    }
    [Fact] public async Task IpCheckerReportsAuthenticationCause()
    {
        await using var mock = new MockProxy { RejectAuthentication = true };
        var checker = new IpCheckerService(Connector, new StandardSessionProvider());
        var result = await checker.CheckGatewayAsync(mock.Configuration, new() { EchoEndpoint = "https://echo.test", TimeoutSeconds = 3 }, "A", CancellationToken.None);
        Assert.False(result.Success); Assert.Contains("407", result.Error); Assert.DoesNotContain(MockProxy.Password, result.Error!);
    }
    [Fact] public async Task CancellationStopsIpCheck()
    {
        await using var mock = new MockProxy { Stall = true };
        var checker = new IpCheckerService(Connector, new StandardSessionProvider()); using var ct = new CancellationTokenSource(200);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => checker.CheckGatewayAsync(mock.Configuration, new() { EchoEndpoint = "https://echo.test" }, "A", ct.Token));
    }
    [Fact] public async Task ClosingBridgeTerminatesOpenTunnel()
    {
        await using var mock = new MockProxy(); var bridge = new LocalProxyBridge(mock.Configuration, Connector); bridge.Start();
        using var local = new TcpClient(); await local.ConnectAsync(IPAddress.Loopback, bridge.Port);
        await ProxyWire.WriteAsync(local.GetStream(), "CONNECT echo.test:443 HTTP/1.1\r\nHost: echo.test:443\r\n\r\n", CancellationToken.None);
        await ProxyWire.ReadHeaderAsync(local.GetStream(), CancellationToken.None);
        await bridge.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(0, await local.GetStream().ReadAsync(new byte[1]).AsTask().WaitAsync(TimeSpan.FromSeconds(3)));
    }
    private static HttpClient Client(int port) => new(new HttpClientHandler { Proxy = new WebProxy($"http://127.0.0.1:{port}"), UseProxy = true }) { Timeout = TimeSpan.FromSeconds(8) };
}
