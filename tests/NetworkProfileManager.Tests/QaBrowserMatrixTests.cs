using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text;
using NetworkProfileManager.Core.Models;
using NetworkProfileManager.Infrastructure.BrowserAdapters;
using NetworkProfileManager.Infrastructure.Services;
using NetworkProfileManager.Infrastructure.SessionProviders;
using Xunit;

namespace NetworkProfileManager.Tests;

[CollectionDefinition("QA browser lifecycle", DisableParallelization = true)]
public class QaBrowserCollection { }

[Collection("QA browser lifecycle")]
public class QaBrowserMatrixTests
{
    [Fact] public async Task PreexistingUnmanagedQaBrowserSurvivesManagerStopBoth()
    {
        using var folder = new TestFolder(); await using var gateway = new MockProxy { TlsEcho = true };
        await using var externalBridge = new NetworkProfileManager.Infrastructure.ProxyAdapters.LocalProxyBridge(gateway.Configuration, new()); externalBridge.Start();
        var info = new ChromiumBrowserAdapter().Prepare(new("B", "External QA", BrowserKind.Chrome), BrowserProfileService.FindExecutable(BrowserKind.Chrome)!,
            Path.Combine(folder.Root, "outside-manager"), externalBridge.Port, "http://203.0.113.42/external");
        using var external = Headless(info);
        await using var manager = new BrowserLauncherService(new(folder.Paths), new(), new StandardSessionProvider(), Headless);
        await manager.StartAsync(new("A", "Owned", BrowserKind.Chrome), gateway.Configuration, QaProtocolTests.Settings(), CancellationToken.None);
        var owned = CaptureProcesses(manager, "A"); await manager.StopBothAsync(); await AssertExited(owned);
        Assert.True(external.IsRunning); await external.CloseGracefullyAsync();
    }
    [Theory]
    [InlineData(BrowserKind.Chrome, BrowserKind.Firefox)]
    [InlineData(BrowserKind.Chrome, BrowserKind.Edge)]
    [InlineData(BrowserKind.Chrome, BrowserKind.CocCoc)]
    [InlineData(BrowserKind.Firefox, BrowserKind.Edge)]
    [InlineData(BrowserKind.Firefox, BrowserKind.CocCoc)]
    [InlineData(BrowserKind.Edge, BrowserKind.CocCoc)]
    [InlineData(BrowserKind.Chrome, BrowserKind.Chrome)]
    [InlineData(BrowserKind.Firefox, BrowserKind.Firefox)]
    [InlineData(BrowserKind.Edge, BrowserKind.Edge)]
    [InlineData(BrowserKind.CocCoc, BrowserKind.CocCoc)]
    [Trait("Category", "WindowsBrowser")]
    public async Task PairLaunchStopARestartAAndStopBoth(BrowserKind browserA, BrowserKind browserB)
    {
        using var folder = new TestFolder(); await using var gateway = new MockProxy { TlsEcho = true };
        var profiles = new BrowserProfileService(folder.Paths);
        Assert.True(File.Exists(BrowserProfileService.FindExecutable(browserA))); Assert.True(File.Exists(BrowserProfileService.FindExecutable(browserB)));
        var a = new BrowserProfile("A", "QA A", browserA); var b = new BrowserProfile("B", "QA B", browserB);
        Assert.NotEqual(profiles.ProfileDirectory(a), profiles.ProfileDirectory(b));
        await using var manager = new BrowserLauncherService(profiles, new(), new StandardSessionProvider(), DesktopMinimized);
        await manager.StartAsync(a, gateway.Configuration, QaProtocolTests.Settings(), CancellationToken.None);
        await manager.StartAsync(b, gateway.Configuration, QaProtocolTests.Settings(), CancellationToken.None);
        await WaitForBrowserRequest(gateway, "A"); await WaitForBrowserRequest(gateway, "B");
        var portA = manager.GetPort("A")!.Value; var portB = manager.GetPort("B")!.Value; Assert.NotEqual(portA, portB);
        var ownedA = CaptureProcesses(manager, "A"); var ownedB = CaptureProcesses(manager, "B");
        await manager.StopAsync("A"); await AssertExited(ownedA); await AssertPortClosed(portA);
        Assert.True(manager.IsRunning("B")); Assert.Equal(portB, manager.GetPort("B"));
        var resultB = await QaProtocolTests.Checker(gateway).CheckAsync(QaProtocolTests.Settings(), "B", "QA B bridge", portB, CancellationToken.None);
        Assert.True(resultB.Success, resultB.Error);
        var requestsA = gateway.Targets.Count(t => t.Contains("/proof-A"));
        var marker = Path.Combine(profiles.ProfileDirectory(a), "qa-preserved-marker.txt"); await File.WriteAllTextAsync(marker, "keep");
        await manager.StartAsync(a, gateway.Configuration, QaProtocolTests.Settings(), CancellationToken.None);
        await WaitForBrowserRequest(gateway, "A", requestsA + 1); Assert.Equal("keep", await File.ReadAllTextAsync(marker));
        var restartedA = CaptureProcesses(manager, "A"); var restartPort = manager.GetPort("A")!.Value;
        await manager.StopBothAsync(); await AssertExited(restartedA); await AssertExited(ownedB);
        await AssertPortClosed(restartPort); await AssertPortClosed(portB);
        Assert.False(manager.IsRunning("A")); Assert.False(manager.IsRunning("B"));
    }
    [Fact]
    [Trait("Category", "Stress")]
    public Task TwentyDualBrowserCyclesAndFiftyIpChecksRecordResourceSamples() => RunResourceCycles(BrowserKind.Edge, BrowserKind.Edge, "performance-edge.json");
    [Fact]
    [Trait("Category", "Stress")]
    public Task TwentyChromeFirefoxCyclesAndFiftyChecksRecordResources() => RunResourceCycles(BrowserKind.Chrome, BrowserKind.Firefox, "performance-chrome-firefox.json");
    private static async Task RunResourceCycles(BrowserKind kindA, BrowserKind kindB, string evidenceFile)
    {
        using var folder = new TestFolder(); await using var gateway = new MockProxy { TlsEcho = true, Ips = ["203.0.113.7", "203.0.113.8"] };
        await using var manager = new BrowserLauncherService(new(folder.Paths), new(), new StandardSessionProvider(), DesktopMinimized);
        var samples = new List<object>(); samples.Add(Sample("baseline", manager)); var assignedPorts = new HashSet<int>();
        var a = new BrowserProfile("A", "Stress A", kindA); var b = new BrowserProfile("B", "Stress B", kindB);
        var check = QaProtocolTests.Checker(gateway); var timer = Stopwatch.StartNew();
        var completed = 0; var ipChecks = 0;
        try
        {
        for (var cycle = 0; cycle < 20; cycle++)
        {
            var aBefore = gateway.Targets.Count(t => t.Contains("/proof-A")); var bBefore = gateway.Targets.Count(t => t.Contains("/proof-B"));
            await manager.StartAsync(a, gateway.Configuration, QaProtocolTests.Settings(), CancellationToken.None);
            await manager.StartAsync(b, gateway.Configuration, QaProtocolTests.Settings(), CancellationToken.None);
            QaEvidence.Write("logs/stress-progress.json", new { Cycle = cycle + 1, ExpectedA = aBefore + 1, ExpectedB = bBefore + 1,
                ActualA = gateway.Targets.Count(t => t.Contains("/proof-A")), ActualB = gateway.Targets.Count(t => t.Contains("/proof-B")), ARunning = manager.IsRunning("A"), BRunning = manager.IsRunning("B"), PortA = manager.GetPort("A"), PortB = manager.GetPort("B") });
            samples.Add(Sample($"started-cycle-{cycle + 1}", manager));
            await WaitForBrowserRequest(gateway, "A", aBefore + 1); await WaitForBrowserRequest(gateway, "B", bBefore + 1);
            assignedPorts.Add(manager.GetPort("A")!.Value); assignedPorts.Add(manager.GetPort("B")!.Value);
            var owned = CaptureProcesses(manager, "A").Concat(CaptureProcesses(manager, "B")).ToArray();
            if (cycle is 0 or 9 or 19) samples.Add(Sample($"during-cycle-{cycle + 1}", manager));
            if (cycle == 0)
                for (var i = 0; i < 50; i++)
                {
                    var id = i % 2 == 0 ? "A" : "B";
                    var result = await check.CheckAsync(QaProtocolTests.Settings(), id, "stress-loopback", manager.GetPort(id), CancellationToken.None);
                    Assert.True(result.Success, result.Error);
                    ipChecks++;
                }
            await manager.StopBothAsync(); await AssertExited(owned);
            samples.Add(Sample($"after-cycle-{cycle + 1}", manager));
            completed++;
        }
        foreach (var port in assignedPorts) await AssertPortClosed(port);
        }
        finally
        {
            var owned = CaptureProcesses(manager, "A").Concat(CaptureProcesses(manager, "B")).ToArray();
            await manager.StopBothAsync(); await AssertExited(owned);
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); samples.Add(Sample("after-cleanup-and-gc", manager));
            QaEvidence.Write("logs/" + evidenceFile, new { RequestedCycles = 20, CompletedCycles = completed, IpChecks = ipChecks,
                BrowserA = kindA.ToString(), BrowserB = kindB.ToString(), Status = completed == 20 && ipChecks == 50 ? "PASS" : "FAIL", ElapsedMs = timer.ElapsedMilliseconds, Samples = samples });
        }
    }
    [Fact] public async Task DuplicateStartStopWhileStoppingAndCrashAreContained()
    {
        using var folder = new TestFolder(); await using var mock = new MockProxy { TlsEcho = true };
        await using var manager = new BrowserLauncherService(new(folder.Paths), new(), new StandardSessionProvider(), Headless);
        var a = new BrowserProfile("A", "Race A", BrowserKind.Edge); var settings = QaProtocolTests.Settings();
        await manager.StopBothAsync();
        var first = manager.StartAsync(a, mock.Configuration, settings, CancellationToken.None);
        var duplicate = manager.StartAsync(a, mock.Configuration, settings, CancellationToken.None);
        await first; await Assert.ThrowsAsync<InvalidOperationException>(() => duplicate);
        await WaitForBrowserRequest(mock, "A");
        var stop = manager.StopAsync("A"); var restart = manager.StartAsync(a, mock.Configuration, settings, CancellationToken.None);
        await Task.WhenAll(stop, restart); Assert.True(manager.IsRunning("A"));
        var owned = CaptureProcesses(manager, "A"); var port = manager.GetPort("A")!.Value;
        foreach (var process in owned) try { if (!process.HasExited) process.Kill(); } catch (InvalidOperationException) { }
        var deadline = DateTime.UtcNow.AddSeconds(8); while (manager.IsRunning("A") && DateTime.UtcNow < deadline) await Task.Delay(100);
        Assert.False(manager.IsRunning("A")); await AssertExited(owned); await AssertPortClosed(port);
    }
    [Fact] public async Task InvalidExecutableAndLockedProfileCannotLeakManagedSession()
    {
        using var folder = new TestFolder(); await using var mock = new MockProxy();
        var profiles = new BrowserProfileService(folder.Paths);
        await using var manager = new BrowserLauncherService(profiles, new(), new StandardSessionProvider());
        await Assert.ThrowsAsync<FileNotFoundException>(() => manager.StartAsync(new("A", "Missing", BrowserKind.Edge, Path.Combine(folder.Root, "not-found.exe")), mock.Configuration, QaProtocolTests.Settings(), CancellationToken.None));
        Assert.Null(manager.GetPort("A")); Assert.False(manager.IsRunning("A"));
        var a = new BrowserProfile("A", "Locked", BrowserKind.Firefox); var directory = profiles.ProfileDirectory(a); Directory.CreateDirectory(directory);
        using var lease = new FileStream(Path.Combine(directory, "manager.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        await Assert.ThrowsAnyAsync<IOException>(() => manager.StartAsync(a, mock.Configuration, QaProtocolTests.Settings(), CancellationToken.None));
        Assert.Null(manager.GetPort("A")); Assert.False(manager.IsRunning("A"));
        Assert.Throws<FileNotFoundException>(() => new BrowserProfileService(folder.Paths, _ => null).Prepare(new("B", "Uninstalled", BrowserKind.Chrome), 12345, "https://echo.test"));
    }
    internal static BrowserProcessJob Headless(ProcessStartInfo info) => PrepareProcess(info, true);
    private static BrowserProcessJob DesktopMinimized(ProcessStartInfo info) => PrepareProcess(info, false);
    private static BrowserProcessJob PrepareProcess(ProcessStartInfo info, bool headless)
    {
        var firefox = info.ArgumentList.Contains("-profile");
        if (headless) info.ArgumentList.Insert(0, firefox ? "-headless" : "--headless=new");
        else info.WindowStyle = ProcessWindowStyle.Minimized;
        var id = info.ArgumentList.Any(arg => arg.Contains(Path.DirectorySeparatorChar + "A" + Path.DirectorySeparatorChar)) ? "A" : "B";
        // TEST-NET literal avoids HTTPS-first domain upgrades and browser private-network prompts.
        // The mock gateway serves the response; no server exists at this reserved destination.
        info.ArgumentList[^1] = $"http://203.0.113.42/proof-{id}?run={Guid.NewGuid():N}";
        QaEvidence.Write($"logs/browser-launch-{Guid.NewGuid():N}.json", new { info.FileName, Arguments = info.ArgumentList.ToArray(), Id = id });
        return BrowserProcessJob.Start(info);
    }
    private static async Task CaptureDebugTargets(BrowserProfileService profiles, BrowserProfile profile, int cycle)
    {
        var file = Path.Combine(profiles.ProfileDirectory(profile), "DevToolsActivePort");
        try
        {
            await Task.Delay(2000);
            if (!File.Exists(file)) return;
            var lines = await File.ReadAllLinesAsync(file); if (!int.TryParse(lines[0], out var port)) return;
            using var client = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(2) };
            var text = await client.GetStringAsync($"http://127.0.0.1:{port}/json/list");
            using var targets = JsonDocument.Parse(text);
            var page = targets.RootElement.EnumerateArray().First(t => t.GetProperty("type").GetString() == "page");
            using var socket = new ClientWebSocket(); using var limit = new CancellationTokenSource(2000);
            await socket.ConnectAsync(new Uri(page.GetProperty("webSocketDebuggerUrl").GetString()!), limit.Token);
            await socket.SendAsync(Encoding.UTF8.GetBytes("{\"id\":1,\"method\":\"Runtime.evaluate\",\"params\":{\"expression\":\"document.body.innerText\",\"returnByValue\":true}}"), WebSocketMessageType.Text, true, limit.Token);
            var buffer = new byte[32768]; var response = await socket.ReceiveAsync(buffer, limit.Token);
            QaEvidence.Write($"logs/browser-debug-cycle-{cycle}.json", new { Cycle = cycle, Targets = text, Body = Encoding.UTF8.GetString(buffer, 0, response.Count) });
        }
        catch (Exception ex) { QaEvidence.Write($"logs/browser-debug-cycle-{cycle}.json", new { Error = ex.GetType().Name }); }
    }
    private static async Task WaitForBrowserRequest(MockProxy mock, string id, int count = 1)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (mock.Targets.Count(t => t.Contains($"/proof-{id}")) < count && DateTime.UtcNow < deadline) await Task.Delay(100);
        if (mock.Targets.Count(t => t.Contains($"/proof-{id}")) < count) QaEvidence.Write("logs/browser-route-failure.json", new { Id = id, Expected = count, Targets = mock.Targets.ToArray(), Errors = mock.Errors.Take(5).ToArray() });
        Assert.True(mock.Targets.Count(t => t.Contains($"/proof-{id}")) >= count,
            $"Browser {id} did not send its request through the mock gateway. Targets: {string.Join("; ", mock.Targets.Take(20))}");
    }
    internal static Process[] CaptureProcesses(BrowserLauncherService manager, string id) => manager.GetProcessIds(id).Select(pid =>
    { try { return Process.GetProcessById(pid); } catch (ArgumentException) { return null; } }).Where(p => p is not null).Cast<Process>().ToArray();
    internal static async Task AssertExited(IEnumerable<Process> processes)
    { foreach (var process in processes) using (process) await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
    private static async Task AssertPortClosed(int port)
    {
        using var socket = new TcpClient(); await Assert.ThrowsAsync<SocketException>(() => socket.ConnectAsync(IPAddress.Loopback, port));
    }
    private static object Sample(string phase, BrowserLauncherService manager)
    {
        using var process = Process.GetCurrentProcess(); process.Refresh();
        var ids = manager.GetProcessIds("A").Concat(manager.GetProcessIds("B")).ToArray(); long childMemory = 0;
        foreach (var id in ids) try { using var child = Process.GetProcessById(id); childMemory += child.PrivateMemorySize64; } catch (ArgumentException) { }
        return new { Phase = phase, Timestamp = DateTimeOffset.UtcNow, PrivateBytes = process.PrivateMemorySize64,
            WorkingSet = process.WorkingSet64, ManagedBytes = GC.GetTotalMemory(false), Handles = process.HandleCount,
            Threads = process.Threads.Count, CpuMs = process.TotalProcessorTime.TotalMilliseconds,
            ManagedChildCount = ids.Length, ChildPrivateBytes = childMemory,
            ManagedListeners = new[] { manager.GetPort("A"), manager.GetPort("B") }.Count(p => p.HasValue),
            TotalHostListeners = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Length };
    }
}
