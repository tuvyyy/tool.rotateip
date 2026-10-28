using CommunityToolkit.Mvvm.ComponentModel;
using NetworkProfileManager.Core.Models;
using NetworkProfileManager.Infrastructure.BrowserAdapters;

namespace NetworkProfileManager.App.ViewModels;

public sealed record BrowserOption(BrowserKind Kind, string Label)
{
    public override string ToString() => Label;
}

public partial class BrowserCardViewModel : ObservableObject
{
    public string Id { get; }
    public string Caption => $"Trình duyệt {Id}";
    public IReadOnlyList<BrowserOption> Browsers { get; } = Enum.GetValues<BrowserKind>().Select(k => new BrowserOption(k, BrowserProfileService.DisplayName(k))).ToArray();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BrowserDisplayName), nameof(ProfileFolderHint))]
    private BrowserKind browser;

    [ObservableProperty]
    private string name;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExecutableSummary), nameof(HasCustomExecutable))]
    private string executablePath;

    [ObservableProperty]
    private string connectionStatus = "Chưa kiểm tra";

    [ObservableProperty]
    private string statusLabel = "Chưa kiểm tra";

    [ObservableProperty]
    private bool hasProxyError;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasIp))]
    private string ip = "—";

    [ObservableProperty]
    private string lastCheck = "—";

    [ObservableProperty]
    private string lastCheckDetails = "Chưa có lần kiểm tra";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEdit))]
    private bool isRunning;

    public bool CanEdit => !IsRunning;
    public string BrowserDisplayName => BrowserProfileService.DisplayName(Browser);
    public string ExecutableSummary => string.IsNullOrWhiteSpace(ExecutablePath) ? "Tự phát hiện trình duyệt đã cài" : ExecutablePath;
    public bool HasCustomExecutable => !string.IsNullOrWhiteSpace(ExecutablePath);
    public bool HasIp => !string.IsNullOrWhiteSpace(Ip) && Ip != "—";
    public string ProfileFolderHint => $"%LOCALAPPDATA%\\NetworkProfileManager\\Profiles\\{Id}\\{Browser}";

    public BrowserCardViewModel(BrowserProfile profile)
    {
        Id = profile.Id;
        browser = profile.Browser;
        name = profile.Name;
        executablePath = profile.ExecutablePath;
    }

    public BrowserProfile ToProfile() => new(Id, Name.Trim(), Browser, ExecutablePath.Trim());
    public override string ToString() => Name;

    public void UpdateConnection(bool running, string details)
    {
        IsRunning = running;
        ConnectionStatus = details;
        StatusLabel = running ? "Đang chạy" : "Đã dừng";
        HasProxyError = false;
    }

    public void ShowProxyError(string details)
    {
        ConnectionStatus = details;
        StatusLabel = "Proxy lỗi";
        HasProxyError = true;
    }

    public void ResetSessionStatus()
    {
        ConnectionStatus = "Chưa kiểm tra trong phiên này";
        StatusLabel = "Chưa kiểm tra";
        HasProxyError = false;
    }

    public void ApplyResult(IpTestResult result)
    {
        Ip = result.Ip ?? "—";
        LastCheck = result.Timestamp.ToLocalTime().ToString("dd/MM HH:mm:ss");
        LastCheckDetails = $"{result.LocalTime} • {result.ElapsedMs} ms";
        if (!result.Success)
        {
            ShowProxyError(result.Error ?? "Kiểm tra thất bại");
            return;
        }
        HasProxyError = false;
        StatusLabel = IsRunning ? "Đang chạy" : "Proxy hoạt động";
        ConnectionStatus = result.Success ? $"Proxy hoạt động • {result.ElapsedMs} ms" : "Kiểm tra thất bại";
    }
}
