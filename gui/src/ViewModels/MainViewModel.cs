using System.Collections.ObjectModel;
using System.Text.Json;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NtfsStreamsViewer.Models;
using NtfsStreamsViewer.Services;

namespace NtfsStreamsViewer.ViewModels;

/// <summary>A row in the scan-results table.</summary>
public sealed partial class ScanStreamItem : ObservableObject
{
    public StreamInfo Stream { get; }

    public string FileName => System.IO.Path.GetFileName(Stream.OwnerPath);
    public string OwnerDirectory => System.IO.Path.GetDirectoryName(Stream.OwnerPath) ?? string.Empty;
    public string StreamName => Stream.Name;
    public string SizeText => Stream.SizeText;

    public ScanStreamItem(StreamInfo stream)
    {
        Stream = stream;
    }
}

/// <summary>
/// Main window state: volumes, scanning, results and progress (docs/GUI.md).
/// </summary>
public partial class MainViewModel : ViewModelBase, IAsyncDisposable
{
    private readonly ICoreClient _core;

    /// <summary>Exposes the core client to views (e.g. for the modal export dialog).</summary>
    public ICoreClient CoreClient => _core;
    private readonly IJsonRpcService _rpc;

    [ObservableProperty]
    private VolumeInfo? _selectedVolume;

    [ObservableProperty]
    private ScanStreamItem? _selectedResult;

    [ObservableProperty]
    private bool _isScanning;

    [ObservableProperty]
    private string _currentScanPath = string.Empty;

    [ObservableProperty]
    private ulong _filesScanned;

    [ObservableProperty]
    private ulong _streamsFound;

    [ObservableProperty]
    private ulong _errorsCount;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _isCoreConnected;

    /// <summary>Embedded preview for the currently opened stream (right panel of MainWindow).</summary>
    [ObservableProperty]
    private StreamPreviewViewModel? _preview;

    // Scan options exposed to the UI as checkboxes.
    [ObservableProperty]
    private bool _recursive = true;

    [ObservableProperty]
    private bool _includeHidden = true;

    [ObservableProperty]
    private bool _includeSystem;

    [ObservableProperty]
    private bool _includeDirectories = true;

    [ObservableProperty]
    private bool _followSymlinks;

    public ObservableCollection<VolumeInfo> Volumes { get; } = new();
    public ObservableCollection<ScanStreamItem> ScanResults { get; } = new();

    public string FilterText
    {
        get => _filterText;
        set
        {
            if (SetProperty(ref _filterText, value))
                ApplyFilter();
        }
    }
    private string _filterText = string.Empty;

    /// <summary>Rows visible in the UI after filtering.</summary>
    public ObservableCollection<ScanStreamItem> FilteredResults { get; } = new();

    /// <summary>Raised when the user asks to open a preview for a stream.</summary>
    public event EventHandler<StreamInfo>? PreviewRequested;

    /// <summary>Raised when the user asks to export a stream (dialog owner needs to handle it).</summary>
    public event EventHandler<StreamInfo>? ExportRequested;

    /// <summary>Raised when the sidecar process crashed — the view shows a restart dialog.</summary>
    public event EventHandler<string>? CoreCrashDetected;

    private string? _activeJobId;
    private readonly List<ScanStreamItem> _allResults = new();

    public MainViewModel(ICoreClient core, IJsonRpcService rpc)
    {
        _core = core;
        _rpc = rpc;
        _rpc.NotificationReceived += OnNotificationReceived;
        _core.CoreCrashed += OnCoreCrashed;
    }

    public async Task InitializeAsync()
    {
        await RefreshVolumesAsync().ConfigureAwait(false);
    }

    [RelayCommand]
    private Task RefreshVolumesAsync() => RefreshVolumesInternalAsync();

