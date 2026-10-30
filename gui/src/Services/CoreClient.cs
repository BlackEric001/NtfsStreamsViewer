using System.Text.Json.Serialization;
using NtfsStreamsViewer.Models;

namespace NtfsStreamsViewer.Services;

/// <summary>
/// High-level typed API over JSON-RPC used by the ViewModels (docs/GUI.md).
/// </summary>
public interface ICoreClient
{
    Task<List<VolumeInfo>> ListVolumesAsync(CancellationToken ct = default);
    Task<string> StartScanAsync(string path, ScanOptions options, CancellationToken ct = default);
    Task CancelScanAsync(string jobId, CancellationToken ct = default);
    Task<List<StreamInfo>> ListStreamsAsync(string path, CancellationToken ct = default);
    Task<ReadResult> ReadStreamAsync(string path, string streamName, ulong offset, int length,
        CancellationToken ct = default);
    Task<ExportResult> ExportStreamAsync(string path, string streamName, string destinationPath,
        CancellationToken ct = default);
    Task<ExportAllResult> ExportAllStreamsAsync(string path, string destinationDir,
        CancellationToken ct = default);

    /// <summary>Restart the underlying sidecar after a crash.</summary>
    Task RestartCoreAsync();

    event EventHandler<ProcessExitedEventArgs>? CoreCrashed;
}

/// <summary>Result of <c>stream.exportAll</c>.</summary>
public sealed record ExportAllResult
{
    [JsonPropertyName("streams_exported")]
    public int StreamsExported { get; init; }

    [JsonPropertyName("files")]
    public List<string> Files { get; init; } = new();
}

internal sealed record VolumesListResult
{
    [JsonPropertyName("volumes")]
    public List<VolumeInfo> Volumes { get; init; } = new();
}

internal sealed record StreamsListResult
{
    [JsonPropertyName("streams")]
    public List<StreamInfo> Streams { get; init; } = new();
}

internal sealed record JobIdResult
{
    [JsonPropertyName("job_id")]
    public string JobId { get; init; } = string.Empty;
}

public sealed class CoreClient : ICoreClient, IAsyncDisposable
{
    /// <summary>Preview block size: 64 KiB per docs/GUI.md §6.4 — never read whole streams.</summary>
    public const int PreviewBlockSize = 64 * 1024;

    private readonly ICoreProcessService _processService;
    private readonly IJsonRpcService _rpc;

    public event EventHandler<ProcessExitedEventArgs>? CoreCrashed;

    public CoreClient(ICoreProcessService processService, IJsonRpcService rpc)
    {
        _processService = processService;
        _rpc = rpc;
        _processService.ProcessExited += OnProcessExited;
    }

    private void OnProcessExited(object? sender, ProcessExitedEventArgs e)
    {
        if (!e.IsExpectedShutdown)
            CoreCrashed?.Invoke(this, e);
    }

    public async Task<List<VolumeInfo>> ListVolumesAsync(CancellationToken ct = default)
    {
        var result = await _rpc.SendRequestAsync<VolumesListResult>(
            "volumes.list", null, ct).ConfigureAwait(false);
        return result.Volumes;
    }

    public async Task<string> StartScanAsync(string path, ScanOptions options, CancellationToken ct = default)
    {
        // scan.start params: { "path": ..., ...flattened ScanOptions } (SPEC.md §5.3)
        var parameters = new Dictionary<string, object?>
        {
            ["path"] = path,
            ["recursive"] = options.Recursive,
            ["include_hidden"] = options.IncludeHidden,
            ["include_system"] = options.IncludeSystem,
            ["include_directories"] = options.IncludeDirectories,
            ["follow_symlinks"] = options.FollowSymlinks,
        };
        var result = await _rpc.SendRequestAsync<JobIdResult>(
            "scan.start", parameters, ct).ConfigureAwait(false);
        return result.JobId;
    }

    public async Task CancelScanAsync(string jobId, CancellationToken ct = default)
    {
        await _rpc.SendRequestAsync<object>(
            "scan.cancel", new { job_id = jobId }, ct).ConfigureAwait(false);
    }

    public async Task<List<StreamInfo>> ListStreamsAsync(string path, CancellationToken ct = default)
    {
        var result = await _rpc.SendRequestAsync<StreamsListResult>(
            "streams.list", new { path }, ct).ConfigureAwait(false);
        return result.Streams;
    }

    public async Task<ReadResult> ReadStreamAsync(string path, string streamName, ulong offset, int length,
        CancellationToken ct = default)
    {
        // Hard cap at 1 MiB per SPEC.md §5.3 (stream.read).
        length = Math.Clamp(length, 1, 1024 * 1024);
        return await _rpc.SendRequestAsync<ReadResult>(
            "stream.read",
            new { path, stream_name = streamName, offset, length },
            ct).ConfigureAwait(false);
    }

    public async Task<ExportResult> ExportStreamAsync(string path, string streamName, string destinationPath,
        CancellationToken ct = default)
    {
        // Exports can be large; give them a longer timeout than the default.
        return await _rpc.SendRequestAsync<ExportResult>(
            "stream.export",
            new { path, stream_name = streamName, destination = destinationPath },
            ct).ConfigureAwait(false);
    }

    public async Task<ExportAllResult> ExportAllStreamsAsync(string path, string destinationDir,
        CancellationToken ct = default)
    {
        return await _rpc.SendRequestAsync<ExportAllResult>(
            "stream.exportAll",
            new { path, destination_dir = destinationDir },
            ct).ConfigureAwait(false);
    }

    public Task RestartCoreAsync() => _processService.RestartAsync();

    public async ValueTask DisposeAsync()
    {
        _processService.ProcessExited -= OnProcessExited;
        await _rpc.DisposeAsync().ConfigureAwait(false);
    }
}
