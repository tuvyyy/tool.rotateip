using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NetworkProfileManager.App.Services;
using NetworkProfileManager.Core.Models;
using NetworkProfileManager.Core.Services;
using NetworkProfileManager.Infrastructure.Services;
using NetworkProfileManager.Infrastructure.Storage;

namespace NetworkProfileManager.App.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly ISettingsStore store;
    private readonly INetworkInformationService networkService;
    private readonly IpCheckerService checker;
    private readonly BrowserLauncherService launcher;
    private readonly DesktopService desktop;
    private readonly AppPaths paths;
    private CancellationTokenSource? operation;
    private bool closing;

    public ObservableCollection<BrowserCardViewModel> Cards { get; } = [];
    public ObservableCollection<IpTestResult> Results { get; } = [];
    public ObservableCollection<IpTestResult> FilteredResults { get; } = [];
    public ObservableCollection<LogEntry> Logs { get; } = [];
    public ObservableCollection<LogEntry> FilteredLogs { get; } = [];

    public string[] Protocols { get; } = ["HTTP", "SOCKS5"];
    public string[] RotationModes { get; } = ["Standard Rotation", "Sticky Session"];
    public string[] ResultFilterOptions { get; } = ["Tất cả tuyến", "Profile A", "Profile B", "Gateway", "Trực tiếp"];
    public string[] LogLevelOptions { get; } = ["Tất cả mức độ", "Thông tin (Info)", "Cảnh báo (Warning)", "Lỗi (Error)"];

    [ObservableProperty] private string status = "Sẵn sàng";
    [ObservableProperty] private string statusSummary = "Sẵn sàng";
    [ObservableProperty] private string statusKind = "THÔNG TIN";

    [ObservableProperty]
    [NotifyPropertyChangedFor(
        nameof(PageTitle),
        nameof(PageSubtitle),
        nameof(IsOverviewPage),
        nameof(IsProxyPage),
        nameof(IsBrowsersPage),
        nameof(IsIpCheckPage),
        nameof(IsNetworkPage),
        nameof(IsLogsPage),
        nameof(IsSettingsPage),
        nameof(IsBrowserActionHeaderVisible))]
    private int selectedPageIndex;

    private static readonly (string Title, string Subtitle)[] Pages =
    [
        ("Tổng quan", "Theo dõi nhanh trạng thái gateway và hai trình duyệt cô lập."),
        ("Cấu hình proxy", "Thiết lập máy chủ proxy dùng chung cho cả hai profile."),
        ("Hai trình duyệt", "Cấu hình trình duyệt, tên hồ sơ và đường dẫn thực thi cho Profile A & B."),
        ("Kiểm tra IP", "Kiểm tra, lọc và đối chiếu địa chỉ IP phản hồi theo từng tuyến kết nối."),
        ("Thông tin mạng", "Trạng thái kết nối Wi-Fi, mạng nội bộ, DNS và các công cụ mạng hệ thống."),
        ("Nhật ký", "Theo dõi lịch sử hoạt động và cảnh báo kết nối được lưu trên thiết bị."),
        ("Cài đặt", "Tùy chỉnh giao diện hiển thị, thông số kiểm tra kết nối và dữ liệu cục bộ.")
    ];

    public string PageTitle => Pages[Math.Clamp(SelectedPageIndex, 0, Pages.Length - 1)].Title;
    public string PageSubtitle => Pages[Math.Clamp(SelectedPageIndex, 0, Pages.Length - 1)].Subtitle;

    public bool IsOverviewPage => SelectedPageIndex == 0;
    public bool IsProxyPage => SelectedPageIndex == 1;
    public bool IsBrowsersPage => SelectedPageIndex == 2;
    public bool IsIpCheckPage => SelectedPageIndex == 3;
    public bool IsNetworkPage => SelectedPageIndex == 4;
    public bool IsLogsPage => SelectedPageIndex == 5;
    public bool IsSettingsPage => SelectedPageIndex == 6;
    public bool IsBrowserActionHeaderVisible => SelectedPageIndex is 0 or 2;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    private bool isBusy;

    public bool IsIdle => !IsBusy;
    public bool CanConfigure => !Cards.Any(c => launcher.IsRunning(c.Id));
    public bool IsGatewayLocked => !CanConfigure;

    [ObservableProperty] private int cardGridColumns = 2;
    [ObservableProperty] private bool isCompactSidebar;

    [ObservableProperty] private NetworkSnapshot? network;
    [ObservableProperty] private string directIp = "—";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GatewayProtocolSummary), nameof(GatewayEndpointSummary))]
    private int protocolIndex;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GatewayEndpointSummary), nameof(IsGatewayConfigured))]
    private string host = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GatewayEndpointSummary))]
    private string port = "8080";

    [ObservableProperty] private string username = "";
    [ObservableProperty] private string password = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PasswordToggleLabel))]
    private bool isPasswordVisible;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GatewayExpirationSummary), nameof(HasExpirationDate))]
    private DateTime? expirationDate;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GatewayRotationSummary))]
    private int rotationIndex;

    [ObservableProperty] private string proxyStatus = "Chưa cấu hình";
    [ObservableProperty] private string echoEndpoint = "https://api.ipify.org";
    [ObservableProperty] private string timeoutSeconds = "20";
    [ObservableProperty] private bool lightTheme;
    [ObservableProperty] private string rotationObservation = "Chưa có kết quả kiểm tra ba lần.";
    [ObservableProperty] private string duplicateNotice = "Hai profile dùng chung gateway; IP có thể trùng nhau.";
    [ObservableProperty] private IpTestResult? selectedResult;
    [ObservableProperty] private BrowserCardViewModel? selectedCard;

    [ObservableProperty] private int resultFilterIndex;
    [ObservableProperty] private int logLevelFilterIndex;
    [ObservableProperty] private string logSearchText = "";

    public string GatewayProtocolSummary => Protocols[Math.Clamp(ProtocolIndex, 0, Protocols.Length - 1)];
    public string GatewayEndpointSummary => string.IsNullOrWhiteSpace(Host)
        ? "Chưa thiết lập máy chủ proxy"
        : $"{GatewayProtocolSummary} • {Host.Trim()}:{Port.Trim()}";
    public string GatewayRotationSummary => RotationModes[Math.Clamp(RotationIndex, 0, RotationModes.Length - 1)];
    public string GatewayExpirationSummary => ExpirationDate.HasValue ? ExpirationDate.Value.ToString("dd/MM/yyyy") : "Không giới hạn";
    public bool HasExpirationDate => ExpirationDate.HasValue;
    public bool IsGatewayConfigured => !string.IsNullOrWhiteSpace(Host);
    public string PasswordToggleLabel => IsPasswordVisible ? "Ẩn" : "Hiện";

    public bool HasFilteredResults => FilteredResults.Count > 0;
    public bool IsResultsEmpty => FilteredResults.Count == 0;
    public string ResultsCountSummary => $"{FilteredResults.Count} kết quả";

    public bool HasFilteredLogs => FilteredLogs.Count > 0;
    public bool IsLogsEmpty => FilteredLogs.Count == 0;
    public string LogsCountSummary => $"{FilteredLogs.Count} bản ghi";

    public string DataDirectoryPath => paths.Root;
    public string AppVersionSummary => $"Phiên bản {typeof(MainViewModel).Assembly.GetName().Version?.ToString(3)} • Windows 11 x64";
    public string ProfilesDirectoryPath => paths.Profiles;

    public MainViewModel(
        ISettingsStore store,
        INetworkInformationService networkService,
        IpCheckerService checker,
        BrowserLauncherService launcher,
        DesktopService desktop,
        AppPaths paths)
    {
        this.store = store;
        this.networkService = networkService;
        this.checker = checker;
        this.launcher = launcher;
        this.desktop = desktop;
        this.paths = paths;

        var defaultSettings = new AppSettings();
        Cards.Add(new(defaultSettings.ProfileA));
        Cards.Add(new(defaultSettings.ProfileB));
        SelectedCard = Cards[0];

        Results.CollectionChanged += OnResultsCollectionChanged;
        Logs.CollectionChanged += OnLogsCollectionChanged;

        launcher.StateChanged += (id, message) => Dispatch(() =>
        {
            var card = Cards.FirstOrDefault(c => c.Id == id);
            card?.UpdateConnection(launcher.IsRunning(id), message);
            OnPropertyChanged(nameof(CanConfigure));
            OnPropertyChanged(nameof(IsGatewayLocked));
        });
        launcher.ProxyError += (id, message) => Dispatch(() =>
        {
            Cards.FirstOrDefault(c => c.Id == id)?.ShowProxyError(message);
            Status = $"Profile {id}: {message}";
            StatusKind = "LỖI";
            StatusSummary = "Proxy lỗi";
        });
    }

    private static void Dispatch(Action action) => Application.Current.Dispatcher.BeginInvoke(action);

    public void UpdateResponsiveLayout(double windowWidth)
    {
        if (double.IsNaN(windowWidth) || windowWidth <= 0) return;
        IsCompactSidebar = windowWidth < 980;
        CardGridColumns = windowWidth < 1080 ? 1 : 2;
    }

    partial void OnSelectedPageIndexChanged(int value)
    {
        if (value != 1 && IsPasswordVisible) IsPasswordVisible = false;
    }

    partial void OnResultFilterIndexChanged(int value) => RefreshFilteredResults();
    partial void OnLogLevelFilterIndexChanged(int value) => RefreshFilteredLogs();
    partial void OnLogSearchTextChanged(string value) => RefreshFilteredLogs();

    private void OnResultsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => RefreshFilteredResults();
    private void OnLogsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => RefreshFilteredLogs();

    private void RefreshFilteredResults()
    {
        FilteredResults.Clear();
        foreach (var item in Results)
        {
            var match = ResultFilterIndex switch
            {
                1 => string.Equals(item.Profile, "A", StringComparison.OrdinalIgnoreCase),
                2 => string.Equals(item.Profile, "B", StringComparison.OrdinalIgnoreCase),
                3 => string.Equals(item.Profile, "Gateway", StringComparison.OrdinalIgnoreCase),
                4 => string.Equals(item.Profile, "Direct", StringComparison.OrdinalIgnoreCase),
                _ => true
            };
            if (match) FilteredResults.Add(item);
        }
        if (SelectedResult is null || !FilteredResults.Contains(SelectedResult))
            SelectedResult = FilteredResults.FirstOrDefault();
        OnPropertyChanged(nameof(HasFilteredResults));
        OnPropertyChanged(nameof(IsResultsEmpty));
        OnPropertyChanged(nameof(ResultsCountSummary));
    }

    private void RefreshFilteredLogs()
    {
        FilteredLogs.Clear();
        var query = LogSearchText.Trim();
        foreach (var entry in Logs)
        {
            var levelMatch = LogLevelFilterIndex switch
            {
                1 => string.Equals(entry.Level, "Info", StringComparison.OrdinalIgnoreCase),
                2 => string.Equals(entry.Level, "Warning", StringComparison.OrdinalIgnoreCase),
                3 => string.Equals(entry.Level, "Error", StringComparison.OrdinalIgnoreCase),
                _ => true
            };
            if (!levelMatch) continue;
            if (!string.IsNullOrEmpty(query) &&
                !entry.Message.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                !entry.Level.Contains(query, StringComparison.OrdinalIgnoreCase))
                continue;
            FilteredLogs.Add(entry);
        }
        OnPropertyChanged(nameof(HasFilteredLogs));
        OnPropertyChanged(nameof(IsLogsEmpty));
        OnPropertyChanged(nameof(LogsCountSummary));
    }

    public Task InitializeAsync() => RunAsync(async ct =>
    {
        AppSettings settings; var recoveryWarning = false;
        try { settings = await store.LoadSettingsAsync(ct); }
        catch (InvalidDataException) { settings = new(); recoveryWarning = true; }
        EchoEndpoint = settings.EchoEndpoint;
        TimeoutSeconds = settings.TimeoutSeconds.ToString();
        LightTheme = settings.LightTheme;
        if (Cards.Count == 2)
        {
            Cards[0].Browser = settings.ProfileA.Browser;
            Cards[0].Name = settings.ProfileA.Name;
            Cards[0].ExecutablePath = settings.ProfileA.ExecutablePath;
            Cards[1].Browser = settings.ProfileB.Browser;
            Cards[1].Name = settings.ProfileB.Name;
            Cards[1].ExecutablePath = settings.ProfileB.ExecutablePath;
        }
        else
        {
            Cards.Clear();
            Cards.Add(new(settings.ProfileA));
            Cards.Add(new(settings.ProfileB));
        }
        SelectedCard = Cards[0];
        try
        {
            var p = await store.LoadProxyAsync(ct);
            if (p is not null)
            {
                ProtocolIndex = (int)p.Protocol;
                Host = p.Host;
                Port = p.Port.ToString();
                Username = p.Username;
                Password = p.Password;
                ExpirationDate = p.ExpirationDate;
                RotationIndex = (int)p.RotationMode;
                ProxyStatus = "Đã nạp cấu hình • chưa kiểm tra kết nối";
            }
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or InvalidDataException)
        {
            ProxyStatus = "Không giải mã được cấu hình. Hãy nhập lại tài khoản proxy bằng tài khoản Windows hiện tại.";
            recoveryWarning = true;
        }
        foreach (var result in await store.GetResultsAsync(ct)) Results.Add(result);
        foreach (var entry in await store.GetLogsAsync(ct)) Logs.Add(entry);
        foreach (var card in Cards)
        {
            var latest = Results.FirstOrDefault(r => r.Profile == card.Id);
            if (latest is not null)
            {
                card.ApplyResult(latest);
                card.ResetSessionStatus();
            }
        }
        Network = await networkService.GetAsync(ct);
        Status = "Sẵn sàng. Nhập proxy dùng chung rồi kiểm tra hoặc khởi chạy.";
        if (recoveryWarning)
        {
            Status = "Một phần cấu hình đã lưu không hợp lệ. Đã mở giao diện để nhập lại; dữ liệu cũ chưa bị ghi đè.";
            StatusKind = "CẢNH BÁO";
        }
    });

    partial void OnLightThemeChanged(bool value) => desktop.ApplyTheme(value);

    private ProxyConfiguration ReadProxy()
    {
        if (!int.TryParse(Port, out var number)) throw new ArgumentException("Port phải là số nguyên.");
        var config = new ProxyConfiguration
        {
            Protocol = (ProxyProtocol)ProtocolIndex,
            Host = Host.Trim(),
            Port = number,
            Username = Username,
            Password = Password,
            ExpirationDate = ExpirationDate,
            RotationMode = (RotationMode)RotationIndex
        };
        config.Validate();
        return config;
    }

    private AppSettings ReadSettings()
    {
        if (!int.TryParse(TimeoutSeconds, out var seconds)) throw new ArgumentException("Timeout phải là số nguyên.");
        var config = new AppSettings
        {
            EchoEndpoint = EchoEndpoint.Trim(),
            TimeoutSeconds = seconds,
            LightTheme = LightTheme,
            ProfileA = Cards[0].ToProfile(),
            ProfileB = Cards[1].ToProfile()
        };
        config.Validate();
        return config;
    }

    private void EnsureStopped()
    {
        if (Cards.Any(c => launcher.IsRunning(c.Id)))
            throw new InvalidOperationException("Dừng cả hai trình duyệt trước khi thay đổi cấu hình đang dùng.");
    }

    private Task RunProxyCheckAsync(Func<CancellationToken, Task> work) => RunAsync(work, "Đang kiểm tra", "Sẵn sàng", "Proxy lỗi");
    private Task RunLaunchAsync(Func<CancellationToken, Task> work) => RunAsync(work, "Đang khởi chạy", "Đã khởi chạy", "Khởi chạy lỗi");

    private async Task RunAsync(Func<CancellationToken, Task> work, string workingSummary = "Đang xử lý", string successSummary = "Sẵn sàng", string errorSummary = "Có lỗi")
    {
        if (IsBusy || closing) return;
        IsBusy = true;
        StatusKind = "ĐANG XỬ LÝ";
        StatusSummary = workingSummary;
        Status = "Đang xử lý…";
        operation = new CancellationTokenSource();
        try
        {
            await work(operation.Token);
            if (StatusKind == "ĐANG XỬ LÝ") StatusKind = "THÀNH CÔNG";
            StatusSummary = StatusKind == "LỖI" ? errorSummary : StatusKind == "CẢNH BÁO" ? "Cần kiểm tra" : successSummary;
        }
        catch (OperationCanceledException)
        {
            Status = "Đã hủy thao tác.";
            StatusKind = "CẢNH BÁO";
            StatusSummary = "Đã hủy";
        }
        catch (Exception ex)
        {
            Status = SafeError(ex);
            StatusKind = "LỖI";
            StatusSummary = errorSummary;
            var entry = new LogEntry(DateTimeOffset.Now, "Error", Status);
            Logs.Insert(0, entry);
            try { await store.AddLogAsync(entry); } catch { }
        }
        finally
        {
            operation.Dispose();
            operation = null;
            IsBusy = false;
        }
    }

    private string SafeError(Exception ex)
    {
        var text = ex is ArgumentException or InvalidOperationException or NotSupportedException or FileNotFoundException or IOException ? ex.Message
            : ex is Win32Exception ? "Windows không thể khởi chạy tiến trình. Kiểm tra executable và quyền truy cập."
            : "Thao tác thất bại. Kiểm tra quyền truy cập dữ liệu, kết nối và cấu hình.";
        if (!string.IsNullOrEmpty(Password)) text = text.Replace(Password, "[đã che]", StringComparison.Ordinal);
        if (!string.IsNullOrEmpty(Username)) text = text.Replace(Username, "[đã che]", StringComparison.Ordinal);
        return text;
    }

    private async Task RecordAsync(IpTestResult result, CancellationToken ct)
    {
        Results.Insert(0, result);
        while (Results.Count > 500) Results.RemoveAt(Results.Count - 1);
        SelectedResult = result;
        await store.AddResultAsync(result, ct);
        Cards.FirstOrDefault(c => c.Id == result.Profile)?.ApplyResult(result);
        var entry = new LogEntry(DateTimeOffset.Now, result.Success ? "Info" : "Warning", $"IP check {result.Profile}: {(result.Success ? result.Ip : result.Error)}");
        Logs.Insert(0, entry);
        while (Logs.Count > 500) Logs.RemoveAt(Logs.Count - 1);
        await store.AddLogAsync(entry, ct);
        if (Cards.All(c => c.Ip != "—"))
        {
            DuplicateNotice = Cards[0].Ip == Cards[1].Ip
                ? "Hai profile trả về cùng IP trong lần kiểm tra gần nhất; đây không phải lỗi."
                : "Hai profile có IP khác nhau trong lần kiểm tra gần nhất; kết quả có thể thay đổi.";
        }
    }

    private async Task<IpTestResult> TestCardAsync(BrowserCardViewModel card, CancellationToken ct)
    {
        var settings = ReadSettings();
        var localPort = launcher.GetPort(card.Id);
        var result = localPort.HasValue
            ? await checker.CheckAsync(settings, card.Id, $"http://127.0.0.1:{localPort}", localPort, ct)
            : await checker.CheckGatewayAsync(ReadProxy(), settings, card.Id, ct);
        await RecordAsync(result, ct);
        return result;
    }

    [RelayCommand]
    private Task SaveProxyAsync() => RunAsync(async ct =>
    {
        IsPasswordVisible = false;
        EnsureStopped();
        var proxy = ReadProxy();
        var settings = ReadSettings();
        await store.SaveConfigurationAsync(proxy, settings, ct);
        ProxyStatus = proxy.RotationMode == RotationMode.Sticky ? "Sticky Session chưa được cấu hình" : "Đã lưu bằng DPAPI • chưa kiểm tra kết nối";
        Status = "Đã lưu cấu hình dùng chung cho hai profile.";
    });

    [RelayCommand]
    private Task SaveSettingsAsync() => RunAsync(async ct =>
    {
        EnsureStopped();
        await store.SaveSettingsAsync(ReadSettings(), ct);
        Status = "Đã lưu cài đặt và lựa chọn hai trình duyệt.";
    });

    [RelayCommand]
    private Task TestGatewayAsync() => RunProxyCheckAsync(async ct =>
    {
        var result = await checker.CheckGatewayAsync(ReadProxy(), ReadSettings(), "Gateway", ct);
        await RecordAsync(result, ct);
        ProxyStatus = result.Success ? "Gateway hoạt động • " + result.Ip : result.Error ?? "Không kết nối được";
        Status = ProxyStatus;
        if (!result.Success) StatusKind = "LỖI";
    });

    [RelayCommand]
    private Task TestProfileAsync(BrowserCardViewModel? card) => RunProxyCheckAsync(async ct =>
    {
        if (card is null) return;
        var result = await TestCardAsync(card, ct);
        Status = result.Success ? $"Proxy {card.Id}: {result.Ip}. Đây là IP của request qua proxy service." : result.Error ?? "Kiểm tra thất bại.";
        if (!result.Success) StatusKind = "LỖI";
    });

    [RelayCommand]
    private Task CheckThreeAsync() => RunProxyCheckAsync(async ct =>
    {
        var card = SelectedCard ?? Cards[0];
        var list = new List<IpTestResult>();
        for (var i = 0; i < 3; i++)
        {
            Status = $"Đang kiểm tra proxy {card.Id}: {i + 1}/3…";
            list.Add(await TestCardAsync(card, ct));
        }
        RotationObservation = RotationAnalysis.Describe(list);
        Status = RotationObservation;
        if (list.Any(r => !r.Success)) StatusKind = "CẢNH BÁO";
    });

    [RelayCommand]
    private Task CheckDirectAsync() => RunAsync(async ct =>
    {
        var result = await checker.CheckAsync(ReadSettings(), "Direct", "Kết nối trực tiếp", null, ct);
        await RecordAsync(result, ct);
        DirectIp = result.Ip ?? "—";
        Status = result.Success ? "Public IP trực tiếp: " + result.Ip : result.Error ?? "Kiểm tra thất bại.";
        if (!result.Success) StatusKind = "LỖI";
    }, "Đang kiểm tra", "Sẵn sàng", "Kết nối lỗi");

    private async Task LaunchCardAsync(BrowserCardViewModel card, CancellationToken ct)
    {
        var settings = ReadSettings();
        var proxy = ReadProxy();
        await store.SaveConfigurationAsync(proxy, settings, ct);
        await launcher.StartAsync(card.ToProfile(), proxy, settings, ct);
        card.UpdateConnection(true, $"Đang chạy • 127.0.0.1:{launcher.GetPort(card.Id)}");
        var result = await TestCardAsync(card, ct);
        if (!result.Success)
        {
            StatusKind = "CẢNH BÁO";
            Status = result.Error ?? "Trình duyệt đã mở nhưng kiểm tra IP thất bại.";
        }
    }

    [RelayCommand]
    private Task LaunchProfileAsync(BrowserCardViewModel? card) => RunLaunchAsync(async ct =>
    {
        if (card is null) return;
        await LaunchCardAsync(card, ct);
        if (StatusKind == "ĐANG XỬ LÝ") Status = $"Đã khởi chạy profile {card.Id}. Xem trang IP echo trong trình duyệt để đối chiếu.";
    });

    [RelayCommand]
    private Task StartBothAsync() => RunLaunchAsync(async ct =>
    {
        ReadProxy();
        ReadSettings();
        var errors = new List<string>();
        foreach (var card in Cards)
        {
            ct.ThrowIfCancellationRequested();
            if (launcher.IsRunning(card.Id)) continue;
            try
            {
                Status = $"Đang chuẩn bị profile {card.Id}…";
                await LaunchCardAsync(card, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                card.ShowProxyError(SafeError(ex));
                errors.Add($"{card.Id}: {card.ConnectionStatus}");
            }
        }
        Status = errors.Count == 0
            ? "Đã xử lý cả hai profile. Đối chiếu IP bằng trang đang mở trong từng trình duyệt."
            : string.Join(" | ", errors);
        if (errors.Count > 0) StatusKind = "LỖI";
    });

    [RelayCommand]
    private async Task StopProfileAsync(BrowserCardViewModel? card)
    {
        if (card is null) return;
        try
        {
            await launcher.StopAsync(card.Id);
            card.UpdateConnection(false, "Listener đã đóng");
            Status = $"Đã dừng profile {card.Id} và listener.";
            StatusKind = "THÀNH CÔNG";
            StatusSummary = "Đã dừng";
        }
        catch (Exception ex)
        {
            Status = SafeError(ex);
            StatusKind = "LỖI";
            StatusSummary = "Có lỗi";
        }
    }

    [RelayCommand]
    private async Task StopBothAsync()
    {
        operation?.Cancel();
        try
        {
            await launcher.StopBothAsync();
            foreach (var card in Cards) card.UpdateConnection(false, "Listener đã đóng");
            Status = "Đã dừng cả hai profile và đóng listener.";
            StatusKind = "THÀNH CÔNG";
            StatusSummary = "Đã dừng";
        }
        catch (Exception ex)
        {
            Status = SafeError(ex);
            StatusKind = "LỖI";
            StatusSummary = "Có lỗi";
        }
    }

    [RelayCommand] private void Cancel() => operation?.Cancel();

    [RelayCommand]
    private Task RefreshNetworkAsync() => RunAsync(async ct =>
    {
        Network = await networkService.GetAsync(ct);
        Status = "Đã cập nhật thông tin adapter và Wi-Fi.";
    });

    [RelayCommand]
    private Task FlushDnsAsync() => RunAsync(async ct =>
    {
        Status = await desktop.RunIpConfigAsync(false, ct) ? "Đã Flush DNS." : "Đã hủy Flush DNS.";
    });

    [RelayCommand]
    private Task RenewDhcpAsync() => RunAsync(async ct =>
    {
        var done = await desktop.RunIpConfigAsync(true, ct);
        Network = await networkService.GetAsync(ct);
        Status = done ? "Đã DHCP Renew; thao tác không bảo đảm đổi public IP." : "Đã hủy DHCP Renew.";
    });

    [RelayCommand]
    private Task OpenWifiAsync() => RunAsync(ct =>
    {
        desktop.OpenSettings(true);
        Status = "Đã mở Windows Wi-Fi Settings.";
        return Task.CompletedTask;
    });

    [RelayCommand]
    private Task OpenRandomAddressAsync() => RunAsync(ct =>
    {
        desktop.OpenSettings(false);
        Status = "Đã mở cài đặt Wi-Fi. Mục địa chỉ phần cứng ngẫu nhiên phụ thuộc Windows và driver.";
        return Task.CompletedTask;
    });

    [RelayCommand]
    private Task PickBrowserAsync(BrowserCardViewModel? card) => RunAsync(ct =>
    {
        if (card is not null && !card.IsRunning && desktop.PickExecutable() is { } file)
            card.ExecutablePath = file;
        Status = "Lựa chọn trình duyệt được lưu khi khởi chạy hoặc lưu cài đặt.";
        return Task.CompletedTask;
    });

    [RelayCommand]
    private void AutoDetectBrowser(BrowserCardViewModel? card)
    {
        if (card is null || card.IsRunning) return;
        card.ExecutablePath = "";
        Status = $"Đã đặt Profile {card.Id} về chế độ tự phát hiện trình duyệt.";
        StatusKind = "THÔNG TIN";
        StatusSummary = "Sẵn sàng";
    }

    [RelayCommand]
    private Task OpenProfilesAsync() => RunAsync(ct =>
    {
        Directory.CreateDirectory(paths.Profiles);
        desktop.OpenFolder(paths.Profiles);
        Status = "Đã mở thư mục profile riêng.";
        return Task.CompletedTask;
    });

    [RelayCommand]
    private Task OpenDataFolderAsync() => RunAsync(ct =>
    {
        Directory.CreateDirectory(paths.Root);
        desktop.OpenFolder(paths.Root);
        Status = "Đã mở thư mục dữ liệu ứng dụng.";
        return Task.CompletedTask;
    });

    [RelayCommand]
    private Task CopyAsync(string? value) => RunAsync(ct =>
    {
        desktop.Copy(value);
        Status = "Đã sao chép vào bộ nhớ tạm.";
        return Task.CompletedTask;
    });

    [RelayCommand]
    private Task ExportResultsAsync() => RunAsync(async ct =>
    {
        Status = await desktop.ExportAsync(FilteredResults, ct) ? "Đã xuất kết quả CSV." : "Đã hủy xuất file.";
    });

    [RelayCommand]
    private Task ExportLogsAsync() => RunAsync(async ct =>
    {
        Status = await desktop.ExportLogsAsync(FilteredLogs, ct) ? "Đã xuất nhật ký CSV." : "Đã hủy xuất nhật ký.";
    });

    [RelayCommand]
    private Task RefreshLogsAsync() => RunAsync(async ct =>
    {
        Logs.Clear();
        foreach (var entry in await store.GetLogsAsync(ct)) Logs.Add(entry);
        Status = "Đã tải nhật ký.";
    });

    [RelayCommand]
    private void ClearLogFilter()
    {
        LogLevelFilterIndex = 0;
        LogSearchText = "";
        Status = "Đã xóa bộ lọc nhật ký.";
    }

    [RelayCommand]
    private void TogglePasswordVisibility() => IsPasswordVisible = !IsPasswordVisible;

    [RelayCommand]
    private void ClearExpirationDate() => ExpirationDate = null;

    [RelayCommand]
    private void OpenProxySettings() => SelectedPageIndex = 1;

    [RelayCommand]
    private void OpenBrowserManager() => SelectedPageIndex = 2;

    [RelayCommand]
    private void OpenIpChecker() => SelectedPageIndex = 3;

    [RelayCommand]
    private void ResetDefaultSettings()
    {
        EchoEndpoint = "https://api.ipify.org";
        TimeoutSeconds = "20";
        Status = "Đã đặt lại thông số kiểm tra mặc định. Bấm Lưu cài đặt để áp dụng.";
        StatusKind = "THÔNG TIN";
        StatusSummary = "Sẵn sàng";
    }

    public async Task ShutdownAsync()
    {
        closing = true;
        operation?.Cancel();
        while (IsBusy) await Task.Delay(30);
        await launcher.StopBothAsync();
    }
}
