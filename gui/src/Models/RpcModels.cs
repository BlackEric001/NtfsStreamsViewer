using System.Text.Json.Serialization;

namespace NtfsStreamsViewer.Models;

/// <summary>
/// A single alternate data stream as returned by <c>streams.list</c> / <c>scan.streamFound</c>.
/// Mirrors <c>core::types::StreamInfo</c>. The name never includes the <c>:$DATA</c> suffix.
/// </summary>
public sealed record StreamInfo
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("size")]
    public ulong Size { get; init; }

    [JsonPropertyName("owner_path")]
    public string OwnerPath { get; init; } = string.Empty;

    [JsonIgnore]
    public string SizeText => VolumeInfo.FormatSize(Size);
}

/// <summary>Scan options sent with <c>scan.start</c>. Mirrors <c>core::types::ScanOptions</c>.</summary>
public sealed record ScanOptions
{
    [JsonPropertyName("recursive")]
    public bool Recursive { get; init; } = true;

    [JsonPropertyName("include_hidden")]
    public bool IncludeHidden { get; init; } = true;

    [JsonPropertyName("include_system")]
    public bool IncludeSystem { get; init; }

    [JsonPropertyName("include_directories")]
    public bool IncludeDirectories { get; init; } = true;

    [JsonPropertyName("follow_symlinks")]
    public bool FollowSymlinks { get; init; }
}

/// <summary>Result of <c>stream.read</c>: a base64-encoded block plus paging info.</summary>
public sealed record ReadResult
{
    [JsonPropertyName("data_base64")]
    public string DataBase64 { get; init; } = string.Empty;

    [JsonPropertyName("offset")]
    public ulong Offset { get; init; }

    [JsonPropertyName("length")]
    public ulong Length { get; init; }

    [JsonPropertyName("eof")]
    public bool Eof { get; init; }

    public byte[] DecodeData() => Convert.FromBase64String(DataBase64);
}

/// <summary>Result of <c>stream.export</c>.</summary>
public sealed record ExportResult
{
    [JsonPropertyName("bytes_written")]
    public ulong BytesWritten { get; init; }

    [JsonPropertyName("sha256")]
    public string Sha256 { get; init; } = string.Empty;
}

/// <summary>Payload of the <c>scan.progress</c> notification.</summary>
public sealed record ScanProgressNotification
{
    [JsonPropertyName("job_id")]
    public string JobId { get; init; } = string.Empty;

    [JsonPropertyName("files_scanned")]
    public ulong FilesScanned { get; init; }

    [JsonPropertyName("dirs_scanned")]
    public ulong DirsScanned { get; init; }

    [JsonPropertyName("streams_found")]
    public ulong StreamsFound { get; init; }

    [JsonPropertyName("errors")]
    public ulong Errors { get; init; }

    [JsonPropertyName("current_path")]
    public string? CurrentPath { get; init; }
}

/// <summary>Payload of the <c>scan.streamFound</c> notification.</summary>
public sealed record StreamFoundNotification
{
    [JsonPropertyName("job_id")]
    public string JobId { get; init; } = string.Empty;

    [JsonPropertyName("stream")]
    public StreamInfo Stream { get; init; } = new();
}

/// <summary>Payload of the <c>scan.error</c> notification.</summary>
public sealed record ScanErrorNotification
{
    [JsonPropertyName("job_id")]
    public string JobId { get; init; } = string.Empty;

    [JsonPropertyName("path")]
    public string Path { get; init; } = string.Empty;

    [JsonPropertyName("error")]
    public string Error { get; init; } = string.Empty;
}

/// <summary>
/// A single scan error with details. The core reports completion errors as plain
/// strings in the form "path: message" (see <c>core::scan::record_error</c>).
/// </summary>
public sealed record ScanErrorDetail(string Path, string Message, int Occurrences = 1)
{
    /// <summary>Splits a raw "path: message" string into path and message parts.</summary>
    public static ScanErrorDetail Parse(string raw)
    {
        // Windows drive paths ("C:\...") must not be split at the drive colon.
        var idx = raw.IndexOf(": ", StringComparison.Ordinal);
        if (idx == 1 && raw.Length > 2 && char.IsLetter(raw[0]))
            idx = raw.IndexOf(": ", idx + 1, StringComparison.Ordinal);
        return idx > 0
            ? new ScanErrorDetail(raw[..idx], raw[(idx + 2)..])
            : new ScanErrorDetail(string.Empty, raw);
    }

    public string Display => Occurrences > 1
        ? $"{Path}: {Message} (×{Occurrences})"
        : $"{Path}: {Message}";

    public override string ToString() => Display;
}

/// <summary>Payload of the <c>scan.completed</c> notification. Mirrors <c>core::types::ScanResult</c>.</summary>
public sealed record ScanCompletedNotification
{
    [JsonPropertyName("job_id")]
    public string JobId { get; init; } = string.Empty;

    [JsonPropertyName("total_files")]
    public ulong TotalFiles { get; init; }

    [JsonPropertyName("total_streams")]
    public ulong TotalStreams { get; init; }

    /// <summary>
    /// Detailed error strings collected by the core ("path: message", capped at 1000).
    /// NOTE: this is an array, not a count — the count lives in <see cref="ErrorCount"/>.
    /// </summary>
    [JsonPropertyName("errors")]
    public List<string> Errors { get; init; } = new();

    /// <summary>
    /// Total number of errors (from the last <c>scan.progress</c> notification). The
    /// completion payload does not carry this number, so the ViewModel fills it in.
    /// </summary>
    [JsonIgnore]
    public ulong ErrorCount { get; set; }

    [JsonPropertyName("duration_ms")]
    public ulong DurationMs { get; init; }
}
