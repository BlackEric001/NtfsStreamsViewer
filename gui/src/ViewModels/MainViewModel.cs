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

    /// <summary>Number of distinct error rows collected during the current/last scan.</summary>
    public int ScanErrorListCount => ScanErrorDetails.Count;

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

    /// <summary>Raised (on the UI thread) when the collected scan-error details change.</summary>
    public event EventHandler<IReadOnlyList<ScanErrorDetail>>? ScanErrorsChanged;

    /// <summary>Cap for the in-memory error-detail list (the core itself caps at 1000).</summary>
    private const int MaxErrorDetails = 2000;

    private readonly List<ScanErrorDetail> _scanErrors = new();

    /// <summary>Per-path error details collected during the current/last scan.</summary>
    public IReadOnlyList<ScanErrorDetail> ScanErrorDetails
    {
        get { lock (_scanErrors) return _scanErrors.ToList(); }
    }

    /// <summary>Last progress notification for the active job (used to fill in error counts).</summary>
    private ScanProgressNotification? _lastProgress;

    private string? _activeJobId;
    private readonly List<ScanStreamItem> _allResults = new();

    /// <summary>True once the user opened the log window — suppresses the completion popup afterwards.</summary>
    private bool _logWindowOpened;

    public MainViewModel(ICoreClient core, IJsonRpcService rpc)
    {
        _core = core;
        _rpc = rpc;
        _rpc.NotificationReceived += OnNotificationReceived;
        _core.CoreCrashed += OnCoreCrashed;
    }

    /// <summary>Called by the log window so we can stop nagging with popups about scan errors.</summary>
    public void NotifyLogWindowOpened() => _logWindowOpened = true;

    public void ClearScanErrors()
    {
        lock (_scanErrors)
        {
            _scanErrors.Clear();
            _errorIndex.Clear();
        }
        ScanErrorsChanged?.Invoke(this, Array.Empty<ScanErrorDetail>());
    }

    /// <summary>
    /// Adds a scan error to the detail list. Identical (path, message) pairs are
    /// coalesced into one row with an occurrence counter so a permission-denied wall
    /// on a big tree cannot flood the UI.
    /// </summary>
    private void AddScanError(string path, string message)
    {
        var key = path + "\n" + message;
        bool changed;
        lock (_scanErrors)
        {
            if (_errorIndex.TryGetValue(key, out var idx))
            {
                _scanErrors[idx] = _scanErrors[idx] with { Occurrences = _scanErrors[idx].Occurrences + 1 };
                changed = false; // counter-only update: no need to re-notify yet
            }
            else
            {
                if (_scanErrors.Count >= MaxErrorDetails)
                    return;
                _errorIndex[key] = _scanErrors.Count;
                _scanErrors.Add(new ScanErrorDetail(path, message));
                changed = true;
            }
        }
        if (changed)
        {
            _errorsDirty = true;
            ScheduleErrorFlush();
        }
    }

    private readonly Dictionary<string, int> _errorIndex = new(StringComparer.Ordinal);
    private volatile bool _errorsDirty;
    private System.Threading.Timer? _errorFlushTimer;

    /// <summary>Coalesces error-list notifications to at most one refresh per 500 ms.</summary>
    private void ScheduleErrorFlush()
    {
        if (_errorFlushTimer is null)
        {
            _errorFlushTimer = new System.Threading.Timer(_ => FlushErrorChanges(), null,
                TimeSpan.FromMilliseconds(500), Timeout.InfiniteTimeSpan);
        }
        else
        {
            _errorFlushTimer.Change(TimeSpan.FromMilliseconds(500), Timeout.InfiniteTimeSpan);
        }
    }

    private void FlushErrorChanges()
    {
        if (!_errorsDirty)
            return;
        _errorsDirty = false;
        var snapshot = ScanErrorDetails;
        Avalonia.Threading.Dispatcher.UIThread.Post(
            () => ScanErrorsChanged?.Invoke(this, snapshot));
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
            _lastProgress = null;
            ClearScanErrors();

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

    /// <summary>Raised when the user asks to open the log window (handled by MainWindow).</summary>
    public event EventHandler? OpenLogRequested;

    [RelayCommand]
    private void OpenLog() => OpenLogRequested?.Invoke(this, EventArgs.Empty);

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
        // scan.progress fires many times per second on large trees. Posting every one of
        // them to the Dispatcher saturates the UI queue and the app looks "hung" — so we
        // coalesce them: while a UI update is queued, newer payloads only overwrite the
        // snapshot (latest wins). This caps progress repaints at the natural UI cadence.
        if (e.Method == "scan.progress")
        {
            ScheduleProgress(e.Params);
            return;
        }
        // Marshal to the UI thread: notifications arrive on the background reader loop.
        Dispatcher.UIThread.Post(() => HandleNotification(e.Method, e.Params));
    }

    /// <summary>Minimum interval between two scan.progress UI updates.</summary>
    private const int ProgressThrottleMs = 250;

    private readonly object _progressSync = new();
    private JsonElement _pendingProgress;
    private bool _progressScheduled;
    private DateTimeOffset _lastProgressHandledUtc = DateTimeOffset.MinValue;

    private void ScheduleProgress(JsonElement p)
    {
        double waitMs;
        lock (_progressSync)
        {
            _pendingProgress = p;
            if (_progressScheduled)
                return; // an update is already queued — it will pick up the latest payload
            waitMs = Math.Max(0,
                ProgressThrottleMs - (DateTimeOffset.UtcNow - _lastProgressHandledUtc).TotalMilliseconds);
            _progressScheduled = true;
        }

        _ = Task.Delay(TimeSpan.FromMilliseconds(waitMs)).ContinueWith(_ =>
        {
            JsonElement snapshot;
            lock (_progressSync)
            {
                snapshot = _pendingProgress;
                _progressScheduled = false;
                _lastProgressHandledUtc = DateTimeOffset.UtcNow;
            }
            Dispatcher.UIThread.Post(
                () => HandleNotification("scan.progress", snapshot),
                DispatcherPriority.Background);
        }, TaskScheduler.Default);
    }

    private void HandleNotification(string method, JsonElement p)
    {
        switch (method)
        {
            case "scan.progress":
            {
                var n = Deserialize<ScanProgressNotification>(p);
                if (n is null || n.JobId != _activeJobId) return;
                _lastProgress = n;
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
                AddScanError(n.Path, n.Error);
                AppLogger.Warning("scan", $"Error at {n.Path}: {n.Error}");
                StatusMessage = $"{ErrorsCount} error(s) — open the Log window for details.";
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
                // The completion payload carries detailed errors ("path: message"), not a count.
                ErrorsCount = Math.Max(n.Errors.Count, _lastProgress?.Errors ?? 0);
                n.ErrorCount = ErrorsCount;
                MergeErrorDetails(n.Errors);
                StatusMessage =
                    $"Scan finished: {n.TotalFiles} files, {n.TotalStreams} streams, " +
                    $"{ErrorsCount} errors in {n.DurationMs} ms.";
                if (n.Errors.Count > 0 && !_logWindowOpened)
                    ScanErrorsWithDetailsDetected?.Invoke(this, n);
                break;
            }
        }
    }

    /// <summary>Raised when a scan completed with detailed errors and the log window was never opened.</summary>
    public event EventHandler<ScanCompletedNotification>? ScanErrorsWithDetailsDetected;

    /// <summary>
    /// Fills the error list from the completion payload without dropping live scan.error entries.
    /// </summary>
    private void MergeErrorDetails(List<string> payloadErrors)
    {
        foreach (var raw in payloadErrors)
        {
            var d = Models.ScanErrorDetail.Parse(raw);
            AddScanError(d.Path, d.Message);
        }
        // The merged list is authoritative — push it immediately.
        FlushErrorChanges();
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

    public ValueTask DisposeAsync()
    {
        _rpc.NotificationReceived -= OnNotificationReceived;
        _core.CoreCrashed -= OnCoreCrashed;
        _errorFlushTimer?.Dispose();
        return default;
    }
}
