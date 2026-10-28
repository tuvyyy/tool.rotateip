using System.Net;
using System.Net.NetworkInformation;
using System.Text.Json;
using NetworkProfileManager.Core.Models;
using NetworkProfileManager.Infrastructure.NetworkServices;
using NetworkProfileManager.Infrastructure.Services;
using NetworkProfileManager.Infrastructure.SessionProviders;
using NetworkProfileManager.Infrastructure.Storage;

var command = args.FirstOrDefault() ?? "";
if (command == "probe-proxy-auth")
{
    await ProxyAuthDiagnostics.RunAsync(); return;
}
if (command == "probe-direct")
{
    var result = await new IpCheckerService(new(), new StandardSessionProvider()).CheckAsync(new() { TimeoutSeconds = 8 }, "Direct", "Direct public IP", null, CancellationToken.None);
    Console.WriteLine(JsonSerializer.Serialize(new { Status = result.Success ? "PASS" : "BLOCKED", ValidIp = IPAddress.TryParse(result.Ip, out _), result.ElapsedMs, result.Error })); return;
}
if (command == "network")
{
    var snapshot = await new NetworkInformationService().GetAsync(CancellationToken.None);
    Console.WriteLine(JsonSerializer.Serialize(new { Status = "PASS", AdapterAvailable = snapshot.Adapter != "—",
        WifiAdapterUp = NetworkInterface.GetAllNetworkInterfaces().Any(n => n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 && n.OperationalStatus == OperationalStatus.Up),
        Ipv4Available = !string.IsNullOrEmpty(snapshot.Ipv4) && snapshot.Ipv4 != "—", GatewayAvailable = !string.IsNullOrEmpty(snapshot.Gateway) && snapshot.Gateway != "—" })); return;
}
if (args.Length != 2 || command is not ("seed" or "verify" or "mutex")) throw new ArgumentException("Expected seed|verify|mutex <sandbox-directory>.");
var root = Path.GetFullPath(args[1]); var allowed = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "artifacts")) + Path.DirectorySeparatorChar;
if (!root.StartsWith(allowed, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("QA harness accepts directories only within workspace artifacts.");
var paths = new AppPaths(root);
if (command == "mutex") { Console.WriteLine(paths.MutexName); return; }
var store = new SqliteSettingsStore(paths); await store.InitializeAsync();
if (command == "seed")
{
    await store.SaveConfigurationAsync(new() { Host = "qa-gateway.invalid", Port = 8080, Username = "qa-fixture-user", Password = "qa-fixture-only-secret" },
        new() { LightTheme = true, ProfileA = new("A", "QA Edge A", BrowserKind.Edge), ProfileB = new("B", "QA Firefox B", BrowserKind.Firefox) });
    await store.AddResultAsync(new("A", "203.0.113.5", true, 15, DateTimeOffset.UtcNow, "http://qa-gateway.invalid:8080", null));
    Directory.CreateDirectory(paths.Profiles); await File.WriteAllTextAsync(Path.Combine(paths.Profiles, "preserve-marker.txt"), "QA preserve marker");
}
var proxy = await store.LoadProxyAsync(); var settings = await store.LoadSettingsAsync();
if (proxy?.Username != "qa-fixture-user" || proxy.Password != "qa-fixture-only-secret" || !settings.LightTheme || settings.ProfileA.Browser != BrowserKind.Edge || settings.ProfileB.Browser != BrowserKind.Firefox || !File.Exists(Path.Combine(paths.Profiles, "preserve-marker.txt")))
    throw new InvalidOperationException("QA persistence assertions failed.");
Console.WriteLine(JsonSerializer.Serialize(new { Status = "PASS", CredentialsDecrypt = true, ThemePreserved = true, BrowserSelectionsPreserved = true, ProfileMarkerPreserved = true }));
