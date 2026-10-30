using System.Text.Json.Serialization;

namespace NtfsStreamsViewer.Models;

/// <summary>
/// NTFS volume information as returned by the <c>volumes.list</c> JSON-RPC method.
/// Mirrors <c>core::types::VolumeInfo</c> (SPEC.md §3.2 / §5.3).
/// </summary>
public sealed record VolumeInfo
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("mount_point")]
    public string MountPoint { get; init; } = string.Empty;

    [JsonPropertyName("file_system")]
    public string FileSystem { get; init; } = string.Empty;

    [JsonPropertyName("is_ntfs")]
    public bool IsNtfs { get; init; }

    [JsonPropertyName("is_writable")]
    public bool IsWritable { get; init; }

    [JsonPropertyName("total_size")]
    public ulong? TotalSize { get; init; }

    [JsonPropertyName("free_size")]
    public ulong? FreeSize { get; init; }

    public string DisplayName => $"{Name} ({FileSystem})";

    public string SizeSummary
    {
        get
        {
            if (TotalSize is null || FreeSize is null)
                return string.Empty;
            return $"{FormatSize(FreeSize.Value)} free of {FormatSize(TotalSize.Value)}";
        }
    }

    internal static string FormatSize(ulong bytes) => bytes switch
    {
        >= 1UL << 40 => $"{bytes / (double)(1UL << 40):F2} TiB",
        >= 1UL << 30 => $"{bytes / (double)(1UL << 30):F2} GiB",
        >= 1UL << 20 => $"{bytes / (double)(1UL << 20):F2} MiB",
        >= 1UL << 10 => $"{bytes / (double)(1UL << 10):F2} KiB",
        _ => $"{bytes} B",
    };
}
