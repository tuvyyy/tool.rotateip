using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using NetworkProfileManager.Core.Models;
using NetworkProfileManager.Infrastructure.ProxyAdapters;
using NetworkProfileManager.Infrastructure.Storage;

// Explicit, read-only live diagnostic. Never logs credentials, headers, response bodies or exit IPs.
internal static class ProxyAuthDiagnostics
{
    public static async Task RunAsync()
    {
        var store = new SqliteSettingsStore(new AppPaths());
        var proxy = await store.LoadProxyAsync() ?? throw new InvalidOperationException("No saved proxy.");
        var settings = await store.LoadSettingsAsync();
        Console.WriteLine(JsonSerializer.Serialize(new {
            SavedCredentialsReloadMatch = proxy == await store.LoadProxyAsync(),
            UsernamePresent = proxy.Username.Length > 0, PasswordPresent = proxy.Password.Length > 0,
            CredentialsAscii = (proxy.Username + proxy.Password).All(c => c < 128),
            UsernameHasBackslash = proxy.Username.Contains('\\'),
            CredentialsHaveEdgeWhitespace = proxy.Username != proxy.Username.Trim() || proxy.Password != proxy.Password.Trim(),
            SavedProtocol = proxy.Protocol.ToString(),
            Recent407Results = (await store.GetResultsAsync()).Count(r => r.Error?.Contains("407") == true)
        }));
        var plain = JsonSerializer.SerializeToUtf8Bytes(proxy);
        var encrypted = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);
        var decrypted = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
        Console.WriteLine(JsonSerializer.Serialize(new { DpapiRoundTripMatches = CryptographicOperations.FixedTimeEquals(plain, decrypted) }));
        CryptographicOperations.ZeroMemory(plain); CryptographicOperations.ZeroMemory(decrypted);

        var https = new Uri(settings.EchoEndpoint);
        var http = new UriBuilder(https) { Scheme = "http", Port = 80 }.Uri;
        foreach (var protocol in new[] { ProxyProtocol.Http, ProxyProtocol.Socks5 })
        {
            var config = proxy with { Protocol = protocol, ExpirationDate = null };
            foreach (var target in new[] { https, http })
            {
                using var handler = new HttpClientHandler {
                    Proxy = new WebProxy(new UriBuilder(protocol == ProxyProtocol.Http ? "http" : "socks5", config.Host, config.Port).Uri) {
                        Credentials = new NetworkCredential(config.Username, config.Password)
                    }, UseProxy = true, AllowAutoRedirect = false, UseCookies = false
                };
                await RequestAsync($"WebProxy-{protocol}-{target.Scheme}", handler, target);
                await using var bridge = new LocalProxyBridge(config, new(), 12); bridge.Start();
                using var local = new HttpClientHandler { Proxy = new WebProxy($"http://127.0.0.1:{bridge.Port}"), UseProxy = true, AllowAutoRedirect = false, UseCookies = false };
                await RequestAsync($"Bridge-{protocol}-{target.Scheme}", local, target);
                Console.WriteLine(JsonSerializer.Serialize(new { Route = $"Bridge-{protocol}-{target.Scheme}", Upstream407 = bridge.LastError?.Contains("407") == true }));
            }
        }
    }

    private static async Task RequestAsync(string route, HttpClientHandler handler, Uri target)
    {
        try
        {
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
            using var response = await client.GetAsync(target, HttpCompletionOption.ResponseHeadersRead);
            Console.WriteLine(JsonSerializer.Serialize(new { Route = route, Status = (int)response.StatusCode }));
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            Console.WriteLine(JsonSerializer.Serialize(new { Route = route, ErrorType = ex.GetType().Name,
                HttpError = (ex as HttpRequestException)?.HttpRequestError.ToString(),
                HttpStatus = (int?)(ex as HttpRequestException)?.StatusCode }));
        }
    }
}
