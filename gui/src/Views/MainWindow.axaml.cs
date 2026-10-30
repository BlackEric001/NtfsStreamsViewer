using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using NtfsStreamsViewer.Models;
using NtfsStreamsViewer.Services;
using NtfsStreamsViewer.ViewModels;

namespace NtfsStreamsViewer.Views;

public partial class MainWindow : Window
{
    private LogWindow? _logWindow;
    private MainViewModel? _wiredVm;

    public MainWindow()
    {
        InitializeComponent();
#if DEBUG
        this.AttachDevTools();
#endif
        DataContextChanged += (_, _) => WireViewModel();
        Opened += (_, _) => WireViewModel();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    /// <summary>
    /// Subscribes to the log-window related events of the ViewModel exactly once,
    /// even if DataContext changes or OnOpened fires repeatedly.
    /// </summary>
    private void WireViewModel()
    {
        if (DataContext is not MainViewModel vm || ReferenceEquals(vm, _wiredVm))
            return;
        _wiredVm = vm;
        vm.OpenLogRequested += (_, _) => ShowLogWindow();
        vm.ScanErrorsWithDetailsDetected += OnScanErrorsWithDetails;
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (_wiredVm is { } vm)
        {
            vm.CoreCrashDetected -= OnCoreCrashDetected;
            vm.CoreCrashDetected += OnCoreCrashDetected;
            // ExportRequested is handled by MainView (it wires the dialog with a proper owner).
        }
    }

    private async void OnCoreCrashDetected(object? sender, string message)
    {
        // A core crash must never take the GUI down — offer a restart instead (docs/GUI.md).
        if (DataContext is MainViewModel vm)
        {
            var restart = await Dialog.ConfirmAsync(this, "Core process crashed", message);
            if (restart)
                await vm.RestartCoreCommand.ExecuteAsync(null);
        }
    }

    /// <summary>Opens (or activates) the diagnostics window. Safe to call repeatedly.</summary>
    public void ShowLogWindow()
    {
        if (DataContext is not MainViewModel vm)
            return;
        vm.NotifyLogWindowOpened();

        if (_logWindow is { IsVisible: true })
        {
            _logWindow.Activate();
            return;
        }

        // NOTE: WindowBase.Owner has a protected setter in this Avalonia version,
        // so we cannot assign it here; the log window opens as a normal top-level
        // window centered on screen instead (docs/GUI.md).
        _logWindow = new LogWindow
        {
            DataContext = new LogWindowViewModel(vm),
        };
        _logWindow.Closed += (_, _) => _logWindow = null;
        _logWindow.Show();
    }

    /// <summary>
    /// After a scan finished with detailed errors (and the user never opened the log
    /// window), offer to show them right away instead of leaving a bare counter behind.
    /// </summary>
    private async void OnScanErrorsWithDetails(object? sender, ScanCompletedNotification n)
    {
        AppLogger.Warning("scan",
            $"Scan completed with {n.Errors.Count} recorded error detail(s) " +
            $"({n.ErrorCount} total). First: {n.Errors.FirstOrDefault()}");

        var message =
            $"The scan finished with {n.ErrorCount} error(s); " +
            $"{Math.Min(n.Errors.Count, 1000)} detailed entries were collected.\n\n" +
            "Open the log window to inspect them?";

        if (await Dialog.ConfirmAsync(this, "Scan completed with errors", message))
            ShowLogWindow();
    }
}
