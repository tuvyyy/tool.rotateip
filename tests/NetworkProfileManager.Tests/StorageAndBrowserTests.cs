using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using NetworkProfileManager.Core.Models;
using NetworkProfileManager.Infrastructure.BrowserAdapters;
using NetworkProfileManager.Infrastructure.ProxyAdapters;
using NetworkProfileManager.Infrastructure.Services;
using NetworkProfileManager.Infrastructure.SessionProviders;
using NetworkProfileManager.Infrastructure.Storage;
using Xunit;

namespace NetworkProfileManager.Tests;

public sealed class TestFolder : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "NetworkProfileManager.Tests", Guid.NewGuid().ToString("N"));
    public AppPaths Paths { get; }
    public TestFolder() { Paths = new AppPaths(Root); }
    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        // Antivirus/indexing may briefly retain handles after all owned processes have exited.
        for (var attempt = 0; attempt < 25; attempt++)
        {
            try { if (Directory.Exists(Root)) Directory.Delete(Root, true); return; }
            catch (IOException)
            {
                if (attempt == 24)
                {
                    // A third-party scanner can hold a file after process-exit assertions passed.
                    // Retain only this uniquely generated scratch folder rather than hiding the test outcome.
                    System.Diagnostics.Trace.WriteLine("Scratch cleanup deferred: " + Root); return;
                }
                Thread.Sleep(200);
            }
        }
    }
}
[Collection("QA browser lifecycle")]
public class StorageAndBrowserTests
{
    [Fact] public async Task DpapiRoundTripAndDatabaseDoesNotContainCredentials()
    {
        using var folder = new TestFolder(); var store = new SqliteSettingsStore(folder.Paths); await store.InitializeAsync();
        var proxy = new ProxyConfiguration { Host = "proxy.test", Port = 8080, Username = "sensitive-user-8989", Password = "secret-long-password-92891" };
        await store.SaveProxyAsync(proxy); Assert.Equal(proxy, await store.LoadProxyAsync());
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var file in Directory.GetFiles(folder.Root, "manager.db*"))
        {
            var content = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(file));
            Assert.DoesNotContain(proxy.Password, content); Assert.DoesNotContain(proxy.Username, content);
        }
    }
    [Fact] public async Task SettingsResultsAndLogsSurviveReopen()
    {
        using var folder = new TestFolder(); var store = new SqliteSettingsStore(folder.Paths); await store.InitializeAsync();
        var settings = new AppSettings { LightTheme = true, TimeoutSeconds = 7 };
        await store.SaveSettingsAsync(settings);
        var result = new IpTestResult("B", "203.0.113.1", true, 12, DateTimeOffset.UtcNow, "http://proxy.test:8080", null);
        await store.AddResultAsync(result); await store.AddLogAsync(new(DateTimeOffset.UtcNow, "Info", "check"));
        var reopened = new SqliteSettingsStore(folder.Paths);
        Assert.Equal(settings, await reopened.LoadSettingsAsync()); Assert.Equal(result, Assert.Single(await reopened.GetResultsAsync())); Assert.Single(await reopened.GetLogsAsync());
    }
    [Theory] [InlineData(BrowserKind.Chrome)] [InlineData(BrowserKind.Firefox)] [InlineData(BrowserKind.CocCoc)] [InlineData(BrowserKind.Edge)]
    public void BrowserProfilesHaveDistinctPathsAndNoSecretsInCommand(BrowserKind kind)
    {
        using var folder = new TestFolder(); var service = new BrowserProfileService(folder.Paths);
        var a = new BrowserProfile("A", "A", kind); var b = new BrowserProfile("B", "B", kind);
        Assert.NotEqual(service.ProfileDirectory(a), service.ProfileDirectory(b));
        var info = service.Prepare(a, 17881, "https://api.ipify.org");
        Assert.Contains(service.ProfileDirectory(a), string.Join(" ", info.ArgumentList));
        Assert.DoesNotContain(MockProxy.Password, string.Join(" ", info.ArgumentList));
        if (kind == BrowserKind.Firefox)
        {
            var prefs = File.ReadAllText(Path.Combine(service.ProfileDirectory(a), "user.js"));
            Assert.Contains("network.proxy.failover_direct\", false", prefs); Assert.Contains("17881", prefs);
        }
        else Assert.Contains("--proxy-server=http://127.0.0.1:17881", info.ArgumentList);
    }
    [Theory] [InlineData(BrowserKind.Chrome)] [InlineData(BrowserKind.Edge)] [InlineData(BrowserKind.CocCoc)] [InlineData(BrowserKind.Firefox)]
    [Trait("Category", "WindowsBrowser")]
    public async Task RealTwoBrowserProfilesRouteThroughIndependentBridgesAndCleanup(BrowserKind kind)
    {
        using var folder = new TestFolder(); await using var proxy = new MockProxy();
        var profiles = new BrowserProfileService(folder.Paths); var connector = new UpstreamProxyConnector();
        await using var bridgeA = new LocalProxyBridge(proxy.Configuration, connector); bridgeA.Start();
        await using var bridgeB = new LocalProxyBridge(proxy.Configuration, connector); bridgeB.Start();
        var a = profiles.Prepare(new("A", "A", kind), bridgeA.Port, "http://echo.test/proof-A");
        var b = profiles.Prepare(new("B", "B", kind), bridgeB.Port, "http://echo.test/proof-B");
        a.ArgumentList.Insert(0, kind == BrowserKind.Firefox ? "-headless" : "--headless=new");
        b.ArgumentList.Insert(0, kind == BrowserKind.Firefox ? "-headless" : "--headless=new");
        using var jobA = BrowserProcessJob.Start(a); using var jobB = BrowserProcessJob.Start(b);
        try
        {
            var until = DateTime.UtcNow.AddSeconds(35);
            while (!(proxy.Targets.Any(t => t.Contains("/proof-A")) && proxy.Targets.Any(t => t.Contains("/proof-B"))) && DateTime.UtcNow < until) await Task.Delay(100);
            Assert.True(proxy.Targets.Any(t => t.Contains("/proof-A")), $"{kind} A did not route to mock proxy.");
            Assert.True(proxy.Targets.Any(t => t.Contains("/proof-B")), $"{kind} B did not route to mock proxy.");
            Assert.True(jobA.IsRunning); Assert.True(jobB.IsRunning); Assert.NotEqual(jobA.ProcessId, jobB.ProcessId);
            var processes = jobA.GetProcessIds().Concat(jobB.GetProcessIds()).Select(GetProcess).Where(p => p is not null).Cast<Process>().ToArray();
            jobA.Dispose(); jobB.Dispose();
            foreach (var process in processes) { using (process) await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
        }
        finally
        {
            jobA.Dispose(); jobB.Dispose(); await Task.Delay(500);
        }
    }
    [Fact] public async Task BrowserLauncherRejectsOfflineGatewayBeforeProcessCreation()
    {
        using var folder = new TestFolder(); using var portHolder = new TcpListener(IPAddress.Loopback, 0); portHolder.Start();
        var port = ((IPEndPoint)portHolder.LocalEndpoint).Port; portHolder.Stop();
        await using var launcher = new BrowserLauncherService(new(folder.Paths), new(), new StandardSessionProvider());
        await Assert.ThrowsAsync<IOException>(() => launcher.StartAsync(new("A", "A", BrowserKind.Edge), new() { Host = "127.0.0.1", Port = port }, new() { TimeoutSeconds = 3 }, CancellationToken.None));
        Assert.False(launcher.IsRunning("A")); Assert.Null(launcher.GetPort("A"));
    }
    [Fact] public async Task BrowserLauncherStopsListenerWhenCreatedProcessExits()
    {
        // A short-lived executable exercises the monitor without touching personal browser windows.
        using var folder = new TestFolder(); await using var mock = new MockProxy();
        await using var launcher = new BrowserLauncherService(new(folder.Paths), new(), new StandardSessionProvider());
        var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "whoami.exe");
        await launcher.StartAsync(new("A", "A", BrowserKind.Edge, executable), mock.Configuration, new() { EchoEndpoint = "https://echo.test", TimeoutSeconds = 3 }, CancellationToken.None);
        var port = launcher.GetPort("A");
        var until = DateTime.UtcNow.AddSeconds(5); while (launcher.IsRunning("A") && DateTime.UtcNow < until) await Task.Delay(100);
        Assert.False(launcher.IsRunning("A")); Assert.Null(launcher.GetPort("A"));
        if (port.HasValue) { using var client = new TcpClient(); await Assert.ThrowsAsync<SocketException>(() => client.ConnectAsync(IPAddress.Loopback, port.Value)); }
    }
    [Fact] public async Task ClosingJobKillsOwnedChildProcesses()
    {
        var info = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe")) { WorkingDirectory = Path.GetTempPath() };
        info.ArgumentList.Add("-NoProfile"); info.ArgumentList.Add("-NonInteractive"); info.ArgumentList.Add("-Command");
        info.ArgumentList.Add("$p = Start-Process -FilePath ping.exe -ArgumentList '-n','60','127.0.0.1' -WindowStyle Hidden -PassThru; $p.WaitForExit()");
        using var job = BrowserProcessJob.Start(info); await Task.Delay(300);
        var until = DateTime.UtcNow.AddSeconds(5);
        while (job.GetProcessIds().Count < 2 && DateTime.UtcNow < until) await Task.Delay(100);
        var owned = job.GetProcessIds().Select(GetProcess).Where(p => p is not null).Cast<Process>().ToArray(); Assert.True(owned.Length >= 2, "Job processes: " + string.Join(",", owned.Select(p => p.ProcessName)));
        job.Dispose(); foreach (var process in owned) { using (process) await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
    }
    private static Process? GetProcess(int pid) { try { return Process.GetProcessById(pid); } catch (ArgumentException) { return null; } }
}
