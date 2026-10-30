using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using NtfsStreamsViewer.Models;
using NtfsStreamsViewer.Services;
using NtfsStreamsViewer.ViewModels;

namespace NtfsStreamsViewer.Views;

/// <summary>
/// Small imperative dialog helpers (message box / confirm / export).
/// Avalonia has no built-in MessageBox, so we build lightweight modal windows here.
/// </summary>
public static class Dialog
{
    public static async Task<bool> ConfirmAsync(Window owner, string title, string message)
    {
        var result = false;

        var yes = new Button { Content = "Restart", MinWidth = 90 };
        var no = new Button { Content = "Close", MinWidth = 90 };

        var window = BuildWindow(title, message, owner);
        yes.Click += (_, _) =>
        {
            result = true;
            window.Close();
        };
        no.Click += (_, _) => window.Close();

        SetButtons(window, yes, no);
        await window.ShowDialog(owner);
        return result;
    }

    public static Task ShowMessageAsync(Window owner, string title, string message)
    {
        var ok = new Button { Content = "OK", MinWidth = 90 };
        var window = BuildWindow(title, message, owner);
        ok.Click += (_, _) => window.Close();
        SetButtons(window, ok);
        return window.ShowDialog(owner);
    }

    private static Window BuildWindow(string title, string message, Window owner)
    {
        var text = new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Avalonia.Thickness(0, 0, 0, 12),
            MaxWidth = 420,
        };

        var content = new StackPanel
        {
            Margin = new Avalonia.Thickness(16),
            Spacing = 4,
        };
        content.Children.Add(text);

        var window = new Window
        {
            Title = title,
            SizeToContent = SizeToContent.WidthAndHeight,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = content,
        };
        // No explicit Owner assignment: WindowBase.Owner has a protected setter in
        // Avalonia 11, and ShowDialog(owner) below already establishes the owner.
        return window;
    }

    private static void SetButtons(Window window, params Button[] buttons)
    {
        if (window.Content is not StackPanel panel)
            return;

        var row = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            Spacing = 8,
        };
        foreach (var b in buttons)
            row.Children.Add(b);
        panel.Children.Add(row);
    }
}

/// <summary>
/// Modal export dialog backed by <see cref="ExportViewModel"/> (core writes the file
/// via stream.export — SPEC.md §5). Shows the resulting SHA-256 on success.
/// </summary>
public static class ExportDialog
{
    public static async Task<ExportResult?> ShowAsync(Window owner, ICoreClient core, StreamInfo stream)
    {
        var vm = new ExportViewModel(core, stream);
        Window? window = null;

        var pathBox = new TextBox
        {
            Watermark = "Destination file",
            Text = vm.DestinationPath,
            Margin = new Avalonia.Thickness(0, 0, 0, 8),
        };
        // Two-way wiring without XAML bindings for brevity.
        pathBox.Bind(TextBox.TextProperty, new Binding(nameof(ExportViewModel.DestinationPath))
        {
            Source = vm,
            Mode = BindingMode.TwoWay,
        });

        var status = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 460,
            Margin = new Avalonia.Thickness(0, 8, 0, 8),
        };
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ExportViewModel.StatusMessage))
                status.Text = vm.StatusMessage;
            if (e.PropertyName == nameof(ExportViewModel.Result) && vm.Result is not null)
                window?.Close(vm.Result);
        };

        var exportBtn = new Button
        {
            Content = "Export",
            MinWidth = 90,
            Command = vm.ExportCommand,
        };
        var cancelBtn = new Button { Content = "Cancel", MinWidth = 90 };

        var browseBtn = new Button { Content = "Browse…", MinWidth = 90 };
        browseBtn.Click += async (_, _) =>
        {
            var dlg = new SaveFileDialogPolicy();
            var chosen = await dlg.PickAsync(owner, vm.DestinationPath);
            if (chosen is not null)
            {
                vm.DestinationPath = chosen;
                pathBox.Text = chosen;
            }
        };

        var buttons = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            Spacing = 8,
        };
        buttons.Children.Add(browseBtn);
        buttons.Children.Add(exportBtn);
        buttons.Children.Add(cancelBtn);

        var panel = new StackPanel { Margin = new Avalonia.Thickness(16), Width = 500 };
        panel.Children.Add(new TextBlock { Text = vm.Title, FontWeight = FontWeight.Bold, Margin = new Avalonia.Thickness(0, 0, 0, 8) });
        panel.Children.Add(new TextBlock { Text = "Source: " + stream.OwnerPath + ":" + stream.Name, TextWrapping = TextWrapping.Wrap, Margin = new Avalonia.Thickness(0, 0, 0, 8) });
        panel.Children.Add(pathBox);
        panel.Children.Add(status);
        panel.Children.Add(buttons);

        var dlg = new Window
        {
            Title = "Export stream",
            SizeToContent = SizeToContent.WidthAndHeight,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = panel,
        };
        window = dlg;

        cancelBtn.Click += (_, _) => dlg.Close();

        await dlg.ShowDialog(owner);
        return vm.Result;
    }

    /// <summary>Thin wrapper around Avalonia's StorageProvider save dialog.</summary>
    private sealed class SaveFileDialogPolicy
    {
        public async Task<string?> PickAsync(Window owner, string suggested)
        {
            var sp = owner.StorageProvider;
            if (sp is null || !sp.CanSave)
                return null;

            var options = new FilePickerSaveOptions
            {
                SuggestedFileName = suggested,
                DefaultExtension = "bin",
                Title = "Export destination",
            };
            // In Avalonia 11 the save picker is exposed as SaveFilePickerAsync.
            var file = await sp.SaveFilePickerAsync(options);
            return file?.TryGetLocalPath();
        }
    }
}
