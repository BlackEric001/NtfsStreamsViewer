using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;

namespace NtfsStreamsViewer.Services;

public enum LogLevel
{
    Debug,
    Info,
    Warning,
    Error,
}

/// <summary>One line in the application log (also written to a rotating file).</summary>
public sealed record LogEntry(DateTimeOffset Timestamp, LogLevel Level, string Category, string Message)
{
    public string TimeText => Timestamp.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);

    /// <summary>Single-line representation used by the "Copy all" action and the log file.</summary>
    public string FullText =>
        $"{Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)} [{Level.ToString().ToUpperInvariant()}] [{Category}] {Message}";

    public override string ToString() => $"{TimeText} [{Level.ToString().ToUpperInvariant()}] {Category}: {Message}";
}

/// <summary>
/// In-memory ring buffer + file sink for diagnostics. The GUI must stay responsive even
/// when the sidecar spams notifications/errors, so the UI collection is throttled and capped
/// (older entries are dropped from memory but remain in the log file).
/// </summary>
public static class AppLogger
{
    private const int MemoryCapacity = 5000;

    /// <summary>Cap for the log window's own (filtered) list.</summary>
    public const int MemoryCapacityUiLimit = 3000;
    private const long MaxFileBytes = 10L * 1024 * 1024; // rotate at ~10 MiB

    private static readonly object Sync = new();
    private static readonly LinkedList<LogEntry> Entries = new();

    /// <summary>
    /// Minimum level that reaches the log (both memory and file). Overridable with the
    /// environment variable <c>NTSV_LOG_LEVEL</c> (debug|info|warning|error), default: info.
    /// </summary>
    public static LogLevel MinimumLevel { get; set; } = ParseLevel(
        Environment.GetEnvironmentVariable("NTSV_LOG_LEVEL")) ?? LogLevel.Info;

    private static LogLevel? ParseLevel(string? s) => s?.ToLowerInvariant() switch
    {
        "debug" or "trace" => LogLevel.Debug,
        "info" => LogLevel.Info,
        "warn" or "warning" => LogLevel.Warning,
        "error" => LogLevel.Error,
        _ => null,
    };

    /// <summary>All levels, for the log window's level filter combo box.</summary>
    public static IReadOnlyList<LogLevel> Levels { get; } =
        (LogLevel[])Enum.GetValues(typeof(LogLevel));

    /// <summary>Total number of ERROR-level entries recorded this session.</summary>
    public static int ErrorCount { get; private set; }

    /// <summary>True when messages at <paramref name="level"/> would be recorded (cheap guard for verbose formatting).</summary>
    public static bool IsEnabled(LogLevel level) => level >= MinimumLevel;
    private static readonly string LogDirectory =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NtfsStreamsViewer", "logs");
    private static readonly string LogFilePath = Path.Combine(LogDirectory, "ntfs-streams-viewer.log");

    private static StreamWriter? _writer;
    private static bool _rotationWarned;

    /// <summary>Bound to the log window's ItemsControl (UI-thread only, throttled).</summary>
    public static ObservableCollection<LogEntry> LiveEntries { get; } = new();

    /// <summary>Raised on the UI thread after batches were appended to <see cref="LiveEntries"/>.</summary>
    public static event EventHandler<IReadOnlyList<LogEntry>>? EntriesAppended;

    public static string LogFilePathDisplay
    {
        get
        {
            lock (Sync)
            {
                return TryEnsureWriter(out var path) ? path : "(log file unavailable)";
            }
        }
    }

    public static void Debug(string category, string message) => Log(LogLevel.Debug, category, message);
    public static void Info(string category, string message) => Log(LogLevel.Info, category, message);
    public static void Warning(string category, string message) => Log(LogLevel.Warning, category, message);
    public static void Error(string category, string message) => Log(LogLevel.Error, category, message);

