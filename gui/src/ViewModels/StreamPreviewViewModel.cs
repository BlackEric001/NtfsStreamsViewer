using System.Text;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NtfsStreamsViewer.Models;
using NtfsStreamsViewer.Services;

namespace NtfsStreamsViewer.ViewModels;

/// <summary>
/// Preview of a single stream: paged reads (64 KiB blocks), text/hex modes.
/// Never loads the whole stream into memory (docs/GUI.md, SPEC.md §6.4).
/// </summary>
public partial class StreamPreviewViewModel : ViewModelBase
{
    /// <summary>Initial read per GUI spec: 4096 bytes.</summary>
    private const int InitialReadLength = 4096;

    private readonly ICoreClient _core;
    private readonly StreamInfo _stream;
    private readonly List<byte> _buffer = new();
    private bool _eofReached;
    private bool _loading;

    [ObservableProperty]
    private string _streamName = string.Empty;

    [ObservableProperty]
    private string _ownerPath = string.Empty;

    [ObservableProperty]
    private string _content = string.Empty;

    [ObservableProperty]
    private bool _isHexMode;

    [ObservableProperty]
    private bool _hasMoreData = true;

    [ObservableProperty]
    private ulong _currentOffset;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    public StreamPreviewViewModel(ICoreClient core, StreamInfo stream)
    {
        _core = core;
        _stream = stream;
        StreamName = stream.Name;
        OwnerPath = stream.OwnerPath;
    }

    /// <summary>Loads the first block (offset 0, length 4096) — called when the window opens.</summary>
    [RelayCommand]
    public async Task LoadAsync()
    {
        await ReadBlockAsync(0, InitialReadLength).ConfigureAwait(false);
    }

    [RelayCommand]
    private async Task LoadMoreAsync()
    {
        if (_eofReached || _loading)
            return;
        await ReadBlockAsync(CurrentOffset, CoreClient.PreviewBlockSize).ConfigureAwait(false);
    }

    [RelayCommand]
    private void ToggleHexMode()
    {
        IsHexMode = !IsHexMode;
        RenderContent();
    }

    [RelayCommand]
    private void ShowText()
    {
        if (IsHexMode) ToggleHexMode();
    }

    [RelayCommand]
    private void ShowHex()
    {
        if (!IsHexMode) ToggleHexMode();
    }

    [RelayCommand]
    private async Task ReloadAsync()
    {
        _buffer.Clear();
        _eofReached = false;
        CurrentOffset = 0;
        await LoadAsync().ConfigureAwait(false);
    }

    private async Task ReadBlockAsync(ulong offset, int length)
    {
        if (_loading)
            return;
        _loading = true;
        IsLoading = true;
        try
        {
            var result = await _core.ReadStreamAsync(
                _stream.OwnerPath, _stream.Name, offset, length).ConfigureAwait(false);

            var data = result.DecodeData();
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _buffer.AddRange(data);
                CurrentOffset = result.Offset + (ulong)data.Length;
                _eofReached = result.Eof || data.Length == 0;
                HasMoreData = !_eofReached;
                StatusMessage = _eofReached
                    ? $"End of stream ({CurrentOffset} bytes loaded)."
                    : $"Loaded {CurrentOffset} bytes…";
                RenderContent();
            });
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
                StatusMessage = $"Read failed: {ex.Message}");
        }
        finally
        {
            IsLoading = false;
            _loading = false;
        }
    }

    private void RenderContent()
    {
        Content = IsHexMode ? ToHex(_buffer) : ToText(_buffer);
    }

    private static string ToText(IEnumerable<byte> bytes)
    {
        // Replace invalid sequences instead of throwing on binary content.
        var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);
        var text = encoding.GetString(bytes.ToArray());
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (c == '\0') sb.Replace('\0', '·');
            else if (char.IsControl(c) && c != '\t' && c != '\r' && c != '\n') sb.Append('·');
            else sb.Append(c);
        }
        return sb.ToString();
    }

    private static string ToHex(IReadOnlyList<byte> bytes)
    {
        const int bytesPerLine = 16;
        var sb = new StringBuilder((bytes.Count / bytesPerLine + 1) * 78);
        for (int i = 0; i < bytes.Count; i += bytesPerLine)
        {
            sb.Append(i.ToString("X8")).Append("  ");

            for (int j = 0; j < bytesPerLine; j++)
            {
                if (i + j < bytes.Count)
                    sb.Append(bytes[i + j].ToString("X2")).Append(' ');
                else
                    sb.Append("   ");
                if (j == 7) sb.Append(' ');
            }

            sb.Append(' ');
            for (int j = 0; j < bytesPerLine && i + j < bytes.Count; j++)
            {
                var c = bytes[i + j];
                sb.Append(c >= 0x20 && c < 0x7F ? (char)c : '.');
            }
            sb.AppendLine();
        }
        return sb.ToString();
    }
}
