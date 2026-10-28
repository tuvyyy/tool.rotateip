using System.Collections.Concurrent;
using System.Diagnostics;
using NetworkProfileManager.Core.Models;
using NetworkProfileManager.Core.Services;
using NetworkProfileManager.Infrastructure.BrowserAdapters;
using NetworkProfileManager.Infrastructure.ProxyAdapters;

namespace NetworkProfileManager.Infrastructure.Services;

public sealed class BrowserLauncherService(BrowserProfileService profiles, UpstreamProxyConnector connector, IProxySessionProvider sessions,
    Func<ProcessStartInfo, BrowserProcessJob>? processFactory = null) : IAsyncDisposable
{
    private sealed record RunningBrowser(BrowserProcessJob Job, LocalProxyBridge Bridge, CancellationTokenSource Monitor, ProxyConfiguration Configuration, FileStream ProfileLease);
    private readonly ConcurrentDictionary<string, RunningBrowser> running = new();
    private readonly SemaphoreSlim gate = new(1);
    private bool disposed;
    public event Action<string, string>? StateChanged;
    public event Action<string, string>? ProxyError;
    public int? GetPort(string id) => running.TryGetValue(id, out var browser) ? browser.Bridge.Port : null;
    public bool IsRunning(string id) => running.ContainsKey(id);
    public IReadOnlyList<int> GetProcessIds(string id) => running.TryGetValue(id, out var browser) ? browser.Job.GetProcessIds() : [];
    public async Task StartAsync(BrowserProfile profile, ProxyConfiguration proxy, AppSettings settings, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (running.ContainsKey(profile.Id)) throw new InvalidOperationException($"Profile {profile.Id} đang chạy.");
            var resolved = sessions.Resolve(proxy, profile.Id); settings.Validate();
            if (running.Values.Any(b => b.Configuration != resolved)) throw new InvalidOperationException("Dừng cả hai profile trước khi thay đổi gateway dùng chung.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(settings.TimeoutSeconds));
            var target = new Uri(settings.EchoEndpoint);
            // Probe upstream authentication and CONNECT before creating any browser process.
            using (await connector.OpenTunnelAsync(resolved, target.IdnHost, target.Port, timeout.Token)) { }
            var bridge = new LocalProxyBridge(resolved, connector, settings.TimeoutSeconds);
            FileStream? lease = null;
            try
            {
                bridge.Start(profile.Id == "A" ? 17881 : 17882);
                bridge.ConnectionFailed += message => ProxyError?.Invoke(profile.Id, message);
                var directory = profiles.ProfileDirectory(profile); Directory.CreateDirectory(directory);
                lease = new FileStream(Path.Combine(directory, "manager.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                var info = profiles.Prepare(profile, bridge.Port, settings.EchoEndpoint);
                ct.ThrowIfCancellationRequested();
                var job = (processFactory ?? BrowserProcessJob.Start)(info);
                var browser = new RunningBrowser(job, bridge, new CancellationTokenSource(), resolved, lease);
                if (!running.TryAdd(profile.Id, browser)) { job.Dispose(); browser.Monitor.Dispose(); throw new InvalidOperationException("Profile đang được dùng."); }
                StateChanged?.Invoke(profile.Id, $"Đang chạy • 127.0.0.1:{bridge.Port}");
                _ = MonitorAsync(profile.Id, browser, browser.Monitor.Token);
                lease = null; // Ownership transferred to the running session.
            }
            catch { lease?.Dispose(); await bridge.DisposeAsync(); throw; }
        }
        finally { gate.Release(); }
    }
    private async Task MonitorAsync(string id, RunningBrowser browser, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested && browser.Job.IsRunning) await Task.Delay(750, token);
            if (!token.IsCancellationRequested)
            {
                await gate.WaitAsync(token);
                try { if (running.TryGetValue(id, out var current) && ReferenceEquals(current, browser)) await StopCoreAsync(id); }
                finally { gate.Release(); }
            }
        }
        catch (OperationCanceledException) { }
    }
    public async Task StopAsync(string id, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            await StopCoreAsync(id);
        }
        finally { gate.Release(); }
    }
    private async Task StopCoreAsync(string id)
    {
        if (!running.TryRemove(id, out var browser)) return;
        await browser.Monitor.CancelAsync();
        try { await browser.Job.CloseGracefullyAsync(); }
        finally
        {
            browser.Job.Dispose(); await browser.Bridge.DisposeAsync();
            browser.ProfileLease.Dispose(); browser.Monitor.Dispose();
        }
        StateChanged?.Invoke(id, "Đã dừng • listener đã đóng");
    }
    public async Task StopBothAsync()
    {
        await gate.WaitAsync();
        try { await StopCoreAsync("A"); await StopCoreAsync("B"); }
        finally { gate.Release(); }
    }
    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync();
        try { disposed = true; await StopCoreAsync("A"); await StopCoreAsync("B"); }
        finally { gate.Release(); }
    }
}
