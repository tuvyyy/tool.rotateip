using System.Net;

namespace NetworkProfileManager.Core.Models;

public enum ProxyProtocol { Http, Socks5 }
public enum RotationMode { Standard, Sticky }
public enum BrowserKind { Chrome, Firefox, CocCoc, Edge }

public sealed record ProxyConfiguration
{
    public ProxyProtocol Protocol { get; init; }
    public string Host { get; init; } = "";
    public int Port { get; init; } = 8080;
    public string Username { get; init; } = "";
    public string Password { get; init; } = "";
    public DateTime? ExpirationDate { get; init; }
    public RotationMode RotationMode { get; init; }
    public string Endpoint => $"{Protocol.ToString().ToLowerInvariant()}://{(Host.Contains(':') ? $"[{Host.Trim('[', ']')}]" : Host)}:{Port}";

    public void Validate()
    {
        if (!Enum.IsDefined(Protocol) || !Enum.IsDefined(RotationMode))
            throw new ArgumentException("Giao thức hoặc chế độ proxy không hợp lệ.");
        if (string.IsNullOrWhiteSpace(Host) || Host != Host.Trim() || Host.Any(char.IsWhiteSpace) ||
            Host.IndexOfAny(['/', '\\', '@', '\r', '\n']) >= 0 || Uri.CheckHostName(Host) == UriHostNameType.Unknown)
            throw new ArgumentException("Host phải là tên miền hoặc IP, không kèm giao thức hay tài khoản.");
        if (Port is < 1 or > 65535) throw new ArgumentException("Port phải từ 1 đến 65535.");
        if (Username is null || Password is null || Username.Contains(':') || Username.Any(char.IsControl) || Password.Any(char.IsControl))
            throw new ArgumentException("Thông tin xác thực chứa ký tự không hỗ trợ.");
        if (string.IsNullOrEmpty(Username) && !string.IsNullOrEmpty(Password))
            throw new ArgumentException("Cần username khi sử dụng password.");
        if (System.Text.Encoding.UTF8.GetByteCount(Username) > 255 || System.Text.Encoding.UTF8.GetByteCount(Password) > 255)
            throw new ArgumentException("Thông tin xác thực vượt quá 255 byte.");
        if (ExpirationDate?.Date < DateTime.Today) throw new ArgumentException("Proxy đã hết hạn.");
    }
    // Never include credentials in diagnostic output.
    public override string ToString() => Endpoint;
}

public sealed record BrowserProfile(string Id, string Name, BrowserKind Browser, string ExecutablePath = "");
public sealed record AppSettings
{
    public string EchoEndpoint { get; init; } = "https://api.ipify.org";
    public int TimeoutSeconds { get; init; } = 20;
    public bool LightTheme { get; init; }
    public BrowserProfile ProfileA { get; init; } = new("A", "Profile A", BrowserKind.Chrome);
    public BrowserProfile ProfileB { get; init; } = new("B", "Profile B", BrowserKind.Edge);
    public void Validate()
    {
        if (!Uri.TryCreate(EchoEndpoint, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !string.IsNullOrEmpty(uri.UserInfo))
            throw new ArgumentException("Endpoint kiểm tra phải là HTTPS, không chứa tài khoản.");
        if (TimeoutSeconds is < 3 or > 120) throw new ArgumentException("Timeout phải từ 3 đến 120 giây.");
        if (ProfileA is null || ProfileB is null || ProfileA.Id != "A" || ProfileB.Id != "B" ||
            !Enum.IsDefined(ProfileA.Browser) || !Enum.IsDefined(ProfileB.Browser) ||
            string.IsNullOrWhiteSpace(ProfileA.Name) || string.IsNullOrWhiteSpace(ProfileB.Name) ||
            ProfileA.Name.Length > 80 || ProfileB.Name.Length > 80 || ProfileA.ExecutablePath is null || ProfileB.ExecutablePath is null)
            throw new ArgumentException("Cấu hình hai profile không hợp lệ; cần ID A/B riêng và tên không trống.");
    }
}

public sealed record IpTestResult(string Profile, string? Ip, bool Success, long ElapsedMs,
    DateTimeOffset Timestamp, string Endpoint, string? Error)
{
    public string Status => Success ? "Thành công" : "Thất bại";
    public string LocalTime => Timestamp.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss");
}

public static class RotationAnalysis
{
    public static string Describe(IEnumerable<IpTestResult> results)
    {
        var list = results.ToList();
        if (list.Count != 3 || list.Any(x => !x.Success)) return "Chưa đủ ba lần thành công để kết luận.";
        return list.Select(x => x.Ip).Distinct().Count() == 1 ? "Ba lần kiểm tra cùng IP" : "Phát hiện IP thay đổi";
    }
}

public sealed record NetworkSnapshot(string Ssid, string Adapter, string Ipv4, string Mac,
    string Gateway, string Dns, string Dhcp);
public sealed record LogEntry(DateTimeOffset Time, string Level, string Message);
