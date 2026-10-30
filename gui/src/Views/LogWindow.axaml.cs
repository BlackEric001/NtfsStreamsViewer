using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using NtfsStreamsViewer.Services;
using NtfsStreamsViewer.ViewModels;

namespace NtfsStreamsViewer.Views;

/// <summary>
/// Detached diagnostics window: live application log + per-path scan error details
/// (docs/GUI.md — Error Handling). Owned by <see cref="MainWindow"/>.
/// </summary>
public partial class LogWindow : Window
{
    private ScrollViewer? _scroll;
    private LogWindowViewModel? _vm;

    public LogWindow()
    {
        InitializeComponent();
        Opened += OnOpenedHandler;
        Closed += OnClosedHandler;
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void OnOpenedHandler(object? sender, EventArgs e)
    {
        _scroll = this.FindControl<ScrollViewer>("LogScroll");
        _vm = DataContext as LogWindowViewModel;
        if (_vm is null)
            return;

        // Clipboard for "Copy all".
        var clipboard = this.Clipboard;
        if (clipboard is not null)
        {
            _vm.SetClipboardText = text =>
                text is null ? clipboard.ClearAsync() : clipboard.SetTextAsync(text);
        }

        // Save-file dialog owned by this window's storage provider.
        _vm.PickSavePathAsync = async () =>
        {
            var provider = StorageProvider;
            if (provider is null || !provider.CanSave)
                return null;
            var file = await provider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save application log",
                SuggestedFileName = "ntfs-streams-viewer.log",
                FileTypeChoices = new[]
                {
                    new FilePickerFileType("Log files") { Patterns = new[] { "*.log", "*.txt" } },
                },
            });
            return file?.Path.LocalPath;
        };

        // Auto-scroll: follow the tail of the log whenever entries are appended.
        _vm.Entries.CollectionChanged += OnEntriesChanged;
    }

    private void OnEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_vm?.AutoScroll != true || _scroll is null)
            return;
        if (e.Action == NotifyCollectionChangedAction.Add ||
            e.Action == NotifyCollectionChangedAction.Reset)
        {
            // Defer to the layout pass so Extent already includes the new lines.
            _scroll.ScrollToEnd();
        }
    }

    private void OnClosedHandler(object? sender, EventArgs e)
    {
        if (_vm is not null)
        {
            _vm.Entries.CollectionChanged -= OnEntriesChanged;
            _vm.Detach();
            _vm = null;
        }
    }
}
