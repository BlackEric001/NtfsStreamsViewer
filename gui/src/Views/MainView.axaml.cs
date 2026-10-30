using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;
using NtfsStreamsViewer.Models;
using NtfsStreamsViewer.ViewModels;

namespace NtfsStreamsViewer.Views;

public partial class MainView : UserControl
{
    private MainViewModel? _wiredViewModel;

    public MainView()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    private void OnViewClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ScanStreamItem item })
            ViewModel?.OpenPreview(item.Stream);
    }

    private void OnExportClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ScanStreamItem item })
            ViewModel?.ExportStreamCommand.Execute(item);
    }

    private void OnResultDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (ViewModel?.SelectedResult is { } item)
            ViewModel.OpenPreview(item.Stream);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        WireViewModelEvents();
    }

    /// <summary>
    /// Subscribes to export requests once the view is attached to a window so modal
    /// dialogs get a proper owner (docs/GUI.md Error Handling / Export).
    /// </summary>
    private void WireViewModelEvents()
    {
        if (ViewModel is not { } vm || ReferenceEquals(vm, _wiredViewModel))
            return;

        var owner = this.FindAncestorOfType<Window>() ?? TopLevel.GetTopLevel(this) as Window;
        if (owner is null)
            return;

        _wiredViewModel = vm;
        vm.ExportRequested += (_, stream) =>
            _ = ExportDialog.ShowAsync(owner, vm.CoreClient, stream);
    }
}
