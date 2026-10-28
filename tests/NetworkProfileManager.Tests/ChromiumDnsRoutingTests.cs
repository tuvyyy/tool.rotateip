using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NetworkProfileManager.Core.Models;
using NetworkProfileManager.Infrastructure.BrowserAdapters;
using NetworkProfileManager.Infrastructure.ProxyAdapters;
using Xunit;

namespace NetworkProfileManager.Tests;

[Collection("QA browser lifecycle")]
public class ChromiumDnsRoutingTests
{
    [Theory]
    [InlineData(BrowserKind.Chrome)]
    [InlineData(BrowserKind.Edge)]
    [InlineData(BrowserKind.CocCoc)]
    public void ManagedChromiumArgumentsRetainWarningsTlsAndFixedProxy(BrowserKind kind)
    {
        using var folder = new TestFolder();
        var info = new ChromiumBrowserAdapter().Prepare(new("A", "Routing invariant", kind),
            Path.Combine(Environment.SystemDirectory, "cmd.exe"), folder.Root, 17881, "https://example.com");
        var proxy = Assert.Single(info.ArgumentList, a => a.StartsWith("--proxy-server=", StringComparison.Ordinal));
        Assert.Equal("--proxy-server=http://127.0.0.1:17881", proxy);
        Assert.Contains("--proxy-bypass-list=<-loopback>", info.ArgumentList);
        Assert.Contains("--host-resolver-rules=MAP * ~NOTFOUND, EXCLUDE 127.0.0.1", info.ArgumentList);
        foreach (var forbidden in new[] { "--disable-infobars", "--test-type", "--enable-automation", "--no-sandbox",
            "--disable-web-security", "--ignore-certificate-errors", "--ignore-certificate-errors-spki-list" })
            Assert.DoesNotContain(info.ArgumentList, a => a == forbidden || a.StartsWith(forbidden + "=", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [Trait("Category", "WindowsBrowser")]
    public async Task RawBrowserDnsRequiresResolverGuardEvenWithHttpProxy(bool guard, bool disablePrediction)
    {
        using var folder = new TestFolder();
        await using var upstream = new MockProxy();
        await using var bridge = new LocalProxyBridge(upstream.Configuration, new()); bridge.Start();
        await using var chrome = await ChromeProbe.StartAsync(folder, bridge.Port, guard, disablePrediction);
        await chrome.NavigateAsync("chrome://net-internals/#dns");
        var result = await chrome.EvaluateAsync("""
            (async () => {
              const {sendWithPromise} = await import('chrome://resources/js/cr.js');
              try {
                const answer = await sendWithPromise('resolveHost', 'example.com');
                return {resolved: answer.resolved_addresses.length > 0, error: null};
              } catch (error) { return {resolved: false, error: String(error)}; }
            })()
            """);
        if (guard)
        {
            Assert.False(result.GetProperty("resolved").GetBoolean());
            Assert.Equal("net::ERR_NAME_NOT_RESOLVED", result.GetProperty("error").GetString());
        }
        await chrome.CloseAsync();
        var dnsEvents = CountLocalResolverEvents(chrome.NetLogPath, "example.com");
        Assert.True(guard ? dnsEvents == 0 : dnsEvents > 0,
            $"Unexpected local resolver events: guard={guard}, disablePrediction={disablePrediction}, events={dnsEvents}.");
        QaEvidence.Write($"logs/chromium-dns-guard-{guard}-prediction-disabled-{disablePrediction}.json", new {
            Chrome = chrome.Version, Guard = guard, PredictionDisabled = disablePrediction,
            RawDnsResolved = result.GetProperty("resolved").GetBoolean(), LocalResolverEvents = dnsEvents,
            ExpectedBlocked = guard, Status = "PASS"
        });
    }

    [Theory]
    [InlineData(ProxyProtocol.Http, true)]
    [InlineData(ProxyProtocol.Socks5, true)]
    [InlineData(ProxyProtocol.Http, false)]
    [InlineData(ProxyProtocol.Socks5, false)]
    [Trait("Category", "WindowsBrowser")]
    public async Task RealChromeIpAndHttpsUseProxyHostnameWithoutLocalDns(ProxyProtocol protocol, bool guard)
    {
        using var folder = new TestFolder();
        await using var upstream = new MockProxy(protocol) { TlsEcho = true };
        await using var bridge = new LocalProxyBridge(upstream.Configuration, new()); bridge.Start();
        // Trust only this ephemeral fixture key, only in this isolated test process.
        // No certificate bypass or test switch is added to production launch arguments.
        var spki = Convert.ToBase64String(SHA256.HashData(upstream.Certificate.PublicKey.ExportSubjectPublicKeyInfo()));
        await using var chrome = await ChromeProbe.StartAsync(folder, bridge.Port, guard, fixtureSpki: spki);
        Assert.Null(await chrome.NavigateAsync("https://echo.test/ip?run=" + Guid.NewGuid().ToString("N")));
        Assert.Equal("203.0.113.7", await chrome.WaitForBodyAsync("203.0.113.7"));
        Assert.True((await chrome.EvaluateAsync("window.isSecureContext")).GetBoolean());
        Assert.Contains(protocol == ProxyProtocol.Http ? "CONNECT echo.test:443 HTTP/1.1" : "echo.test", upstream.Targets);
        await chrome.CloseAsync();
        var events = CountLocalResolverEvents(chrome.NetLogPath, "echo.test");
        Assert.Equal(0, events);
        QaEvidence.Write($"logs/chromium-https-{protocol}-guard-{guard}.json", new {
            Chrome = chrome.Version, UpstreamProtocol = protocol.ToString(), Guard = guard,
            BrowserReadFixtureIp = true, SecureContext = true, LocalDestinationDnsEvents = events, Status = "PASS"
        });
    }

    [Fact]
    [Trait("Category", "WindowsBrowser")]
    public async Task ProductionChromeArgumentsStillRejectUntrustedHttps()
    {
        using var folder = new TestFolder();
        await using var upstream = new MockProxy { TlsEcho = true };
        await using var bridge = new LocalProxyBridge(upstream.Configuration, new()); bridge.Start();
        await using var chrome = await ChromeProbe.StartAsync(folder, bridge.Port);
        var error = await chrome.NavigateAsync("https://echo.test/untrusted");
        Assert.StartsWith("net::ERR_CERT_", error);
    }

    [Theory]
    [InlineData(ProxyProtocol.Http)]
    [InlineData(ProxyProtocol.Socks5)]
    [Trait("Category", "WindowsBrowser")]
    public async Task ChromeDoesNotGoDirectWhenUpstreamDropsOrLocalBridgeStops(ProxyProtocol protocol)
    {
        using var folder = new TestFolder();
        await using var upstream = new MockProxy(protocol);
        await using var bridge = new LocalProxyBridge(upstream.Configuration, new()); bridge.Start();
        var directHits = 0;
        await using var directTrap = new ScriptedProxy(async (client, ct) => {
            Interlocked.Increment(ref directHits);
            await ProxyWire.ReadHeaderAsync(client.GetStream(), ct);
            await ProxyWire.WriteAsync(client.GetStream(), "HTTP/1.1 200 OK\r\nContent-Length: 11\r\n\r\nDIRECT-LEAK", ct);
        });
        await using var chrome = await ChromeProbe.StartAsync(folder, bridge.Port);
        var target = $"http://127.0.0.1:{directTrap.Port}";
        Assert.Null(await chrome.NavigateAsync(target + "/healthy"));
        Assert.Equal("203.0.113.7", await chrome.WaitForBodyAsync("203.0.113.7"));
        Assert.Equal(0, Volatile.Read(ref directHits));
        upstream.AbruptClose = true;
        await chrome.NavigateAsync(target + "/upstream-dropped");
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (bridge.LastError is null && DateTime.UtcNow < deadline) await Task.Delay(50);
        Assert.NotNull(bridge.LastError);
        Assert.Equal(0, Volatile.Read(ref directHits));
        await bridge.DisposeAsync();
        var error = await chrome.NavigateAsync(target + "/bridge-stopped");
        Assert.Equal("net::ERR_PROXY_CONNECTION_FAILED", error);
        Assert.Equal(0, Volatile.Read(ref directHits));
        QaEvidence.Write($"logs/chromium-proxy-disconnect-{protocol}.json", new {
            Chrome = chrome.Version, UpstreamProtocol = protocol.ToString(), UpstreamDropDetected = true,
            BridgeStoppedError = error, DirectConnections = directHits, Status = "PASS"
        });
    }

    private static int CountLocalResolverEvents(string path, string host)
    {
        using var log = JsonDocument.Parse(File.ReadAllText(path));
        var names = log.RootElement.GetProperty("constants").GetProperty("logEventTypes")
            .EnumerateObject().ToDictionary(p => p.Value.GetInt32(), p => p.Name);
        return log.RootElement.GetProperty("events").EnumerateArray().Count(e =>
            names.TryGetValue(e.GetProperty("type").GetInt32(), out var name) &&
            (name == "HOST_RESOLVER_MANAGER_JOB" || name == "HOST_RESOLVER_SYSTEM_TASK" || name.StartsWith("DNS_TRANSACTION", StringComparison.Ordinal)) &&
            e.TryGetProperty("params", out var parameters) && parameters.GetRawText().Contains(host, StringComparison.OrdinalIgnoreCase));
    }

    // CDP is restricted to a new headless Chrome and temporary profile owned by this test.
    private sealed class ChromeProbe : IAsyncDisposable
    {
        private readonly BrowserProcessJob job;
        private readonly ClientWebSocket socket;
        private int sequence;
        private bool closed;
        public string NetLogPath { get; }
        public string Version { get; }
        private ChromeProbe(BrowserProcessJob job, ClientWebSocket socket, string netLog, string version)
        { this.job = job; this.socket = socket; NetLogPath = netLog; Version = version; }

        public static async Task<ChromeProbe> StartAsync(TestFolder folder, int port, bool guard = true,
            bool disablePrediction = false, string? fixtureSpki = null)
        {
            var executable = BrowserProfileService.FindExecutable(BrowserKind.Chrome);
            Assert.True(File.Exists(executable), "Chrome must be installed for this focused browser test.");
            var directory = Path.Combine(folder.Root, "Chrome");
            var info = new ChromiumBrowserAdapter().Prepare(new("A", "DNS regression", BrowserKind.Chrome), executable!, directory, port, "about:blank");
            if (!guard)
                info.ArgumentList.Remove(info.ArgumentList.Single(a => a.StartsWith("--host-resolver-rules=", StringComparison.Ordinal)));
            if (disablePrediction)
            {
                Directory.CreateDirectory(Path.Combine(directory, "Default"));
                File.WriteAllText(Path.Combine(directory, "Default", "Preferences"), "{\"net\":{\"network_prediction_options\":2}}");
            }
            var netLog = Path.Combine(folder.Root, "netlog.json");
            info.ArgumentList.Insert(0, "--headless=new");
            info.ArgumentList.Insert(0, "--remote-debugging-port=0");
            info.ArgumentList.Insert(0, "--log-net-log=" + netLog);
            if (fixtureSpki is not null) info.ArgumentList.Insert(0, "--ignore-certificate-errors-spki-list=" + fixtureSpki);
            var job = BrowserProcessJob.Start(info);
            var socket = new ClientWebSocket();
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                var activePort = Path.Combine(directory, "DevToolsActivePort");
                string[] lines = [];
                while (lines.Length < 2)
                {
                    timeout.Token.ThrowIfCancellationRequested();
                    if (File.Exists(activePort)) lines = await File.ReadAllLinesAsync(activePort, timeout.Token);
                    if (lines.Length < 2) await Task.Delay(50, timeout.Token);
                }
                using var http = new HttpClient(new HttpClientHandler { UseProxy = false });
                string? pageSocket = null;
                while (pageSocket is null)
                {
                    using var targets = JsonDocument.Parse(await http.GetStringAsync($"http://127.0.0.1:{lines[0]}/json/list", timeout.Token));
                    pageSocket = targets.RootElement.EnumerateArray().Where(t => t.GetProperty("type").GetString() == "page")
                        .Select(t => t.GetProperty("webSocketDebuggerUrl").GetString()).FirstOrDefault();
                    if (pageSocket is null) await Task.Delay(50, timeout.Token);
                }
                await socket.ConnectAsync(new Uri(pageSocket), timeout.Token);
                var probe = new ChromeProbe(job, socket, netLog, FileVersionInfo.GetVersionInfo(executable!).ProductVersion!);
                await probe.CommandAsync("Page.enable");
                return probe;
            }
            catch { socket.Dispose(); job.Dispose(); throw; }
        }

        public async Task<string?> NavigateAsync(string url)
        {
            var result = await CommandAsync("Page.navigate", new { url });
            return result.TryGetProperty("errorText", out var error) ? error.GetString() : null;
        }

        public async Task<JsonElement> EvaluateAsync(string expression)
        {
            var result = await CommandAsync("Runtime.evaluate", new { expression, awaitPromise = true, returnByValue = true });
            Assert.False(result.TryGetProperty("exceptionDetails", out _), result.GetRawText());
            return result.GetProperty("result").GetProperty("value").Clone();
        }

        public async Task<string> WaitForBodyAsync(string expected)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            string? body = null;
            while (DateTime.UtcNow < deadline)
            {
                body = (await EvaluateAsync("document.body?.innerText ?? ''")).GetString();
                if (body == expected) return body;
                await Task.Delay(50);
            }
            return body ?? "";
        }

        private async Task<JsonElement> CommandAsync(string method, object? parameters = null)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var id = ++sequence;
            await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(new { id, method, @params = parameters ?? new { } }), WebSocketMessageType.Text, true, timeout.Token);
            var buffer = new byte[65536];
            while (true)
            {
                using var message = new MemoryStream();
                WebSocketReceiveResult received;
                do
                {
                    received = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), timeout.Token);
                    if (received.MessageType == WebSocketMessageType.Close) throw new IOException("Test Chrome closed CDP.");
                    message.Write(buffer, 0, received.Count);
                } while (!received.EndOfMessage);
                using var response = JsonDocument.Parse(message.ToArray());
                if (!response.RootElement.TryGetProperty("id", out var responseId) || responseId.GetInt32() != id) continue;
                Assert.False(response.RootElement.TryGetProperty("error", out _), response.RootElement.GetRawText());
                return response.RootElement.GetProperty("result").Clone();
            }
        }

        public async Task CloseAsync()
        {
            if (closed) return;
            closed = true;
            try { await CommandAsync("Browser.close"); }
            catch (Exception error) when (error is IOException or WebSocketException) { }
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (job.IsRunning && DateTime.UtcNow < deadline) await Task.Delay(50);
            Assert.False(job.IsRunning, "Test Chrome did not exit gracefully and flush NetLog.");
        }

        public async ValueTask DisposeAsync()
        {
            try { await CloseAsync(); }
            finally { socket.Dispose(); job.Dispose(); }
        }
    }
}
