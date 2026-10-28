using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using NetworkProfileManager.Core.Models;

namespace NetworkProfileManager.App.Services;

public sealed class DesktopService
{
    public bool Confirm(string message) => MessageBox.Show(message, "Xác nhận thao tác mạng", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    public string? PickExecutable()
    {
        var dialog = new OpenFileDialog { Title = "Chọn trình duyệt", Filter = "Browser executable (*.exe)|*.exe", CheckFileExists = true };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public void Copy(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text == "—") throw new InvalidOperationException("Chưa có dữ liệu để sao chép.");
        Clipboard.SetText(text);
    }

    public void OpenSettings(bool wifiDetails) => Process.Start(new ProcessStartInfo(wifiDetails ? "ms-settings:network-wifi" : "ms-settings:network-wifisettings") { UseShellExecute = true });

    public void OpenFolder(string path) => Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { path }, UseShellExecute = false });

    public async Task<bool> RunIpConfigAsync(bool renew, CancellationToken ct)
    {
        var warning = renew
            ? "DHCP Renew có thể làm gián đoạn kết nối mạng và không bảo đảm đổi public IP. Tiếp tục?"
            : "Xóa bộ nhớ đệm DNS của Windows? Windows có thể yêu cầu quyền quản trị.";
        if (!Confirm(warning)) return false;
        using var process = Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "ipconfig.exe"), renew ? "/renew" : "/flushdns")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true }) ?? throw new InvalidOperationException("Không khởi chạy được ipconfig.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(60));
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token); var error = process.StandardError.ReadToEndAsync(timeout.Token);
        try { await process.WaitForExitAsync(timeout.Token); await Task.WhenAll(output, error); }
        catch { if (!process.HasExited) process.Kill(true); throw; }
        if (process.ExitCode != 0) throw new InvalidOperationException("Windows từ chối thao tác mạng hoặc adapter không sẵn sàng; thao tác này có thể cần quyền quản trị.");
        return true;
    }

    public async Task<bool> ExportAsync(IEnumerable<IpTestResult> results, CancellationToken ct)
    {
        var dialog = new SaveFileDialog { FileName = $"IP-tests-{DateTime.Now:yyyyMMdd-HHmmss}.csv", Filter = "CSV (*.csv)|*.csv" };
        if (dialog.ShowDialog() != true) return false;
        await ExportResultsToPathAsync(results, dialog.FileName, ct);
        return true;
    }

    public static Task ExportResultsToPathAsync(IEnumerable<IpTestResult> results, string fileName, CancellationToken ct)
    {
        var lines = new List<string> { "Profile,IP,Status,ResponseMs,Timestamp,ProxyEndpoint,Error" };
        lines.AddRange(results.Select(r => string.Join(",", new[] { r.Profile, r.Ip, r.Status, r.ElapsedMs.ToString(), r.Timestamp.ToString("O"), r.Endpoint, r.Error }.Select(CsvCell))));
        return File.WriteAllLinesAsync(fileName, lines, new UTF8Encoding(true), ct);
    }

    public async Task<bool> ExportLogsAsync(IEnumerable<LogEntry> logs, CancellationToken ct)
    {
        var dialog = new SaveFileDialog { FileName = $"NPM-logs-{DateTime.Now:yyyyMMdd-HHmmss}.csv", Filter = "CSV (*.csv)|*.csv" };
        if (dialog.ShowDialog() != true) return false;
        var lines = new List<string> { "Timestamp,Level,Message" };
        lines.AddRange(logs.Select(l => string.Join(",", new[] { l.Time.ToString("O"), l.Level, l.Message }.Select(CsvCell))));
        await File.WriteAllLinesAsync(dialog.FileName, lines, new UTF8Encoding(true), ct);
        return true;
    }

    private static string CsvCell(string? text)
    {
        text ??= "";
        if (text.TrimStart().StartsWith('=') || text.TrimStart().StartsWith('+') || text.TrimStart().StartsWith('-') || text.TrimStart().StartsWith('@'))
            text = "'" + text;
        return "\"" + text.Replace("\"", "\"\"") + "\"";
    }

    public void ApplyTheme(bool light)
    {
        (string Key, string Dark, string Light)[] palette =
        [
            ("BackgroundBrush", "#10151F", "#EDF2F7"),
            ("SidebarBrush", "#0C1018", "#F5F8FC"),
            ("HeaderBrush", "#131924", "#F8FAFC"),
            ("SurfaceBrush", "#19212D", "#FFFFFF"),
            ("SubtleBrush", "#141C27", "#F3F6FA"),
            ("InputBrush", "#222D3D", "#F4F7FA"),
            ("HoverBrush", "#263346", "#E8EFF7"),
            ("NavSelectedBrush", "#1D2B3A", "#E1F5EE"),
            ("BorderBrush", "#303D4E", "#D5DFEB"),
            ("SubtleBorderBrush", "#232E3D", "#E4ECF4"),
            ("TextBrush", "#EBF1F8", "#192A3E"),
            ("SecondaryTextBrush", "#C2D0E0", "#33475B"),
            ("MutedBrush", "#9FAFC3", "#52657A"),
            ("AccentBrush", "#61D9B1", "#38C89B"),
            ("AccentTextBrush", "#7BE2BF", "#08745A"),
            ("ErrorBrush", "#FF9EAA", "#B82C48"),
            ("ErrorBgBrush", "#351C24", "#FDECEE"),
            ("WarningBrush", "#EFC77B", "#88610B"),
            ("WarningBgBrush", "#332915", "#FEF6E0"),
            ("SuccessBgBrush", "#143029", "#E2F7EF"),
            ("BadgeNeutralBgBrush", "#222D3D", "#E9EFF6")
        ];
        foreach (var color in palette)
            Application.Current.Resources[color.Key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(light ? color.Light : color.Dark));
    }
}