    private async Task RefreshVolumesInternalAsync()
    {
        try
        {
            var volumes = await _core.ListVolumesAsync().ConfigureAwait(false);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                Volumes.Clear();
                foreach (var v in volumes)
                    Volumes.Add(v);
                SelectedVolume ??= Volumes.FirstOrDefault(v => v.IsNtfs) ?? Volumes.FirstOrDefault();
                IsCoreConnected = true;
                StatusMessage = $"Discovered {volumes.Count} volume(s).";
            });
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                IsCoreConnected = false;
                StatusMessage = $"Cannot list volumes: {ex.Message}";
            });
        }
    }

    [RelayCommand]
    private async Task StartScanAsync()
    {
        if (IsScanning)
            return;

        var path = !string.IsNullOrWhiteSpace(CurrentScanPath)
            ? CurrentScanPath.Trim()
            : SelectedVolume?.MountPoint ?? string.Empty;

        if (string.IsNullOrWhiteSpace(path))
        {
            StatusMessage = "Select a volume or type a path to scan.";
            return;
        }

        try
        {
            ScanResults.Clear();
            FilteredResults.Clear();
            _allResults.Clear();
            FilesScanned = 0;
            StreamsFound = 0;
            ErrorsCount = 0;

            var options = new ScanOptions
            {
                Recursive = Recursive,
                IncludeHidden = IncludeHidden,
                IncludeSystem = IncludeSystem,
                IncludeDirectories = IncludeDirectories,
                FollowSymlinks = FollowSymlinks,
            };

            _activeJobId = await _core.StartScanAsync(path, options).ConfigureAwait(false);
            IsScanning = true;
            StatusMessage = $"Scanning {path}…";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to start scan: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task CancelScanAsync()
    {
        var jobId = _activeJobId;
        if (jobId is null)
            return;

        try
        {
            await _core.CancelScanAsync(jobId).ConfigureAwait(false);
            StatusMessage = "Scan cancellation requested…";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to cancel scan: {ex.Message}";
        }
    }

    [RelayCommand]
    private void ViewStream(ScanStreamItem? item)
    {
        var target = item ?? SelectedResult;
        if (target is not null)
            OpenPreview(target.Stream);
    }

    /// <summary>Opens the embedded preview and also notifies listeners (e.g. standalone window).</summary>
    public void OpenPreview(StreamInfo stream)
    {
        Preview = new StreamPreviewViewModel(_core, stream);
        _ = Preview.LoadAsync();
        PreviewRequested?.Invoke(this, stream);
    }

    [RelayCommand]
    private void ExportStream(ScanStreamItem? item)
    {
        var target = item ?? SelectedResult;
        if (target is not null)
            ExportRequested?.Invoke(this, target.Stream);
    }

    [RelayCommand]
    private async Task RestartCoreAsync()
    {
        try
        {
            await _core.RestartCoreAsync().ConfigureAwait(false);
            await RefreshVolumesInternalAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
                StatusMessage = $"Restart failed: {ex.Message}");
        }
    }

    private void OnNotificationReceived(object? sender, JsonRpcNotificationEventArgs e)
    {
        // Marshal to the UI thread: notifications arrive on the background reader loop.
        Dispatcher.UIThread.Post(() => HandleNotification(e.Method, e.Params));
    }

    private void HandleNotification(string method, JsonElement p)
    {
        switch (method)
        {
            case "scan.progress":
            {
                var n = Deserialize<ScanProgressNotification>(p);
                if (n is null || n.JobId != _activeJobId) return;
                FilesScanned = n.FilesScanned;
                StreamsFound = n.StreamsFound;
                ErrorsCount = n.Errors;
                break;
            }
            case "scan.streamFound":
            {
                var n = Deserialize<StreamFoundNotification>(p);
                if (n is null || n.JobId != _activeJobId) return;
                AddResult(n.Stream);
                break;
            }
            case "scan.error":
            {
                var n = Deserialize<ScanErrorNotification>(p);
                if (n is null || n.JobId != _activeJobId) return;
                ErrorsCount++;
                StatusMessage = $"Scan error at {n.Path}: {n.Error}";
                break;
            }
            case "scan.completed":
            {
                var n = Deserialize<ScanCompletedNotification>(p);
                if (n is null || n.JobId != _activeJobId) return;
                IsScanning = false;
                _activeJobId = null;
                FilesScanned = n.TotalFiles;
                StreamsFound = n.TotalStreams;
                ErrorsCount = n.Errors;
                StatusMessage =
                    $"Scan finished: {n.TotalFiles} files, {n.TotalStreams} streams, " +
                    $"{n.Errors} errors in {n.DurationMs} ms.";
                break;
            }
        }
    }

    private static T? Deserialize<T>(JsonElement el) where T : class
    {
        try
        {
            return el.ValueKind == JsonValueKind.Undefined ? null : el.Deserialize<T>();
        }
        catch (JsonException)
        {
            return null; // malformed notification → log & continue
        }
    }

    private void AddResult(StreamInfo stream)
    {
        var item = new ScanStreamItem(stream);
        _allResults.Add(item);
        if (MatchesFilter(item))
            FilteredResults.Add(item);
        StreamsFound = (ulong)_allResults.Count;
    }

    private bool MatchesFilter(ScanStreamItem item) =>
        string.IsNullOrWhiteSpace(FilterText) ||
        item.FileName.Contains(FilterText, StringComparison.OrdinalIgnoreCase) ||
        item.StreamName.Contains(FilterText, StringComparison.OrdinalIgnoreCase) ||
        item.Stream.OwnerPath.Contains(FilterText, StringComparison.OrdinalIgnoreCase);

    private void ApplyFilter()
    {
        FilteredResults.Clear();
        foreach (var item in _allResults)
        {
            if (MatchesFilter(item))
                FilteredResults.Add(item);
        }
    }

    partial void OnSelectedVolumeChanged(VolumeInfo? value)
    {
        if (value is not null && !IsScanning)
            CurrentScanPath = value.MountPoint;
    }

    private void OnCoreCrashed(object? sender, ProcessExitedEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            IsScanning = false;
            IsCoreConnected = false;
            StatusMessage = $"ads-cli exited unexpectedly (code {e.ExitCode}).";
            CoreCrashDetected?.Invoke(this,
                $"The core process (ads-cli) exited unexpectedly with code {e.ExitCode}.\n\n" +
                "Restart it to continue working?");
        });
    }

    public async ValueTask DisposeAsync()
    {
        _rpc.NotificationReceived -= OnNotificationReceived;
        _core.CoreCrashed -= OnCoreCrashed;
    }
}
