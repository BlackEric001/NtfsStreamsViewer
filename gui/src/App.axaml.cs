using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using NtfsStreamsViewer.Services;
using NtfsStreamsViewer.ViewModels;
using NtfsStreamsViewer.Views;

namespace NtfsStreamsViewer;

public partial class App : Application
{
    private CoreProcessService? _processService;
    private JsonRpcService? _rpcService;
    private CoreClient? _coreClient;
    private MainViewModel? _mainViewModel;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Composition root: wire up the service layer (SPEC.md §6.2).
            _processService = new CoreProcessService();
            _rpcService = new JsonRpcService(_processService);
            _coreClient = new CoreClient(_processService, _rpcService);
            _mainViewModel = new MainViewModel(_coreClient, _rpcService);

            desktop.MainWindow = new MainWindow
            {
                DataContext = _mainViewModel,
            };

            desktop.Exit += OnExit;

            _ = InitializeAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private async Task InitializeAsync()
    {
        try
        {
            await _processService!.EnsureStartedAsync();
            await _mainViewModel!.InitializeAsync();
        }
        catch (Exception ex)
        {
            // Sidecar failed to start — MainViewModel surfaces the error in the status bar
            // and the crash dialog offers a restart (docs/GUI.md Error Handling).
            _mainViewModel!.StatusMessage = $"Cannot start ads-cli: {ex.Message}";
        }
    }

    private void OnExit(object? sender, ControlledApplicationLifetimeExitEventArgs e)
    {
        // Graceful shutdown of the sidecar. Fire-and-forget with a bounded wait so
        // we never hang the process on exit.
        try
        {
            var task = DisposeServicesAsync();
            if (!task.Wait(TimeSpan.FromSeconds(5)))
                _processService?.ForceTerminate();
        }
        catch
        {
            // best effort
        }
    }

    private async Task DisposeServicesAsync()
    {
        if (_coreClient is not null) await _coreClient.DisposeAsync();
        if (_rpcService is not null) await _rpcService.DisposeAsync();
        if (_processService is not null) await _processService.DisposeAsync();
    }
}
