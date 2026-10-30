using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using NtfsStreamsViewer.ViewModels;

namespace NtfsStreamsViewer.Views;

/// <summary>
/// Standalone preview window hosting <see cref="StreamPreviewView"/>.
/// Opened from <see cref="MainWindow"/> when the main view is showing a stream in a
/// separate window (docs/GUI.md: embedded preview + optional detached window).
/// </summary>
public partial class StreamPreviewWindow : Window
{
    public StreamPreviewWindow()
    {
        InitializeComponent();
    }

    public StreamPreviewWindow(StreamPreviewViewModel viewModel)
        : this()
    {
        DataContext = viewModel;
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