    public static void Error(string category, string message, Exception ex) =>
        Log(LogLevel.Error, category, $"{message} — {ex.GetType().Name}: {ex.Message}");

    public static void Log(LogLevel level, string category, string message)
    {
        if (level < MinimumLevel)
            return;

        var entry = new LogEntry(DateTimeOffset.Now, level, category, message);

        List<LogEntry>? toPublish = null;
        lock (Sync)
        {
            if (level == LogLevel.Error)
                ErrorCount++;
            Entries.AddLast(entry);
            while (Entries.Count > MemoryCapacity)
                Entries.RemoveFirst();

            WriteToFileLocked(entry);

            // Throttle live UI updates: publish batches of error/warning lines immediately,
            // everything else at most every 500 ms. This keeps the Dispatcher queue small
            // during noisy scans (the app previously looked "hung" under notification floods).
            if (level >= LogLevel.Warning || (DateTime.UtcNow - _lastPublishUtc).TotalMilliseconds >= 500)
            {
                toPublish = DrainUnpublishedLocked();
                _lastPublishUtc = DateTime.UtcNow;
            }
        }

        if (toPublish is { Count: > 0 })
        {
            Dispatcher.UIThread.Post(() =>
            {
                foreach (var e in toPublish)
                {
                    LiveEntries.Add(e);
                    while (LiveEntries.Count > MemoryCapacity)
                        LiveEntries.RemoveAt(0);
                }
                EntriesAppended?.Invoke(null, toPublish);
            }, DispatcherPriority.Background);
        }
    }

    /// <summary>Snapshot of the in-memory ring buffer (for the log window / saving to file).</summary>
    public static IReadOnlyList<LogEntry> Snapshot()
    {
        lock (Sync)
            return Entries.ToList();
    }

    /// <summary>Writes the whole in-memory ring buffer to an arbitrary file (log window "Save…").</summary>
    public static void ExportTo(string filePath)
    {
        var snapshot = Snapshot();
        using var writer = new StreamWriter(filePath);
        foreach (var e in snapshot)
            writer.WriteLine(e.FullText);
    }

    private static LinkedListNode<LogEntry>? _lastPublished;
    private static DateTime _lastPublishUtc = DateTime.MinValue;

    /// <summary>Collects everything appended since the previous publish (caller holds <see cref="Sync"/>).</summary>
    private static List<LogEntry> DrainUnpublishedLocked()
    {
        var result = new List<LogEntry>();
        var node = _lastPublished is null ? Entries.First : _lastPublished.Next;
        while (node is not null)
        {
            result.Add(node.Value);
            node = node.Next;
        }
        _lastPublished = Entries.Last;
        return result;
    }

    private static bool TryEnsureWriter(out string path)
    {
        path = LogFilePath;
        try
        {
            if (_writer is not null)
                return true;
            Directory.CreateDirectory(LogDirectory);
            _writer = new StreamWriter(
                new FileStream(LogFilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
            {
                AutoFlush = false,
            };
            return true;
        }
        catch (Exception ex)
        {
            // Never let logging failures take down the GUI.
            System.Diagnostics.Debug.WriteLine($"[logger] cannot open log file: {ex.Message}");
            path = string.Empty;
            return false;
        }
    }

    private static void WriteToFileLocked(LogEntry entry)
    {
        if (!TryEnsureWriter(out _))
            return;
        try
        {
            _writer!.WriteLine(entry.FullText);
            _writer.Flush();

            if (_writer.BaseStream.Length > MaxFileBytes)
            {
                _writer.Dispose();
                _writer = null;
                var backup = LogFilePath + ".old";
                File.Move(LogFilePath, backup, overwrite: true);
                TryEnsureWriter(out _);
            }
        }
        catch (Exception ex)
        {
            if (!_rotationWarned)
            {
                _rotationWarned = true;
                System.Diagnostics.Debug.WriteLine($"[logger] file write failed: {ex.Message}");
            }
        }
    }
}
