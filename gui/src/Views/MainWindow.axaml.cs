using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using NtfsStreamsViewer.Services;
using NtfsStreamsViewer.ViewModels;

namespace NtfsStreamsViewer.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
#if DEBUG
        this.AttachDevTools();
#endif
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (DataContext is MainViewModel vm)
        {
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
}
