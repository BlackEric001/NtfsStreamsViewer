using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace NtfsStreamsViewer.Views;

public partial class StreamPreviewView : UserControl
{
    public StreamPreviewView()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
