using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NtfsStreamsViewer.Models;
using NtfsStreamsViewer.Services;

namespace NtfsStreamsViewer.ViewModels;

/// <summary>
/// Backing logic for the export dialog (docs/GUI.md ViewModels layer).
/// The core writes the destination file itself via <c>stream.export</c>.
/// </summary>
public partial class ExportViewModel : ViewModelBase
{
    private readonly ICoreClient _core;
    private readonly StreamInfo _stream;

    [ObservableProperty]
    private string _destinationPath = string.Empty;

    [ObservableProperty]
    private bool _isExporting;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    /// <summary>True once the export finished successfully; the dialog can close with this result.</summary>
    [ObservableProperty]
    private ExportResult? _result;

    public string Title { get; }

    public ExportViewModel(ICoreClient core, StreamInfo stream)
    {
        _core = core;
        _stream = stream;
        Title = $"Export stream \"{stream.Name}\"";
        DestinationPath = SuggestFileName(stream);
    }

    /// <summary>Sanitized default file name (SPEC.md §10: replace invalid characters).</summary>
    internal static string SuggestFileName(StreamInfo stream)
    {
        var owner = Path.GetFileName(stream.OwnerPath);
        if (string.IsNullOrEmpty(owner))
            owner = "stream";

        var invalid = Path.GetInvalidFileNameChars();
        var raw = $"{owner}_{stream.Name}.bin";
        var set = new HashSet<char>(invalid);
        var sb = new System.Text.StringBuilder(raw.Length);
        foreach (var c in raw)
            sb.Append(set.Contains(c) || c == ':' ? '_' : c);
        return sb.ToString();
    }

    [RelayCommand(CanExecute = nameof(CanExport))]
    private async Task ExportAsync()
    {
        if (string.IsNullOrWhiteSpace(DestinationPath))
        {
            StatusMessage = "Choose a destination file first.";
            return;
        }

        IsExporting = true;
        ExportCommand.NotifyCanExecuteChanged();
        try
        {
            var full = Path.GetFullPath(DestinationPath.Trim().Trim('"'));
            var res = await _core.ExportStreamAsync(_stream.OwnerPath, _stream.Name, full)
                .ConfigureAwait(false);
            Result = res;
            StatusMessage = $"Exported {res.BytesWritten} bytes.\nSHA-256: {res.Sha256}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Export failed: {ex.Message}";
        }
        finally
        {
            IsExporting = false;
            ExportCommand.NotifyCanExecuteChanged();
        }
    }

    private bool CanExport() => !IsExporting && Result is null;
}
