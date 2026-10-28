using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using NetworkProfileManager.Core.Models;
using NetworkProfileManager.Infrastructure.ProxyAdapters;

namespace NetworkProfileManager.Tests;

internal sealed class MockProxy : IAsyncDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource lifetime = new();
    private readonly ConcurrentBag<Task> clients = [];
    private readonly Task accepting;
    private readonly ProxyProtocol protocol;
    private int requests;
    public const string User = "mock-user";
    public const string Password = "mock-secret!";
    public bool RejectAuthentication { get; set; }
    public bool TlsEcho { get; init; }
    public bool Stall { get; init; }
    public bool NoAuthentication { get; init; }
    public string ExpectedUser { get; init; } = User;
    public string ExpectedPassword { get; init; } = Password;
    public int ConnectStatus { get; init; } = 200;
    public int ResponseStatus { get; init; } = 200;
    public byte SocksReply { get; init; }
    public bool Chunked { get; init; }
    public bool AbruptClose { get; set; }
    public int ResponseDelayMs { get; set; }
    public Func<int, string>? IpFactory { get; init; }
    public string? Body { get; init; }
    public string[] Ips { get; init; } = ["203.0.113.7"];
    public ConcurrentBag<string> Targets { get; } = [];
    public ConcurrentBag<string> Errors { get; } = [];
    public int ConnectionCount;
    public X509Certificate2 Certificate { get; }
    public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;
    public ProxyConfiguration Configuration => new() { Protocol = protocol, Host = "127.0.0.1", Port = Port,
        Username = NoAuthentication ? "" : ExpectedUser, Password = NoAuthentication ? "" : ExpectedPassword };
    public MockProxy(ProxyProtocol protocol = ProxyProtocol.Http)
    {
        this.protocol = protocol;
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=echo.test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder(); names.AddDnsName("echo.test"); request.CertificateExtensions.Add(names.Build());
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(2));
        Certificate = X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.Exportable);
        listener.Start(); accepting = AcceptAsync();
    }
    private async Task AcceptAsync()
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(lifetime.Token);
                Interlocked.Increment(ref ConnectionCount); clients.Add(HandleAsync(client, lifetime.Token));
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException) { }
    }
    private async Task HandleAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                if (Stall) { await Task.Delay(Timeout.Infinite, ct); return; }
                if (protocol == ProxyProtocol.Socks5)
                {
                    var hello = new byte[2]; await stream.ReadExactlyAsync(hello, ct);
                    var methods = new byte[hello[1]]; await stream.ReadExactlyAsync(methods, ct);
                    await stream.WriteAsync(new byte[] { 5, (byte)(NoAuthentication ? 0 : 2) }, ct);
                    if (!NoAuthentication)
                    {
                    var auth = new byte[2]; await stream.ReadExactlyAsync(auth, ct);
                    var user = new byte[auth[1]]; await stream.ReadExactlyAsync(user, ct);
                    var pLen = new byte[1]; await stream.ReadExactlyAsync(pLen, ct);
                    var pass = new byte[pLen[0]]; await stream.ReadExactlyAsync(pass, ct);
                    var valid = !RejectAuthentication && Encoding.UTF8.GetString(user) == ExpectedUser && Encoding.UTF8.GetString(pass) == ExpectedPassword;
                    await stream.WriteAsync(new byte[] { 1, (byte)(valid ? 0 : 1) }, ct); if (!valid) return;
                    }
                    var command = new byte[4]; await stream.ReadExactlyAsync(command, ct);
                    var length = command[3] switch { 1 => 4, 4 => 16, 3 => await ByteAsync(stream, ct), _ => throw new IOException() };
                    var address = new byte[length]; await stream.ReadExactlyAsync(address, ct);
                    var port = new byte[2]; await stream.ReadExactlyAsync(port, ct);
                    Targets.Add(command[3] == 3 ? Encoding.ASCII.GetString(address) : new IPAddress(address).ToString());
                    await stream.WriteAsync(new byte[] { 5, SocksReply, 0, 1, 127, 0, 0, 1, 0, 0 }, ct);
                    if (SocksReply != 0 || AbruptClose) return;
                    await TunnelAsync(stream, ct); return;
                }
                var header = await ProxyWire.ReadHeaderAsync(stream, ct);
                var expected = "Proxy-Authorization: Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(ExpectedUser + ":" + ExpectedPassword));
                if (RejectAuthentication || (!NoAuthentication && !header.Split("\r\n").Contains(expected)))
                { await ProxyWire.WriteAsync(stream, "HTTP/1.1 407 Proxy Authentication Required\r\nProxy-Authenticate: Basic realm=\"fixture\"\r\nConnection: close\r\nContent-Length: 0\r\n\r\n", ct); return; }
                Targets.Add(header.Split("\r\n")[0]);
                if (header.StartsWith("CONNECT ", StringComparison.Ordinal))
                {
                    if (AbruptClose) return;
                    await ProxyWire.WriteAsync(stream, $"HTTP/1.1 {ConnectStatus} Proxy status\r\n\r\n", ct);
                    if (ConnectStatus != 200) return;
                    await TunnelAsync(stream, ct);
                }
                else await RespondAsync(stream, header, ct);
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or AuthenticationException or ObjectDisposedException) { Errors.Add(ex.ToString()); }
        }
    }
    private async Task TunnelAsync(Stream stream, CancellationToken ct)
    {
        if (TlsEcho)
        {
            using var tls = new SslStream(stream, true);
            await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = Certificate, EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13 }, ct);
            var header = await ProxyWire.ReadHeaderAsync(tls, ct); await RespondAsync(tls, header, ct); return;
        }
        var buffer = new byte[4096];
        var read = await stream.ReadAsync(buffer.AsMemory(0, 1), ct); if (read == 0) return;
        // SOCKS HTTP test: the bridge changes absolute form into origin form.
        if (buffer[0] is (byte)'G' or (byte)'P')
        {
            var raw = Encoding.Latin1.GetString(buffer, 0, read);
            while (!raw.Contains("\r\n\r\n")) { read = await stream.ReadAsync(buffer.AsMemory(0, 1), ct); if (read == 0) return; raw += Encoding.Latin1.GetString(buffer, 0, read); }
            Targets.Add(raw.Split("\r\n")[0]); await RespondAsync(stream, raw, ct); return;
        }
        await stream.WriteAsync(buffer.AsMemory(0, read), ct);
        while ((read = await stream.ReadAsync(buffer, ct)) > 0) await stream.WriteAsync(buffer.AsMemory(0, read), ct);
    }
    private async Task RespondAsync(Stream stream, string header, CancellationToken ct)
    {
        if (AbruptClose) return;
        if (ResponseDelayMs > 0) await Task.Delay(ResponseDelayMs, ct);
        var lengthLine = header.Split("\r\n").FirstOrDefault(l => l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
        string? posted = null;
        if (lengthLine is not null && int.TryParse(lengthLine.Split(':')[1], out var length) && length > 0)
        { var body = new byte[length]; await stream.ReadExactlyAsync(body, ct); posted = Encoding.UTF8.GetString(body); }
        var i = Interlocked.Increment(ref requests) - 1;
        var text = posted ?? Body ?? IpFactory?.Invoke(i) ?? Ips[i % Ips.Length];
        var bytes = Encoding.UTF8.GetBytes(text);
        if (Chunked)
        {
            await ProxyWire.WriteAsync(stream, $"HTTP/1.1 {ResponseStatus} Status\r\nTransfer-Encoding: chunked\r\nConnection: close\r\n\r\n", ct);
            foreach (var part in bytes.Chunk(4))
            { await ProxyWire.WriteAsync(stream, $"{part.Length:X}\r\n", ct); await stream.WriteAsync(part, ct); await ProxyWire.WriteAsync(stream, "\r\n", ct); }
            await ProxyWire.WriteAsync(stream, "0\r\n\r\n", ct);
        }
        else
        {
            await ProxyWire.WriteAsync(stream, $"HTTP/1.1 {ResponseStatus} Status\r\nContent-Type: text/plain\r\nCache-Control: no-store\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n", ct);
            await stream.WriteAsync(bytes, ct);
        }
    }
    private static async Task<int> ByteAsync(Stream stream, CancellationToken ct) { var b = new byte[1]; await stream.ReadExactlyAsync(b, ct); return b[0]; }
    public async ValueTask DisposeAsync()
    {
        await lifetime.CancelAsync(); listener.Stop(); await accepting; await Task.WhenAll(clients);
        Certificate.Dispose(); lifetime.Dispose();
    }
}
