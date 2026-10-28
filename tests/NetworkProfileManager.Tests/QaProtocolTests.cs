using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using NetworkProfileManager.Core.Models;
using NetworkProfileManager.Infrastructure.ProxyAdapters;
using NetworkProfileManager.Infrastructure.Services;
using NetworkProfileManager.Infrastructure.SessionProviders;
using Xunit;

namespace NetworkProfileManager.Tests;
public class QaProtocolTests
{
    [Fact] public async Task ActualIpv6LoopbackGatewayConnects()
    {
        await using var ipv6 = new ScriptedProxy(async (client, ct) =>
        { await ProxyWire.ReadHeaderAsync(client.GetStream(), ct); await ProxyWire.WriteAsync(client.GetStream(), "HTTP/1.1 200 OK\r\n\r\nok", ct); }, IPAddress.IPv6Loopback);
        var config = new ProxyConfiguration { Host = "::1", Port = ipv6.Port }; config.Validate();
        using var timeout = new CancellationTokenSource(5000);
        using var tunnel = await new UpstreamProxyConnector().OpenTunnelAsync(config, "echo.test", 443, timeout.Token);
        var body = new byte[2]; await tunnel.GetStream().ReadExactlyAsync(body, timeout.Token); Assert.Equal("ok", Encoding.ASCII.GetString(body));
    }
    [Fact] public async Task SocksPostBodyIsNotLostAtHeaderBoundary()
    {
        await using var mock = new MockProxy(ProxyProtocol.Socks5); await using var bridge = new LocalProxyBridge(mock.Configuration, new()); bridge.Start();
        using var client = Client(bridge.Port); var body = new string('a', 12000) + "-end";
        using var response = await client.PostAsync("http://echo.test/post", new StringContent(body));
        Assert.Equal(body, await response.Content.ReadAsStringAsync());
    }
    [Theory] [InlineData(403)] [InlineData(407)] [InlineData(502)] [InlineData(503)]
    public async Task HttpConnectStatusIsDiagnosable(int status)
    {
        await using var mock = new MockProxy { ConnectStatus = status };
        var result = await Checker(mock).CheckGatewayAsync(mock.Configuration, Settings(), "A", CancellationToken.None);
        Assert.False(result.Success); Assert.Contains(status.ToString(), result.Error); Assert.DoesNotContain(MockProxy.Password, result.Error!);
    }
    [Theory] [InlineData(403)] [InlineData(407)] [InlineData(502)] [InlineData(503)]
    public async Task PlainHttpStatusIsForwardedOrAuthChallengeSuppressed(int status)
    {
        await using var mock = new MockProxy { ResponseStatus = status };
        await using var bridge = new LocalProxyBridge(mock.Configuration, new()); bridge.Start();
        using var client = Client(bridge.Port); using var response = await client.GetAsync("http://echo.test/");
        Assert.Equal(status == 407 ? 502 : status, (int)response.StatusCode);
    }
    [Theory] [InlineData(ProxyProtocol.Http)] [InlineData(ProxyProtocol.Socks5)]
    public async Task NoAuthGatewayAndChunkedResponse(ProxyProtocol protocol)
    {
        await using var mock = new MockProxy(protocol) { NoAuthentication = true, Chunked = true, TlsEcho = true };
        var result = await Checker(mock).CheckGatewayAsync(mock.Configuration, Settings(), "A", CancellationToken.None);
        Assert.True(result.Success, result.Error); Assert.Equal("203.0.113.7", result.Ip);
    }
    [Theory] [InlineData(ProxyProtocol.Http)] [InlineData(ProxyProtocol.Socks5)]
    public async Task SpecialCredentialsAuthenticate(ProxyProtocol protocol)
    {
        await using var mock = new MockProxy(protocol) { ExpectedUser = "u+é/@&", ExpectedPassword = "p:a@ss/?&=é", TlsEcho = true };
        var result = await Checker(mock).CheckGatewayAsync(mock.Configuration, Settings(), "A", CancellationToken.None);
        Assert.True(result.Success, result.Error);
    }
    [Theory] [InlineData("echo.test")] [InlineData("203.0.113.1")] [InlineData("2001:db8::1")]
    public async Task SocksForwardsRemoteDnsOrLiteralAddress(string host)
    {
        await using var mock = new MockProxy(ProxyProtocol.Socks5); using var timeout = new CancellationTokenSource(5000);
        using var tunnel = await new UpstreamProxyConnector().OpenTunnelAsync(mock.Configuration, host, 443, timeout.Token);
        Assert.Contains(host, mock.Targets);
    }
    [Theory] [InlineData(1)] [InlineData(4)] [InlineData(5)] [InlineData(8)]
    public async Task SocksConnectRefusedHasError(byte reply)
    {
        await using var mock = new MockProxy(ProxyProtocol.Socks5) { SocksReply = reply };
        var result = await Checker(mock).CheckGatewayAsync(mock.Configuration, Settings(), "A", CancellationToken.None);
        Assert.False(result.Success); Assert.Contains($"mã {reply}", result.Error);
    }
    [Theory] [InlineData(ProxyProtocol.Http)] [InlineData(ProxyProtocol.Socks5)]
    public async Task AbruptDisconnectNeverReturnsSuccess(ProxyProtocol protocol)
    {
        await using var mock = new MockProxy(protocol) { AbruptClose = true };
        var result = await Checker(mock).CheckGatewayAsync(mock.Configuration, Settings(), "A", CancellationToken.None);
        Assert.False(result.Success); Assert.Null(result.Ip);
    }
    [Theory] [InlineData("invalid ip")] [InlineData("{\"ip\":42}")] [InlineData("{\"other\":\"value\"}")] [InlineData("[]")] [InlineData("{bad")]
    public async Task InvalidEchoContentIsActionableFailure(string body)
    {
        await using var mock = new MockProxy { TlsEcho = true, Body = body };
        var result = await Checker(mock).CheckGatewayAsync(mock.Configuration, Settings(), "A", CancellationToken.None);
        Assert.False(result.Success); Assert.NotNull(result.Error);
    }
    [Fact] public async Task OversizedEchoIsRejected()
    {
        await using var mock = new MockProxy { TlsEcho = true, Body = new string('x', 5000) };
        var result = await Checker(mock).CheckGatewayAsync(mock.Configuration, Settings(), "A", CancellationToken.None);
        Assert.False(result.Success); Assert.Contains("quá lớn", result.Error);
    }
    [Theory] [InlineData("203.0.113.8")] [InlineData("2001:db8::8")] [InlineData("{\"ip\":\"2001:db8::8\"}")]
    public async Task EchoSupportsIpv4Ipv6AndJson(string body)
    {
        await using var mock = new MockProxy { TlsEcho = true, Body = body };
        var result = await Checker(mock).CheckGatewayAsync(mock.Configuration, Settings(), "B", CancellationToken.None);
        Assert.True(result.Success, result.Error); Assert.True(IPAddress.TryParse(result.Ip, out _));
    }
    [Fact] public async Task ProductionTlsValidationRejectsUntrustedFixtureCertificate()
    {
        await using var mock = new MockProxy { TlsEcho = true };
        var checker = new IpCheckerService(new(), new StandardSessionProvider());
        var result = await checker.CheckGatewayAsync(mock.Configuration, Settings(), "A", CancellationToken.None);
        Assert.False(result.Success); Assert.Null(result.Ip); Assert.True(mock.ConnectionCount > 0);
    }
    [Fact] public async Task DirectRouteBypassesAnyGatewayAndUsesOnlyEchoServer()
    {
        await using var certificate = new MockProxy();
        await using var direct = new ScriptedProxy(async (client, ct) =>
        {
            using var tls = new SslStream(client.GetStream());
            await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate.Certificate }, ct);
            await ProxyWire.ReadHeaderAsync(tls, ct);
            await ProxyWire.WriteAsync(tls, "HTTP/1.1 200 OK\r\nContent-Length: 11\r\nConnection: close\r\n\r\n203.0.113.5", ct);
        });
        var result = await Checker(certificate).CheckAsync(new() { EchoEndpoint = $"https://127.0.0.1:{direct.Port}", TimeoutSeconds = 3 }, "Direct", "direct-fixture", null, CancellationToken.None);
        Assert.True(result.Success, result.Error); Assert.Equal(0, certificate.ConnectionCount); Assert.Equal(1, direct.Connections);
    }
    [Fact] public async Task FailedProxyCannotConnectToReachableDirectEchoServer()
    {
        await using var direct = new ScriptedProxy((client, ct) => Task.CompletedTask);
        await using var offline = new MockProxy { RejectAuthentication = true };
        var result = await Checker(offline).CheckGatewayAsync(offline.Configuration, new() { EchoEndpoint = $"https://127.0.0.1:{direct.Port}", TimeoutSeconds = 3 }, "A", CancellationToken.None);
        Assert.False(result.Success); Assert.Equal(0, direct.Connections); Assert.Contains("407", result.Error);
    }
    [Fact] public async Task TimedRotationAndReauthenticationAcrossConnections()
    {
        var rotated = false;
        await using var mock = new MockProxy { TlsEcho = true, IpFactory = _ => rotated ? "203.0.113.9" : "203.0.113.7" };
        var checker = Checker(mock);
        var first = await checker.CheckGatewayAsync(mock.Configuration, Settings(), "A", CancellationToken.None);
        await Task.Delay(150); rotated = true;
        var second = await checker.CheckGatewayAsync(mock.Configuration, Settings(), "B", CancellationToken.None);
        Assert.True(first.Success); Assert.True(second.Success); Assert.NotEqual(first.Ip, second.Ip);
        mock.RejectAuthentication = true;
        var rejected = await checker.CheckGatewayAsync(mock.Configuration, Settings(), "A", CancellationToken.None);
        Assert.False(rejected.Success); Assert.Contains("407", rejected.Error);
        mock.RejectAuthentication = false;
        Assert.True((await checker.CheckGatewayAsync(mock.Configuration, Settings(), "A", CancellationToken.None)).Success);
    }
    [Fact] public async Task BridgeBindsOnlyIpv4LoopbackAndClosesPort()
    {
        await using var mock = new MockProxy(); var bridge = new LocalProxyBridge(mock.Configuration, new()); bridge.Start();
        var endpoints = System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Where(e => e.Port == bridge.Port).ToArray();
        Assert.Single(endpoints); Assert.Equal(IPAddress.Loopback, endpoints[0].Address);
        var port = bridge.Port; await bridge.DisposeAsync();
        Assert.DoesNotContain(System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners(), e => e.Port == port);
    }
    internal static IpCheckerService Checker(MockProxy mock) => new(new(), new StandardSessionProvider(), () => new HttpClientHandler
    { ServerCertificateCustomValidationCallback = (_, cert, _, _) => cert?.GetCertHashString() == mock.Certificate.GetCertHashString() });
    internal static AppSettings Settings() => new() { EchoEndpoint = "https://echo.test", TimeoutSeconds = 3 };
    private static HttpClient Client(int port) => new(new HttpClientHandler { Proxy = new WebProxy($"http://127.0.0.1:{port}"), UseProxy = true }) { Timeout = TimeSpan.FromSeconds(5) };
}
