using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using NetworkProfileManager.Core.Models;
using NetworkProfileManager.Core.Services;

namespace NetworkProfileManager.Infrastructure.Storage;

public sealed class SqliteSettingsStore(AppPaths paths) : ISettingsStore
{
    private SqliteConnection Open() => new(new SqliteConnectionStringBuilder { DataSource = paths.Database, DefaultTimeout = 2 }.ToString());
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("NetworkProfileManager.V1");
    private async Task InitializeCoreAsync(CancellationToken ct)
    {
        await using var db = Open(); await db.OpenAsync(ct);
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS config (key TEXT PRIMARY KEY, value BLOB NOT NULL);
            CREATE TABLE IF NOT EXISTS results (id INTEGER PRIMARY KEY, data TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS logs (id INTEGER PRIMARY KEY, data TEXT NOT NULL);
            """;
        await cmd.ExecuteNonQueryAsync(ct);
    }
    private async Task<byte[]?> GetAsync(string key, CancellationToken ct)
    {
        await using var db = Open(); await db.OpenAsync(ct);
        using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT value FROM config WHERE key=$k";
        cmd.Parameters.AddWithValue("$k", key);
        return await cmd.ExecuteScalarAsync(ct) as byte[];
    }
    private async Task SetAsync(string key, byte[] value, CancellationToken ct)
    {
        await using var db = Open(); await db.OpenAsync(ct);
        using var cmd = db.CreateCommand(); cmd.CommandText = "INSERT INTO config VALUES ($k,$v) ON CONFLICT(key) DO UPDATE SET value=$v";
        cmd.Parameters.AddWithValue("$k", key); cmd.Parameters.AddWithValue("$v", value);
        await cmd.ExecuteNonQueryAsync(ct);
    }
    private async Task<ProxyConfiguration?> LoadProxyCoreAsync(CancellationToken ct)
    {
        var blob = await GetAsync("proxy", ct);
        if (blob is null) return null;
        var plain = ProtectedData.Unprotect(blob, Entropy, DataProtectionScope.CurrentUser);
        try
        {
            var config = JsonSerializer.Deserialize<ProxyConfiguration>(plain) ?? throw new ArgumentException();
            (config with { ExpirationDate = null }).Validate(); return config;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException) { throw new InvalidDataException("Cấu hình proxy đã lưu không hợp lệ. Hãy nhập lại cấu hình."); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    private async Task SaveProxyCoreAsync(ProxyConfiguration proxy, CancellationToken ct)
    {
        proxy.Validate(); var plain = JsonSerializer.SerializeToUtf8Bytes(proxy);
        try { await SetAsync("proxy", ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser), ct); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    private async Task<AppSettings> LoadSettingsCoreAsync(CancellationToken ct)
    {
        var blob = await GetAsync("settings", ct);
        if (blob is null) return new();
        try
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(blob) ?? throw new ArgumentException();
            settings.Validate(); return settings;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        { throw new InvalidDataException("Cài đặt đã lưu không hợp lệ. Hãy kiểm tra và lưu lại cài đặt."); }
    }
    private Task SaveSettingsCoreAsync(AppSettings settings, CancellationToken ct)
    { settings.Validate(); return SetAsync("settings", JsonSerializer.SerializeToUtf8Bytes(settings), ct); }
    private async Task AddAsync<T>(string table, T value, CancellationToken ct)
    {
        await using var db = Open(); await db.OpenAsync(ct);
        using var cmd = db.CreateCommand();
        // Table names are internal constants only, never user input.
        cmd.CommandText = $"INSERT INTO {table}(data) VALUES ($v); DELETE FROM {table} WHERE id NOT IN (SELECT id FROM {table} ORDER BY id DESC LIMIT 2000);";
        cmd.Parameters.AddWithValue("$v", JsonSerializer.Serialize(value)); await cmd.ExecuteNonQueryAsync(ct);
    }
    private async Task<IReadOnlyList<T>> ReadAsync<T>(string table, CancellationToken ct)
    {
        await using var db = Open(); await db.OpenAsync(ct);
        using var cmd = db.CreateCommand(); cmd.CommandText = $"SELECT data FROM {table} ORDER BY id DESC LIMIT 500";
        using var reader = await cmd.ExecuteReaderAsync(ct); var list = new List<T>();
        while (await reader.ReadAsync(ct))
        {
            try { if (JsonSerializer.Deserialize<T>(reader.GetString(0)) is { } item) list.Add(item); }
            catch (JsonException) { /* Keep damaged history rows on disk, but do not block valid history. */ }
        }
        return list;
    }
    // SQLite async APIs execute synchronously: isolate DB waits from the WPF dispatcher.
    public Task InitializeAsync(CancellationToken ct = default) => Task.Run(() => InitializeCoreAsync(ct), ct);
    public Task<ProxyConfiguration?> LoadProxyAsync(CancellationToken ct = default) => Task.Run(() => LoadProxyCoreAsync(ct), ct);
    public Task SaveProxyAsync(ProxyConfiguration proxy, CancellationToken ct = default) => Task.Run(() => SaveProxyCoreAsync(proxy, ct), ct);
    public Task<AppSettings> LoadSettingsAsync(CancellationToken ct = default) => Task.Run(() => LoadSettingsCoreAsync(ct), ct);
    public Task SaveSettingsAsync(AppSettings settings, CancellationToken ct = default) => Task.Run(() => SaveSettingsCoreAsync(settings, ct), ct);
    public Task AddResultAsync(IpTestResult result, CancellationToken ct = default) => Task.Run(() => AddAsync("results", result, ct), ct);
    public Task<IReadOnlyList<IpTestResult>> GetResultsAsync(CancellationToken ct = default) => Task.Run(() => ReadAsync<IpTestResult>("results", ct), ct);
    public Task AddLogAsync(LogEntry entry, CancellationToken ct = default) => Task.Run(() => AddAsync("logs", entry, ct), ct);
    public Task<IReadOnlyList<LogEntry>> GetLogsAsync(CancellationToken ct = default) => Task.Run(() => ReadAsync<LogEntry>("logs", ct), ct);
    public Task SaveConfigurationAsync(ProxyConfiguration proxy, AppSettings settings, CancellationToken ct = default) => Task.Run(async () =>
    {
        proxy.Validate(); settings.Validate(); var plain = JsonSerializer.SerializeToUtf8Bytes(proxy);
        try
        {
            var encrypted = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
            await using var db = Open(); await db.OpenAsync(ct); using var transaction = db.BeginTransaction();
            foreach (var pair in new[] { ("proxy", encrypted), ("settings", JsonSerializer.SerializeToUtf8Bytes(settings)) })
            {
                using var command = db.CreateCommand(); command.Transaction = transaction;
                command.CommandText = "INSERT INTO config VALUES ($k,$v) ON CONFLICT(key) DO UPDATE SET value=$v";
                command.Parameters.AddWithValue("$k", pair.Item1); command.Parameters.AddWithValue("$v", pair.Item2);
                await command.ExecuteNonQueryAsync(ct);
            }
            ct.ThrowIfCancellationRequested(); transaction.Commit();
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }, ct);
}
