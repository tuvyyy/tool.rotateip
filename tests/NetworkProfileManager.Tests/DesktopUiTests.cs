using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Windows.Data;
using NetworkProfileManager.App.Services;
using NetworkProfileManager.App.ViewModels;
using NetworkProfileManager.Core.Models;
using NetworkProfileManager.Core.Services;
using NetworkProfileManager.Infrastructure.BrowserAdapters;
using NetworkProfileManager.Infrastructure.ProxyAdapters;
using NetworkProfileManager.Infrastructure.Services;
using NetworkProfileManager.Infrastructure.SessionProviders;
using NetworkProfileManager.Infrastructure.Storage;
using Xunit;

namespace NetworkProfileManager.Tests;

[Collection("QA browser lifecycle")]
public class DesktopUiTests
{
    private sealed class BindingTraceCollector : TraceListener
    {
        public List<string> Errors { get; } = [];
        public override void Write(string? message) { }
        public override void WriteLine(string? message)
        {
            if (!string.IsNullOrWhiteSpace(message))
                Errors.Add(message);
        }
    }

    [Fact]
    [Trait("Category", "WindowsDesktop")]
    public async Task WpfViewsRenderBrowserLabelsThemesAndAllSevenPages()
    {
        using var folder = new TestFolder();
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            var bindingTrace = new BindingTraceCollector();
            PresentationTraceSources.Refresh();
            PresentationTraceSources.DataBindingSource.Listeners.Add(bindingTrace);
            PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning | SourceLevels.Error;

            var app = new NetworkProfileManager.App.App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.InitializeComponent();
            _ = Dispatcher.CurrentDispatcher.InvokeAsync(async () =>
            {
                try
                {
                    var store = new SqliteSettingsStore(folder.Paths); await store.InitializeAsync();
                    var connector = new UpstreamProxyConnector(); var sessions = new StandardSessionProvider(); var desktop = new DesktopService();
                    await using var launcher = new BrowserLauncherService(new BrowserProfileService(folder.Paths), connector, sessions);
                    var vm = new MainViewModel(store, new FixtureNetwork(), new IpCheckerService(connector, sessions), launcher, desktop, folder.Paths);
                    await vm.InitializeAsync();
                    var window = new NetworkProfileManager.App.MainWindow(vm)
                    { WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000, Width = 1280, Height = 780, ShowInTaskbar = false };
                    window.Show(); window.UpdateLayout();

                    Assert.Equal(ColorConverter.ConvertFromString("#10151F"), ((SolidColorBrush)window.Background).Color);

                    // Verify Page 0 (Overview) is concise summary, not duplicating full exe form
                    var text = Descendants<TextBlock>(window).Select(t => t.Text).ToArray();
                    Assert.Contains("Chrome", text);
                    Assert.Contains("Microsoft Edge", text);
                    Assert.DoesNotContain(text, t => t.Contains("BrowserOption {"));
                    Assert.Contains("1 gateway • 2 profile độc lập", text);
                    Assert.DoesNotContain(text, t => t.Contains("Standard Rotation không bảo đảm"));
                    Assert.DoesNotContain(text, t => t.Contains("Đây là IP của request"));
                    Assert.Empty(Descendants<TextBox>(window)); // No input TextBoxes on Dashboard Summary

                    Assert.Equal("Sẵn sàng", ((TextBlock)window.FindName("GlobalStatusText")).Text);
                    Assert.DoesNotContain(Descendants<TextBlock>(window), t => t.IsVisible && BindingOperations.GetBindingExpression(t, TextBlock.TextProperty)?.ParentBinding.Path.Path == "Status");
                    var help = Descendants<Button>(window).Where(b => b.Style == app.FindResource("HelpButton")).ToArray();
                    Assert.True(help.Length >= 8);
                    Assert.All(help, b => Assert.NotNull(b.ToolTip));

                    // Hover explanations are also available to keyboard users through the help button.
                    help[0].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    var tip = Assert.IsType<ToolTip>(help[0].ToolTip);
                    Assert.True(tip.IsOpen);
                    tip.IsOpen = false;

                    Capture(window, "overview-dark.png");
                    vm.LightTheme = true; window.UpdateLayout(); Capture(window, "overview-light.png"); vm.LightTheme = false;
                    window.Width = 1060; window.Height = 650; window.UpdateLayout();
                    Capture(window, "overview-compact.png");
                    window.Width = 1280; window.Height = 780; window.UpdateLayout();

                    var tabs = Assert.Single(Descendants<TabControl>(window));
                    Assert.Equal(7, tabs.Items.Count);
                    for (var page = 0; page < 7; page++)
                    {
                        tabs.SelectedIndex = page;
                        window.UpdateLayout();
                        await Dispatcher.Yield(DispatcherPriority.Background);
                        Assert.NotNull(tabs.SelectedContent);
                        Assert.NotEmpty(Descendants<TextBlock>(window));

                        var pageTexts = Descendants<TextBlock>(window).Select(t => t.Text).ToArray();
                        Assert.DoesNotContain("Select a date", pageTexts);

                        if (page == 1)
                        {
                            Assert.Single(Descendants<PasswordBox>(window));
                            Assert.Contains("Chưa đặt ngày hết hạn", pageTexts);
                            // Verify password show/hide toggle and automatic reset on tab switch
                            Assert.False(vm.IsPasswordVisible);
                            vm.TogglePasswordVisibilityCommand.Execute(null);
                            Assert.True(vm.IsPasswordVisible);
                        }
                        if (page == 2)
                        {
                            Assert.False(vm.IsPasswordVisible); // Auto-reset when leaving page 1
                            Assert.True(Descendants<TextBox>(window).Count() >= 4); // Name + Exe path for A & B
                        }
                        if (page == 3)
                        {
                            Assert.Equal(6, Assert.Single(Descendants<DataGrid>(window)).Columns.Count);
                            Assert.Contains("Chưa có kết quả kiểm tra", pageTexts);
                        }
                        if (page == 4)
                        {
                            Assert.Contains(Descendants<TextBlock>(window), t => t.Text == "Fixture Wi-Fi");
                        }
                        if (page == 5)
                        {
                            Assert.Contains("Chưa có nhật ký hiển thị", pageTexts);
                        }
                    }

                    // Test responsive window sizes without clipping or binding errors
                    foreach (var (w, h) in new[] { (1100, 700), (1280, 720), (1366, 768), (1600, 900), (1920, 1080) })
                    {
                        window.Width = w;
                        window.Height = h;
                        window.UpdateLayout();
                        await Dispatcher.Yield(DispatcherPriority.Background);
                    }

                    vm.LightTheme = true; window.UpdateLayout();
                    Assert.Equal(ColorConverter.ConvertFromString("#EDF2F7"), ((SolidColorBrush)window.Background).Color);
                    vm.LightTheme = false;

                    await RunVisualMatrix(window, vm, tabs, app);
                    QaEvidence.Write("logs/ui-binding-errors.json", bindingTrace.Errors);
                    Assert.Throws<InvalidOperationException>(() => desktop.Copy(null));
                    Assert.Throws<InvalidOperationException>(() => desktop.Copy("—"));
                    var clipboard = Clipboard.GetDataObject();
                    try { desktop.Copy("203.0.113.9"); Assert.Equal("203.0.113.9", Clipboard.GetText()); }
                    finally { if (clipboard is not null) Clipboard.SetDataObject(clipboard, true); else Clipboard.Clear(); }

                    // Sticky is a real validation error before any network activity.
                    vm.Host = "proxy.test"; vm.RotationIndex = 1;
                    await vm.TestGatewayCommand.ExecuteAsync(null);
                    Assert.Equal("LỖI", vm.StatusKind);
                    Assert.Equal("Sticky Session chưa được cấu hình", vm.Status);
                    Assert.Equal("Proxy lỗi", vm.StatusSummary);

                    vm.Cards[0].ShowProxyError("Proxy từ chối xác thực (HTTP 407). Kiểm tra username/password.");
                    Assert.Equal("Proxy lỗi", vm.Cards[0].StatusLabel);
                    Assert.Contains("407", vm.Cards[0].ConnectionStatus);

                    Assert.Empty(bindingTrace.Errors);

                    var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    window.Closed += (_, _) => closed.TrySetResult();
                    window.Close();
                    await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    await CloseWhileBrowsersAndRequestAreActive();
                    finished.TrySetResult();
                }
                catch (Exception ex) { finished.TrySetException(ex); }
                finally
                {
                    PresentationTraceSources.DataBindingSource.Listeners.Remove(bindingTrace);
                    app.Shutdown();
                }
            });
            Dispatcher.Run();
        }) { IsBackground = true, Name = "WPF UI verification" };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(120)); thread.Join(5000);
    }

    private static async Task CloseWhileBrowsersAndRequestAreActive()
    {
        using var folder = new TestFolder(); await using var gateway = new MockProxy { TlsEcho = true };
        var store = new SqliteSettingsStore(folder.Paths); await store.InitializeAsync();
        await using var launcher = new BrowserLauncherService(new(folder.Paths), new(), new StandardSessionProvider(), QaBrowserMatrixTests.Headless);
        var vm = new MainViewModel(store, new FixtureNetwork(), QaProtocolTests.Checker(gateway), launcher, new DesktopService(), folder.Paths);
        await vm.InitializeAsync();
        vm.Host = gateway.Configuration.Host; vm.Port = gateway.Port.ToString(); vm.Username = gateway.Configuration.Username; vm.Password = gateway.Configuration.Password;
        vm.EchoEndpoint = "https://echo.test"; vm.TimeoutSeconds = "3";
        var window = new NetworkProfileManager.App.MainWindow(vm)
        { WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000, ShowInTaskbar = false, ShowActivated = false };
        window.Show(); await vm.StartBothCommand.ExecuteAsync(null);
        Assert.True(launcher.IsRunning("A")); Assert.True(launcher.IsRunning("B"));
        var owned = QaBrowserMatrixTests.CaptureProcesses(launcher, "A").Concat(QaBrowserMatrixTests.CaptureProcesses(launcher, "B")).ToArray();
        gateway.ResponseDelayMs = 10000;
        var pending = vm.TestProfileCommand.ExecuteAsync(vm.Cards[0]); await Task.Delay(100); Assert.True(vm.IsBusy);
        vm.SelectedPageIndex = 5; vm.LightTheme = !vm.LightTheme; window.UpdateLayout();
        await Dispatcher.Yield(DispatcherPriority.Background);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); window.Closed += (_, _) => closed.TrySetResult();
        window.Close(); await closed.Task.WaitAsync(TimeSpan.FromSeconds(12)); await pending;
        await QaBrowserMatrixTests.AssertExited(owned); Assert.Null(launcher.GetPort("A")); Assert.Null(launcher.GetPort("B"));
        QaEvidence.Write("logs/close-while-active.json", new { Status = "PASS", Browsers = 2, RequestCancelled = true, ManagedProcessesExited = true, ListenersClosed = true });
    }

    private static void Capture(Window window, string fileName, double dpi = 96)
    {
        window.UpdateLayout();
        var content = (FrameworkElement)window.Content;
        var bitmap = new RenderTargetBitmap(
            (int)Math.Ceiling((content.ActualWidth + content.Margin.Left + content.Margin.Right) * dpi / 96),
            (int)Math.Ceiling((content.ActualHeight + content.Margin.Top + content.Margin.Bottom) * dpi / 96), dpi, dpi, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var directory = Path.Combine(QaEvidence.Root, "ui-redesign"); Directory.CreateDirectory(directory);
        var path = Path.GetFullPath(Path.Combine(directory, fileName)); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var file = File.Create(path); encoder.Save(file);
    }

    private static async Task RunVisualMatrix(Window window, MainViewModel vm, TabControl tabs, Application app)
    {
        var matrix = new List<object>();
        var sizes = new[] { (1100, 700), (1280, 720), (1366, 768), (1600, 900), (1920, 1080) };
        foreach (var light in new[] { false, true })
        {
            vm.LightTheme = light;
            AssertContrast(app, "TextBrush", "SurfaceBrush"); AssertContrast(app, "MutedBrush", "SurfaceBrush");
            foreach (var (width, height) in sizes)
            {
                window.Width = width; window.Height = height;
                for (var page = 0; page < 7; page++)
                {
                    tabs.SelectedIndex = page; window.UpdateLayout(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    Assert.Equal(vm.PageTitle, ((TabItem)tabs.SelectedItem).Header);
                    var action = Assert.Single(Descendants<Button>(window), b => Equals(b.Content, "Khởi chạy cả hai"));
                    Assert.Equal(page is 0 or 2, action.IsVisible);
                    if (action.IsVisible)
                    {
                        var position = action.TranslatePoint(new Point(), window);
                        Assert.True(position.X >= 0 && position.X + action.ActualWidth < window.ActualWidth, "Header action overflow");
                    }
                    if (page == 6)
                    {
                        var timeout = Assert.Single(Descendants<TextBox>(window), t => BindingOperations.GetBindingExpression(t, TextBox.TextProperty)?.ParentBinding.Path.Path == "TimeoutSeconds");
                        var input = new Rect(timeout.TranslatePoint(new Point(), window), timeout.RenderSize);
                        foreach (var button in Descendants<Button>(window).Where(b => b.IsVisible && Equals(b.Content, "Mặc định")))
                        {
                            var bounds = new Rect(button.TranslatePoint(new Point(), window), button.RenderSize);
                            Assert.False(input.IntersectsWith(bounds), "Timeout input overlaps reset button at " + width);
                        }
                        var about = Descendants<TextBlock>(window).Where(t => t.Text is "HTTP CONNECT & SOCKS5 Loopback Bridge" or "Windows Job Object (KILL_ON_JOB_CLOSE)" or "Hoạt động 100% cục bộ, không thu thập telemetry").ToArray();
                        for (var item = 1; item < about.Length; item++)
                            Assert.False(new Rect(about[item - 1].TranslatePoint(new Point(), window), about[item - 1].RenderSize)
                                .IntersectsWith(new Rect(about[item].TranslatePoint(new Point(), window), about[item].RenderSize)), "About row text overlap");
                    }
                    Capture(window, $"../screenshots/{(light ? "light" : "dark")}-{width}x{height}-page-{page}.png");
                    matrix.Add(new { Theme = light ? "Light" : "Dark", Width = width, Height = height, Page = page, Status = "PASS" });
                }
            }
        }
        vm.Results.Add(new("A", "2001:db8:ffff:ffff:ffff:ffff:ffff:ffff", true, 34, DateTimeOffset.UtcNow, "http://very-long-qa-gateway.example.test:65535", null));
        vm.Logs.Add(new(DateTimeOffset.UtcNow, "Warning", "QA warning: " + new string('x', 220)));
        window.Width = 1280; window.Height = 720; tabs.SelectedIndex = 3; window.UpdateLayout(); Capture(window, "../screenshots/populated-ip-grid.png");
        tabs.SelectedIndex = 5; window.UpdateLayout(); Capture(window, "../screenshots/populated-logs.png");
        vm.Results.Clear(); vm.Logs.Clear();
        for (var i = 0; i < 50; i++)
        { tabs.SelectedIndex = i % 7; vm.LightTheme = i % 2 == 0; window.Width = sizes[i % 5].Item1; window.Height = sizes[i % 5].Item2; window.UpdateLayout(); }
        tabs.SelectedIndex = 0; window.Width = 1280; window.Height = 720; vm.LightTheme = false; window.UpdateLayout();
        foreach (var dpi in new[] { 96, 120, 144 }) Capture(window, $"../screenshots/render-scale-{dpi}.png", dpi);
        QaEvidence.Write("logs/ui-matrix.json", new { Cases = matrix, NativeDpi = VisualTreeHelper.GetDpi(window).PixelsPerInchX,
            RenderScalesDpi = new[] { 96, 120, 144 }, TabThemeResizeCycles = 50 });
    }

    private static void AssertContrast(Application app, string foreground, string background)
    {
        static double L(Color c)
        {
            static double Linear(byte b) { var v = b / 255d; return v <= .04045 ? v / 12.92 : Math.Pow((v + .055) / 1.055, 2.4); }
            return .2126 * Linear(c.R) + .7152 * Linear(c.G) + .0722 * Linear(c.B);
        }
        var a = L(((SolidColorBrush)app.Resources[foreground]).Color); var b = L(((SolidColorBrush)app.Resources[background]).Color);
        Assert.True((Math.Max(a, b) + .05) / (Math.Min(a, b) + .05) >= 4.5, foreground + " contrast below 4.5:1");
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i); if (child is T item) yield return item;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }

    private sealed class FixtureNetwork : INetworkInformationService
    {
        public Task<NetworkSnapshot> GetAsync(CancellationToken ct) => Task.FromResult(new NetworkSnapshot("Fixture Wi-Fi", "Test adapter", "10.0.0.2", "02:00:00:00:00:01", "10.0.0.1", "10.0.0.1", "Đang bật"));
    }
}
