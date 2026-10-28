using NetworkProfileManager.Core.Models;

namespace NetworkProfileManager.Core.Services;

public interface ISettingsStore
{
    Task InitializeAsync(CancellationToken ct = default);
    Task<ProxyConfiguration?> LoadProxyAsync(CancellationToken ct = default);
    Task SaveProxyAsync(ProxyConfiguration proxy, CancellationToken ct = default);
    Task<AppSettings> LoadSettingsAsync(CancellationToken ct = default);
    Task SaveSettingsAsync(AppSettings settings, CancellationToken ct = default);
    Task SaveConfigurationAsync(ProxyConfiguration proxy, AppSettings settings, CancellationToken ct = default);
    Task AddResultAsync(IpTestResult result, CancellationToken ct = default);
    Task<IReadOnlyList<IpTestResult>> GetResultsAsync(CancellationToken ct = default);
    Task AddLogAsync(LogEntry entry, CancellationToken ct = default);
    Task<IReadOnlyList<LogEntry>> GetLogsAsync(CancellationToken ct = default);
}

public interface INetworkInformationService { Task<NetworkSnapshot> GetAsync(CancellationToken ct); }
public interface IProxySessionProvider
{
    bool SupportsSticky { get; }
    ProxyConfiguration Resolve(ProxyConfiguration gateway, string profileId);
}
