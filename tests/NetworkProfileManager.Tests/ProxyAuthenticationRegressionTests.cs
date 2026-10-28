using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using NetworkProfileManager.App.Services;
using NetworkProfileManager.App.ViewModels;
using NetworkProfileManager.App.Views;
using NetworkProfileManager.Infrastructure.ProxyAdapters;
using NetworkProfileManager.Infrastructure.Services;
using NetworkProfileManager.Infrastructure.SessionProviders;
using NetworkProfileManager.Infrastructure.Storage;
using Xunit;

namespace NetworkProfileManager.Tests;

[Collection("QA browser lifecycle")]
public class ProxyAuthenticationRegressionTests
{
    [Theory]
    [InlineData("http")]
    [InlineData("https")]
    public async Task SuccessfulWebProxyAndBridgeAuthenticateAgainstSameUpstream(string scheme)
    {
        await using var upstream = new MockProxy { TlsEcho = true };
        var config = upstream.Configuration;
        using var directHandler = new HttpClientHandler {
            Proxy = new WebProxy($"http://127.0.0.1:{upstream.Port}") {
                Credentials = new NetworkCredential(config.Username, config.Password)
            }, UseProxy = true,
            ServerCertificateCustomValidationCallback = (_, cert, _, _) => cert?.GetCertHashString() == upstream.Certificate.GetCertHashString()
        };
        using var direct = new HttpClient(directHandler) { Timeout = TimeSpan.FromSeconds(5) };
        var expected = await direct.GetStringAsync($"{scheme}://echo.test/");
        await using var bridge = new LocalProxyBridge(config, new()); bridge.Start();
        using var bridgeHandler = new HttpClientHandler {
            Proxy = new WebProxy($"http://127.0.0.1:{bridge.Port}"), UseProxy = true,
            ServerCertificateCustomValidationCallback = (_, cert, _, _) => cert?.GetCertHashString() == upstream.Certificate.GetCertHashString()
        };
        using var local = new HttpClient(bridgeHandler) { Timeout = TimeSpan.FromSeconds(5) };
        Assert.Equal(expected, await local.GetStringAsync($"{scheme}://echo.test/"));
        Assert.Null(bridge.LastError);
    }

    [Fact]
    public Task InitiallyEmptyPasswordIsSavedReloadedAndUsedByFreshGatewayBridge() => OnDispatcherAsync(async () =>
    {
        using var folder = new TestFolder();
        await using var upstream = new MockProxy { TlsEcho = true };
        var store = new SqliteSettingsStore(folder.Paths); await store.InitializeAsync();
        await store.SaveProxyAsync(upstream.Configuration with { Password = "" });
        await using var launcher = new BrowserLauncherService(new(folder.Paths), new(), new StandardSessionProvider());
        var checker = QaProtocolTests.Checker(upstream);
        await using var oldBridge = new LocalProxyBridge((await store.LoadProxyAsync())!, new()); oldBridge.Start();
        var oldPort = oldBridge.Port;
        var oldResult = await checker.CheckAsync(new() { EchoEndpoint = "https://echo.test", TimeoutSeconds = 5 },
            "Gateway", "fixture", oldPort, CancellationToken.None);
        Assert.False(oldResult.Success); Assert.Contains("407", oldBridge.LastError);
        var vm = new MainViewModel(store, new QaRegressionTests.StaticNetwork(), checker, launcher, new DesktopService(), folder.Paths);
        await vm.InitializeAsync();
        vm.EchoEndpoint = "https://echo.test"; vm.TimeoutSeconds = "5";
        var box = new PasswordBox();
        BindingOperations.SetBinding(box, PasswordBinding.PasswordProperty, new Binding(nameof(vm.Password)) {
            Source = vm, Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
        });
        Assert.True(vm.Password.Length == 0);
        box.Password = upstream.Configuration.Password; // Real PasswordBox edit, not assigning the view model.
        Assert.True(vm.Password == upstream.Configuration.Password, "PasswordBox edit did not reach the view model.");
        vm.IsPasswordVisible = true;
        await vm.SaveProxyCommand.ExecuteAsync(null);
        Assert.NotEqual("LỖI", vm.StatusKind);
        var reloaded = await new SqliteSettingsStore(folder.Paths).LoadProxyAsync();
        Assert.True(reloaded == upstream.Configuration, "DPAPI reload differs from the edited configuration.");
        Assert.False(vm.IsPasswordVisible);
        await oldBridge.DisposeAsync();
        using (var closed = new TcpClient())
            await Assert.ThrowsAsync<SocketException>(() => closed.ConnectAsync(IPAddress.Loopback, oldPort));
        var restarted = new MainViewModel(new SqliteSettingsStore(folder.Paths), new QaRegressionTests.StaticNetwork(),
            checker, launcher, new DesktopService(), folder.Paths);
        await restarted.InitializeAsync();
        Assert.True(restarted.Password == upstream.Configuration.Password, "Reloaded UI credentials differ from saved input.");
        await restarted.TestGatewayCommand.ExecuteAsync(null);
        Assert.True(restarted.Results[0].Success, restarted.Results[0].Error);
        // Programmatic changes must continue reaching an already edited box.
        vm.Password = "replacement-fixture";
        Assert.True(box.Password == vm.Password, "Password binding was lost after the edit.");
        box.Password = "second-fixture";
        Assert.True(vm.Password == box.Password);
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ConnectAndForwardingSendExactlyOneSavedAuthorization(bool connect)
    {
        var valid = false;
        await using var upstream = new ScriptedProxy(async (client, ct) =>
        {
            var header = await ProxyWire.ReadHeaderAsync(client.GetStream(), ct);
            var auth = header.Split("\r\n").Where(l => l.StartsWith("Proxy-Authorization:", StringComparison.OrdinalIgnoreCase)).ToArray();
            // Independent expectation: do not reuse the production Authorization helper.
            var expected = "Proxy-Authorization: Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("fixture-user:fixture-password"));
            valid = auth.Length == 1 && auth[0] == expected &&
                header.StartsWith(connect ? "CONNECT echo.test:443 HTTP/1.1\r\n" : "GET http://echo.test/path HTTP/1.1\r\n");
            await ProxyWire.WriteAsync(client.GetStream(), "HTTP/1.1 200 OK\r\nContent-Length: 0\r\n\r\n", ct);
        });
        await using var bridge = new LocalProxyBridge(new() {
            Host = "127.0.0.1", Port = upstream.Port, Username = "fixture-user", Password = "fixture-password"
        }, new()); bridge.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var downstream = new TcpClient(); await downstream.ConnectAsync(IPAddress.Loopback, bridge.Port, timeout.Token);
        var request = connect ? "CONNECT echo.test:443" : "GET http://echo.test/path";
        await ProxyWire.WriteAsync(downstream.GetStream(), request + " HTTP/1.1\r\nHost: echo.test\r\nProxy-Authorization: Basic Zm9yZ2Vk\r\nConnection: close\r\n\r\n", timeout.Token);
        Assert.StartsWith("HTTP/1.1 200", await ProxyWire.ReadHeaderAsync(downstream.GetStream(), timeout.Token));
        Assert.True(valid, "Upstream request did not carry exactly the saved credentials.");
    }

    private static Task OnDispatcherAsync(Func<Task> action)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            var dispatcher = Dispatcher.CurrentDispatcher;
            _ = dispatcher.InvokeAsync(async () =>
            {
                try { await action(); done.TrySetResult(); }
                catch (Exception error) { done.TrySetException(error); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return done.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }
}
