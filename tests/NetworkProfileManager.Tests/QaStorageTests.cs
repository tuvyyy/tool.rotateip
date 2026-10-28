using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using NetworkProfileManager.Core.Models;
using NetworkProfileManager.Infrastructure.Storage;
using Xunit;
using NetworkProfileManager.App.Services;

namespace NetworkProfileManager.Tests;
public class QaStorageTests
{
    [Fact] public async Task ExportCsvWritesUnicodeEscapesAndNeutralizesFormulaInjection()
    {
        using var folder = new TestFolder(); var path = Path.Combine(folder.Root, "qa.csv");
        await DesktopService.ExportResultsToPathAsync([new("A", "203.0.113.8", false, 4, DateTimeOffset.UtcNow, "mock", "=malicious,\"value\"\nTiếng Việt")], path, CancellationToken.None);
        var text = await File.ReadAllTextAsync(path); Assert.Contains("Tiếng Việt", text); Assert.Contains("'=malicious", text); Assert.Contains("\"\"value\"\"", text);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, (await File.ReadAllBytesAsync(path)).Take(3));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DesktopService.ExportResultsToPathAsync([], Path.Combine(folder.Root, "cancel.csv"), cancelled.Token));
    }
    [Theory] [InlineData("", "", true)] [InlineData("", "password", false)] [InlineData("username", "", true)]
    [InlineData("user:invalid", "pass", false)] [InlineData("user", "bad\npass", false)]
    [InlineData("é+/user", "p:@/?&=é", true)]
    public void CredentialValidationBoundary(string username, string password, bool valid)
    {
        var proxy = new ProxyConfiguration { Host = "proxy.test", Username = username, Password = password };
        if (valid) proxy.Validate(); else Assert.Throws<ArgumentException>(proxy.Validate);
    }
    [Fact] public void OversizedUtf8CredentialsRejected() => Assert.Throws<ArgumentException>(() => new ProxyConfiguration { Host = "host", Username = "user", Password = new string('é', 128) }.Validate());
    [Fact] public void Ipv6EndpointUsesAuthorityBrackets() => Assert.Equal("http://[::1]:8080", new ProxyConfiguration { Host = "::1" }.Endpoint);
    [Fact] public async Task AtomicConfigurationRollsBackIfSecondWriteFails()
    {
        using var folder = new TestFolder(); var store = new SqliteSettingsStore(folder.Paths); await store.InitializeAsync();
        var original = new ProxyConfiguration { Host = "original.test" }; await store.SaveProxyAsync(original);
        using (var db = Open(folder))
        {
            using var command = db.CreateCommand();
            command.CommandText = "CREATE TRIGGER reject_settings BEFORE INSERT ON config WHEN NEW.key='settings' BEGIN SELECT RAISE(ABORT,'qa write failure'); END";
            command.ExecuteNonQuery();
        }
        await Assert.ThrowsAsync<SqliteException>(() => store.SaveConfigurationAsync(original with { Host = "replacement.test" }, new()));
        Assert.Equal(original, await store.LoadProxyAsync());
    }
    [Fact] public async Task RestartPreservesThemeBrowsersAndEncryptedCredentials()
    {
        using var folder = new TestFolder(); var store = new SqliteSettingsStore(folder.Paths); await store.InitializeAsync();
        var settings = new AppSettings { LightTheme = true, ProfileA = new("A", "Cốc Cốc A", BrowserKind.CocCoc), ProfileB = new("B", "Firefox B", BrowserKind.Firefox) };
        var proxy = new ProxyConfiguration { Host = "proxy.test", Username = "qa-user-unique", Password = "qa-secret-98+/é" };
        await store.SaveConfigurationAsync(proxy, settings);
        var restarted = new SqliteSettingsStore(folder.Paths); await restarted.InitializeAsync();
        Assert.Equal(settings, await restarted.LoadSettingsAsync()); Assert.Equal(proxy, await restarted.LoadProxyAsync());
        SqliteConnection.ClearAllPools();
        foreach (var file in Directory.GetFiles(folder.Root, "manager.db*"))
        {
            var bytes = await File.ReadAllBytesAsync(file);
            foreach (var encoding in new[] { Encoding.UTF8, Encoding.Unicode })
            { var text = encoding.GetString(bytes); Assert.DoesNotContain(proxy.Password, text); Assert.DoesNotContain(proxy.Username, text); }
        }
    }
    [Fact] public async Task TamperedDpapiBlobCannotBeDecrypted()
    {
        using var folder = new TestFolder(); var store = new SqliteSettingsStore(folder.Paths); await store.InitializeAsync();
        using (var db = Open(folder)) { using var command = db.CreateCommand(); command.CommandText = "INSERT INTO config VALUES('proxy',randomblob(80))"; command.ExecuteNonQuery(); }
        await Assert.ThrowsAnyAsync<CryptographicException>(() => store.LoadProxyAsync());
    }
    [Fact] public async Task MissingTablesAreRecreatedWithoutDeletingConfig()
    {
        using var folder = new TestFolder(); var store = new SqliteSettingsStore(folder.Paths); await store.InitializeAsync();
        await store.SaveSettingsAsync(new() { TimeoutSeconds = 9 });
        using (var db = Open(folder)) { using var command = db.CreateCommand(); command.CommandText = "DROP TABLE logs; DROP TABLE results"; command.ExecuteNonQuery(); }
        await store.InitializeAsync(); Assert.Empty(await store.GetResultsAsync()); Assert.Empty(await store.GetLogsAsync()); Assert.Equal(9, (await store.LoadSettingsAsync()).TimeoutSeconds);
    }
    [Fact] public async Task CorruptDatabaseFailsWithoutOverwritingFile()
    {
        using var folder = new TestFolder(); var bytes = Encoding.UTF8.GetBytes("QA deliberately invalid SQLite data"); await File.WriteAllBytesAsync(folder.Paths.Database, bytes);
        await Assert.ThrowsAnyAsync<SqliteException>(() => new SqliteSettingsStore(folder.Paths).InitializeAsync());
        SqliteConnection.ClearAllPools();
        Assert.Equal(bytes, await File.ReadAllBytesAsync(folder.Paths.Database));
    }
    [Fact] public async Task CorruptHistoryRowDoesNotHideValidRows()
    {
        using var folder = new TestFolder(); var store = new SqliteSettingsStore(folder.Paths); await store.InitializeAsync();
        await store.AddResultAsync(new("A", "203.0.113.1", true, 1, DateTimeOffset.UtcNow, "mock", null));
        using (var db = Open(folder)) { using var command = db.CreateCommand(); command.CommandText = "INSERT INTO results(data) VALUES('{bad')"; command.ExecuteNonQuery(); }
        Assert.Single(await store.GetResultsAsync());
        using (var db = Open(folder)) { using var command = db.CreateCommand(); command.CommandText = "SELECT COUNT(*) FROM results"; Assert.Equal(2L, command.ExecuteScalar()); }
    }
    [Fact] public async Task ReadOnlySandboxFailsWithoutChangingOtherData()
    {
        using var folder = new TestFolder(); var info = new DirectoryInfo(folder.Root); var original = info.GetAccessControl();
        var restricted = info.GetAccessControl(); restricted.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.Write, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Deny));
        try
        {
            info.SetAccessControl(restricted);
            await Assert.ThrowsAnyAsync<Exception>(() => new SqliteSettingsStore(folder.Paths).InitializeAsync());
            Assert.False(File.Exists(folder.Paths.Database));
        }
        finally { info.SetAccessControl(original); }
    }
    [Fact] public async Task RetentionBoundsDatabaseAndUiHistory()
    {
        using var folder = new TestFolder(); var store = new SqliteSettingsStore(folder.Paths); await store.InitializeAsync();
        using (var db = Open(folder))
        {
            using var command = db.CreateCommand(); command.CommandText = "WITH RECURSIVE c(n) AS (SELECT 1 UNION ALL SELECT n+1 FROM c WHERE n<2001) INSERT INTO results(data) SELECT $data FROM c";
            command.Parameters.AddWithValue("$data", JsonSerializer.Serialize(new IpTestResult("A", "203.0.113.1", true, 1, DateTimeOffset.UtcNow, "mock", null))); command.ExecuteNonQuery();
        }
        await store.AddResultAsync(new("B", "203.0.113.2", true, 1, DateTimeOffset.UtcNow, "mock", null));
        Assert.Equal(500, (await store.GetResultsAsync()).Count);
        using (var db = Open(folder)) { using var command = db.CreateCommand(); command.CommandText = "SELECT COUNT(*) FROM results"; Assert.Equal(2000L, command.ExecuteScalar()); }
    }
    private static SqliteConnection Open(TestFolder folder) { var connection = new SqliteConnection($"Data Source={folder.Paths.Database}"); connection.Open(); return connection; }
}
