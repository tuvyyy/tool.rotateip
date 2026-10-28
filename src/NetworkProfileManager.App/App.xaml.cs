using System.Windows;
using System.Windows.Controls;
using System.IO;
using Microsoft.Extensions.DependencyInjection;
using NetworkProfileManager.Core.Services;
using NetworkProfileManager.Infrastructure.NetworkServices;
using NetworkProfileManager.Infrastructure.Storage;
using NetworkProfileManager.App.ViewModels;
using NetworkProfileManager.App.Services;
using NetworkProfileManager.Infrastructure.ProxyAdapters;
using NetworkProfileManager.Infrastructure.SessionProviders;
using NetworkProfileManager.Infrastructure.BrowserAdapters;
using NetworkProfileManager.Infrastructure.Services;

namespace NetworkProfileManager.App;
public partial class App : Application
{
    private void HelpButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.ToolTip is null) return;
        if (element.ToolTip is not ToolTip tip)
        {
            tip = new ToolTip { Content = element.ToolTip, PlacementTarget = element, StaysOpen = false };
            element.ToolTip = tip;
        }
        tip.PlacementTarget = element;
        tip.IsOpen = true;
    }
    private ServiceProvider? services;
    private Mutex? instanceMutex;
    private bool ownsMutex;
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        AppPaths? paths = null;
        try
        {
            if (e.Args.Length != 0 && (e.Args.Length != 2 || e.Args[0] != "--data-directory" || !Path.IsPathFullyQualified(e.Args[1])))
                throw new ArgumentException("Tham số thư mục dữ liệu không hợp lệ.");
            paths = new AppPaths(e.Args.Length == 2 ? e.Args[1] : null);
            instanceMutex = new Mutex(true, paths.MutexName, out ownsMutex);
            if (!ownsMutex) { MessageBox.Show("Accre Rotate IP đang chạy với thư mục dữ liệu này.", "Accre Rotate IP"); Shutdown(); return; }
            services = new ServiceCollection().AddSingleton(paths)
                .AddSingleton<ISettingsStore, SqliteSettingsStore>()
                .AddSingleton<INetworkInformationService, NetworkInformationService>()
                .AddSingleton<IProxySessionProvider, StandardSessionProvider>()
                .AddSingleton<UpstreamProxyConnector>().AddSingleton<IpCheckerService>()
                .AddSingleton<BrowserProfileService>().AddSingleton<BrowserLauncherService>()
                .AddSingleton<DesktopService>()
                .AddSingleton<MainViewModel>().AddSingleton<MainWindow>().BuildServiceProvider();
            await services.GetRequiredService<ISettingsStore>().InitializeAsync();
            var window = services.GetRequiredService<MainWindow>();
            MainWindow = window; window.Show();
            await services.GetRequiredService<MainViewModel>().InitializeAsync();
        }
        catch (Exception ex)
        {
            // No exception message or stack dump here: diagnostics must never serialize configuration values.
            if (paths is not null) try { File.AppendAllText(Path.Combine(paths.Root, "startup-error.log"), $"{DateTimeOffset.UtcNow:O} Startup failed ({ex.GetType().Name}).{Environment.NewLine}"); } catch { }
            MessageBox.Show("Không thể khởi tạo ứng dụng. Kiểm tra quyền truy cập và tính toàn vẹn thư mục dữ liệu.", "Accre Rotate IP"); Shutdown(1);
        }
    }
    protected override void OnExit(ExitEventArgs e)
    {
        if (ownsMutex) instanceMutex?.ReleaseMutex(); instanceMutex?.Dispose(); base.OnExit(e);
    }
}
