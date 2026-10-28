using System.ComponentModel;
using System.Windows;
using NetworkProfileManager.App.ViewModels;

namespace NetworkProfileManager.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel vm;
    private bool cleaned;
    private bool shuttingDown;

    public MainWindow(MainViewModel vm)
    {
        InitializeComponent();
        this.vm = vm;
        DataContext = vm;
        Loaded += (_, _) => vm.UpdateResponsiveLayout(ActualWidth > 0 ? ActualWidth : Width);
        SizeChanged += (_, e) => vm.UpdateResponsiveLayout(e.NewSize.Width);
        Closing += OnClosing;
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (cleaned) return;
        e.Cancel = true;
        IsEnabled = false;
        if (shuttingDown) return;
        shuttingDown = true;
        try
        {
            await vm.ShutdownAsync();
        }
        finally
        {
            cleaned = true;
            // Closing may complete synchronously when no profiles are running. Defer the second
            // Close until WPF has returned from the current Closing event.
            _ = Dispatcher.BeginInvoke(new Action(Close));
        }
    }
}
