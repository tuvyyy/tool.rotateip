using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.Data.Sqlite;
using NetworkProfileManager.App.Services;
using NetworkProfileManager.App.ViewModels;
using NetworkProfileManager.Core.Models;
using NetworkProfileManager.Core.Services;
using NetworkProfileManager.Infrastructure.BrowserAdapters;
using NetworkProfileManager.Infrastructure.ProxyAdapters;
using NetworkProfileManager.Infrastructure.Services;
using NetworkProfileManager.Infrastructure.SessionProviders;
using NetworkProfileManager.Infrastructure.Storage;
using Xunit;

namespace NetworkProfileManager.Tests;

[Collection("QA browser lifecycle")]
public class QaRegressionTests
{
    [Fact] public async Task DisposedLauncherCannotCreateAnotherManagedProcess()
    {
        using var folder = new TestFolder(); await using var mock = new MockProxy();
        var launcher = new BrowserLauncherService(new(folder.Paths), new(), new StandardSessionProvider()); await launcher.DisposeAsync();
        try
        {
            await Assert.ThrowsAsync<ObjectDisposedException>(() => launcher.StartAsync(new("A", "A", BrowserKind.Edge,
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "whoami.exe")), mock.Configuration,
                new() { EchoEndpoint = "https://echo.test", TimeoutSeconds = 3 }, CancellationToken.None));
        }
        finally { await launcher.StopBothAsync(); }
    }
    [Fact] public async Task ConnectCancellationRetainsCancellationSemantics()
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new UpstreamProxyConnector().ConnectGatewayAsync(new() { Host = "127.0.0.1", Port = 1 }, cancellation.Token));
    }
    [Fact] public async Task ConnectHalfCloseStillReceivesServerResponse()
    {
        await using var upstream = new ScriptedProxy(async (client, ct) =>
        {
            var stream = client.GetStream(); await ProxyWire.ReadHeaderAsync(stream, ct);
            await ProxyWire.WriteAsync(stream, "HTTP/1.1 200 OK\r\n\r\n", ct);
            var input = new byte[128]; while (await stream.ReadAsync(input, ct) != 0) { }
            await ProxyWire.WriteAsync(stream, "response-after-half-close", ct); client.Client.Shutdown(SocketShutdown.Send);
        });
        await using var bridge = new LocalProxyBridge(new() { Host = "127.0.0.1", Port = upstream.Port }, new()); bridge.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)); using var local = new TcpClient();
        await local.ConnectAsync(IPAddress.Loopback, bridge.Port, timeout.Token);
        await ProxyWire.WriteAsync(local.GetStream(), "CONNECT echo.test:443 HTTP/1.1\r\nHost: echo.test:443\r\n\r\n", timeout.Token);
        Assert.StartsWith("HTTP/1.1 200", await ProxyWire.ReadHeaderAsync(local.GetStream(), timeout.Token));
        var downstream = local.GetStream();
        await ProxyWire.WriteAsync(downstream, "request", timeout.Token); local.Client.Shutdown(SocketShutdown.Send);
        using var reader = new StreamReader(downstream);
        Assert.Equal("response-after-half-close", await reader.ReadToEndAsync(timeout.Token));
    }
    [Fact] public async Task LockedDatabaseDoesNotBlockCallingThread()
    {
        using var folder = new TestFolder(); var store = new SqliteSettingsStore(folder.Paths); await store.InitializeAsync();
        using var blocker = new SqliteConnection($"Data Source={folder.Paths.Database}"); blocker.Open(); using var transaction = blocker.BeginTransaction();
        var released = Task.Run(async () => { await Task.Delay(700); transaction.Rollback(); });
        var watch = Stopwatch.StartNew(); var write = store.SaveSettingsAsync(new()); var invocationMs = watch.ElapsedMilliseconds;
        await released; await write;
        Assert.True(invocationMs < 200, $"SaveSettingsAsync blocked its caller for {invocationMs} ms.");
    }
    [Fact] public void StoredProfileIdentifiersCannotAlias()
    {
        var settings = new AppSettings { ProfileA = new("B", "Alias A", BrowserKind.Chrome) };
        Assert.Throws<ArgumentException>(settings.Validate);
    }
    [Fact] public async Task InvalidSettingsCannotPartiallySaveNewProxy()
    {
        using var folder = new TestFolder(); var store = new SqliteSettingsStore(folder.Paths); await store.InitializeAsync();
        var original = new ProxyConfiguration { Host = "original.test", Port = 8000 };
        await store.SaveProxyAsync(original);
        await using var launcher = new BrowserLauncherService(new(folder.Paths), new(), new StandardSessionProvider());
        var vm = new MainViewModel(store, new StaticNetwork(), new(new(), new StandardSessionProvider()), launcher, new DesktopService(), folder.Paths);
        await vm.InitializeAsync(); vm.Host = "replacement.test"; vm.TimeoutSeconds = "invalid";
        await vm.SaveProxyCommand.ExecuteAsync(null);
        Assert.Equal("LỖI", vm.StatusKind); Assert.Equal(original, await store.LoadProxyAsync());
    }
    [Fact] public async Task CorruptSettingsStillAllowUserToRecoverInUi()
    {
        using var folder = new TestFolder(); var store = new SqliteSettingsStore(folder.Paths); await store.InitializeAsync();
        using (var db = new SqliteConnection($"Data Source={folder.Paths.Database}"))
        { db.Open(); using var command = db.CreateCommand(); command.CommandText = "INSERT INTO config VALUES ('settings',x'7b')"; command.ExecuteNonQuery(); }
        await using var launcher = new BrowserLauncherService(new(folder.Paths), new(), new StandardSessionProvider());
        var vm = new MainViewModel(store, new StaticNetwork(), new(new(), new StandardSessionProvider()), launcher, new DesktopService(), folder.Paths);
        await vm.InitializeAsync(); Assert.Equal(2, vm.Cards.Count);
        Assert.Contains(vm.StatusKind, new[] { "CẢNH BÁO", "LỖI" });
    }
    internal sealed class StaticNetwork : INetworkInformationService
    {
        public Task<NetworkSnapshot> GetAsync(CancellationToken ct) => Task.FromResult(new NetworkSnapshot("QA Wi-Fi", "QA adapter", "10.0.0.2", "02:00:00:00:00:01", "10.0.0.1", "10.0.0.1", "Đang bật"));
    }
}
