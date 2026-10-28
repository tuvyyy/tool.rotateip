using System.Diagnostics;
using System.Text;
using NetworkProfileManager.Core.Models;
using NetworkProfileManager.Infrastructure.Storage;

namespace NetworkProfileManager.Infrastructure.BrowserAdapters;

public interface IBrowserAdapter
{
    bool Supports(BrowserKind kind);
    ProcessStartInfo Prepare(BrowserProfile profile, string executable, string directory, int proxyPort, string startUrl);
}

public sealed class ChromiumBrowserAdapter : IBrowserAdapter
{
    public bool Supports(BrowserKind kind) => kind != BrowserKind.Firefox;
    public ProcessStartInfo Prepare(BrowserProfile profile, string executable, string directory, int proxyPort, string startUrl)
    {
        Directory.CreateDirectory(directory);
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(executable)! };
        foreach (var arg in new[] { $"--user-data-dir={directory}", $"--proxy-server=http://127.0.0.1:{proxyPort}", "--proxy-bypass-list=<-loopback>",
            "--disable-quic", "--force-webrtc-ip-handling-policy=disable_non_proxied_udp", "--host-resolver-rules=MAP * ~NOTFOUND, EXCLUDE 127.0.0.1",
            "--no-first-run", "--no-default-browser-check", "--disable-background-mode", "--new-window", startUrl }) info.ArgumentList.Add(arg);
        return info;
    }
}
public sealed class FirefoxBrowserAdapter : IBrowserAdapter
{
    public bool Supports(BrowserKind kind) => kind == BrowserKind.Firefox;
    public ProcessStartInfo Prepare(BrowserProfile profile, string executable, string directory, int proxyPort, string startUrl)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "user.js"), $$"""
            // Managed only in the dedicated Network Profile Manager profile.
            user_pref("network.proxy.type", 1);
            user_pref("network.proxy.http", "127.0.0.1");
            user_pref("network.proxy.http_port", {{proxyPort}});
            user_pref("network.proxy.ssl", "127.0.0.1");
            user_pref("network.proxy.ssl_port", {{proxyPort}});
            user_pref("network.proxy.no_proxies_on", "");
            user_pref("network.proxy.allow_hijacking_localhost", true);
            user_pref("network.proxy.failover_direct", false);
            user_pref("network.http.http3.enable", false);
            user_pref("network.dns.disablePrefetch", true);
            user_pref("network.prefetch-next", false);
            user_pref("network.trr.mode", 5);
            user_pref("media.peerconnection.ice.proxy_only", true);
            user_pref("browser.shell.checkDefaultBrowser", false);
            """, new UTF8Encoding(false));
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(executable)! };
        foreach (var arg in new[] { "-no-remote", "-new-instance", "-profile", directory, startUrl }) info.ArgumentList.Add(arg);
        return info;
    }
}

public sealed class BrowserProfileService(AppPaths paths, Func<BrowserKind, string?>? executableFinder = null)
{
    private readonly IBrowserAdapter[] adapters = [new ChromiumBrowserAdapter(), new FirefoxBrowserAdapter()];
    public string ProfileDirectory(BrowserProfile profile)
    {
        if (profile.Id is not ("A" or "B")) throw new ArgumentException("Chỉ hỗ trợ profile A và B.");
        // Separate folders for browser engines as well as card IDs; switching a card never corrupts another browser's data.
        return Path.Combine(paths.Profiles, profile.Id, profile.Browser.ToString());
    }
    public ProcessStartInfo Prepare(BrowserProfile profile, int port, string startUrl)
    {
        if (!Enum.IsDefined(profile.Browser)) throw new ArgumentException("Loại trình duyệt không hợp lệ.");
        var executable = string.IsNullOrWhiteSpace(profile.ExecutablePath) ? (executableFinder ?? FindExecutable)(profile.Browser) : profile.ExecutablePath;
        if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
            throw new FileNotFoundException($"Chưa tìm thấy {DisplayName(profile.Browser)}. Hãy chọn file .exe trong thẻ trình duyệt.");
        if (!executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Cần chọn file thực thi .exe.");
        return adapters.Single(a => a.Supports(profile.Browser)).Prepare(profile, executable, ProfileDirectory(profile), port, startUrl);
    }
    public static string DisplayName(BrowserKind kind) => kind switch { BrowserKind.CocCoc => "Cốc Cốc", BrowserKind.Edge => "Microsoft Edge", _ => kind.ToString() };
    public static string? FindExecutable(BrowserKind kind)
    {
        var relative = kind switch
        {
            BrowserKind.Chrome => @"Google\Chrome\Application\chrome.exe",
            BrowserKind.Edge => @"Microsoft\Edge\Application\msedge.exe",
            BrowserKind.CocCoc => @"CocCoc\Browser\Application\browser.exe",
            _ => @"Mozilla Firefox\firefox.exe"
        };
        return new[] { Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) }
            .Select(root => Path.Combine(root, relative)).FirstOrDefault(File.Exists);
    }
}
