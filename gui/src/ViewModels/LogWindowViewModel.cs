using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NtfsStreamsViewer.Models;
using NtfsStreamsViewer.Services;

namespace NtfsStreamsViewer.ViewModels;

/// <summary>A row in the "Scan errors" tab of the log window.</summary>
public sealed record ScanErrorRow(int Index, string Path, string Message)
{
    public override string ToString() => $"#{Index} {Path}: {Message}";
}

/// <summary>
/// State for <c>LogWindow</c>: live application log (from <see cref="AppLogger"/>)
/// plus a detailed list of scan errors collected from notifications and the
/// completion payload (docs/GUI.md Error Handling).
/// </summary>
public partial class LogWindowViewModel : ViewModelBase
{
    /// <summary>How many error details we keep at most (the core caps its own list at 1000).</summary>
    public const int MaxVisibleErrors = 2000;

    private readonly MainViewModel? _main;

    public ObservableCollection<LogEntry> Entries { get; } = new();
    public ObservableCollection<ScanErrorRow> Errors { get; } = new();

    [ObservableProperty]
    private string _filterText = string.Empty;

    [ObservableProperty]
    private bool _autoScroll = true;

    [ObservableProperty]
    private LogLevel _minimumLevel = AppLogger.MinimumLevel;

    [ObservableProperty]
    private int _errorCount;

    /// <summary>Set by the view so the "Copy all" command can put text on the clipboard.</summary>
    public Func<string?, Task>? SetClipboardText { get; set; }

    public string LogFilePath => AppLogger.LogFilePathDisplay;

    public LogWindowViewModel(MainViewModel? main)
    {
        _main = main;
        // Start with everything currently buffered, then follow live updates.
        foreach (var e in AppLogger.Snapshot())
            Entries.Add(e);
        if (_main is not null)
            _main.ScanErrorsChanged += OnScanErrorsChanged;
        AppLogger.EntriesAppended += OnEntriesAppended;
        FilterText = string.Empty; // triggers initial filtering
        RebuildErrors(_main?.ScanErrorDetails ?? Array.Empty<ScanErrorDetail>());
    }

    public void Detach()
    {
        AppLogger.EntriesAppended -= OnEntriesAppended;
        if (_main is not null)
            _main.ScanErrorsChanged -= OnScanErrorsChanged;
    }

    private void OnEntriesAppended(object? sender, IReadOnlyList<LogEntry> batch)
    {
        Dispatcher.UIThread.Post(() =>
        {
            foreach (var e in batch)
            {
                if (PassesFilter(e))
                    Entries.Add(e);
            }
            TrimEntries();
        }, DispatcherPriority.Background);
    }

    private void OnScanErrorsChanged(object? sender, IReadOnlyList<ScanErrorDetail> errors)
    {
        Dispatcher.UIThread.Post(() => RebuildErrors(errors));
    }

    private void RebuildErrors(IReadOnlyList<ScanErrorDetail> details)
    {
        Errors.Clear();
        var take = Math.Min(details.Count, MaxVisibleErrors);
        for (var i = 0; i < take; i++)
            Errors.Add(new ScanErrorRow(i + 1, details[i].Path, details[i].Message));
        ErrorCount = details.Count;
    }

    private bool PassesFilter(LogEntry e)
    {
        if (e.Level < MinimumLevel)
            return false;
        if (string.IsNullOrWhiteSpace(FilterText))
            return true;
        return e.Message.Contains(FilterText, StringComparison.OrdinalIgnoreCase) ||
               e.Category.Contains(FilterText, StringComparison.OrdinalIgnoreCase);
    }

    private void TrimEntries()
    {
        while (Entries.Count > AppLogger.MemoryCapacityUiLimit)
            Entries.RemoveAt(0);
    }

    partial void OnMinimumLevelChanged(LogLevel value)
    {
        AppLogger.MinimumLevel = value;
        OnPropertyChanged(nameof(MinimumLevelText));
        RebuildEntriesFromBuffer();
    }

    partial void OnFilterTextChanged(string value) => RebuildEntriesFromBuffer();

    private void RebuildEntriesFromBuffer()
    {
        Entries.Clear();
        foreach (var e in AppLogger.Snapshot())
        {
            if (PassesFilter(e))
                Entries.Add(e);
        }
        TrimEntries();
    }

    public string MinimumLevelText => MinimumLevel.ToString();

    [RelayCommand]
    private void ClearUi()
    {
        Entries.Clear();
        Errors.Clear();
        ErrorCount = 0;
        _main?.ClearScanErrors();
    }

    [RelayCommand]
    private async Task CopyAllAsync()
    {
        if (SetClipboardText is null)
            return;
        var text = string.Join(Environment.NewLine, Entries.Select(e => e.FullText));
        await SetClipboardText(text);
    }

    [RelayCommand]
    private void OpenLogFile()
    {
        var path = AppLogger.LogFilePathDisplay;
        if (string.IsNullOrEmpty(path) || path.StartsWith("("))
            return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            AppLogger.Warning("log", $"Cannot open log file '{path}': {ex.Message}");
        }
    }

    /// <summary>"Save…" via the storage provider owned by the log window (set by the view).</summary>
    public Func<Task<string?>>? PickSavePathAsync { get; set; }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (PickSavePathAsync is null)
            return;
        var path = await PickSavePathAsync();
        if (string.IsNullOrEmpty(path))
            return;
        try
        {
            AppLogger.ExportTo(path);
            AppLogger.Info("log", $"Exported {Entries.Count} entries to '{path}'.");
        }
        catch (Exception ex)
        {
            AppLogger.Error("log", $"Failed to export log to '{path}'.", ex);
        }
    }
}
