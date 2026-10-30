using System.Diagnostics;
using System.IO;
using NtfsStreamsViewer.Services;

namespace NtfsStreamsViewer.Services;

public sealed class ProcessExitedEventArgs : EventArgs
{
    public int ExitCode { get; init; }
    public bool IsExpectedShutdown { get; init; }

    public ProcessExitedEventArgs(int exitCode, bool isExpectedShutdown)
    {
        ExitCode = exitCode;
        IsExpectedShutdown = isExpectedShutdown;
    }
}

/// <summary>
/// Owns the lifetime of the <c>ads-cli serve --stdio</c> sidecar process and
/// exposes its stdin/stdout streams to the JSON-RPC layer (docs/GUI.md).
/// </summary>
public interface ICoreProcessService : IAsyncDisposable
{
    /// <summary>Stream for sending requests to the core (the sidecar's stdin).</summary>
    Task<Stream> GetStdinAsync();

    /// <summary>Stream for receiving responses from the core (the sidecar's stdout).</summary>
    Task<Stream> GetStdoutAsync();

    /// <summary>Raised when the sidecar process exits (crash or expected shutdown).</summary>
    event EventHandler<ProcessExitedEventArgs>? ProcessExited;

    /// <summary>Restart the sidecar process (e.g. after a crash).</summary>
    Task RestartAsync();

    /// <summary>Start the sidecar if it is not running yet.</summary>
    Task EnsureStartedAsync();
}

/// <inheritdoc cref="ICoreProcessService"/>
public sealed class CoreProcessService : ICoreProcessService
{
    private readonly object _sync = new();
    private Process? _process;
    private bool _isExpectedShutdown;
    private bool _disposed;
    private string? _resolvedBinaryPath;

    public event EventHandler<ProcessExitedEventArgs>? ProcessExited;

    /// <summary>Optional explicit path to the ads-cli binary (overrides discovery).</summary>
    public string? ExplicitBinaryPath { get; set; }

    public async Task EnsureStartedAsync()
    {
        lock (_sync)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(CoreProcessService));
            if (_process is { HasExited: false })
                return;
        }

        await StartProcessAsync().ConfigureAwait(false);
    }

    public async Task RestartAsync()
    {
        lock (_sync)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(CoreProcessService));
        }

        await StopProcessAsync(expected: true).ConfigureAwait(false);
        lock (_sync)
        {
            _resolvedBinaryPath = null; // re-discover on restart
        }
        await StartProcessAsync().ConfigureAwait(false);
    }

    public async Task<Stream> GetStdinAsync()
    {
        await EnsureStartedAsync().ConfigureAwait(false);
        lock (_sync)
        {
            if (_process is null || _disposed)
                throw new InvalidOperationException("Core process is not running.");
            return _process.StandardInput.BaseStream;
        }
    }

    public async Task<Stream> GetStdoutAsync()
    {
        await EnsureStartedAsync().ConfigureAwait(false);
        lock (_sync)
        {
            if (_process is null || _disposed)
                throw new InvalidOperationException("Core process is not running.");
            return _process.StandardOutput.BaseStream;
        }
    }

    private Task StartProcessAsync()
    {
        var binaryPath = FindBinary();
        if (binaryPath is null)
        {
            throw new FileNotFoundException(
                "ads-cli binary not found. Build it with `cargo build --release -p ads-cli` " +
                "or place it next to the GUI executable / in PATH.");
        }

        var psi = new ProcessStartInfo
        {
            FileName = binaryPath,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("serve");
        psi.ArgumentList.Add("--stdio");

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.Exited += OnProcessExited;

        try
        {
            process.Start();
        }
        catch
        {
            process.Dispose();
            throw;
        }

        // Drain stderr so the pipe never fills up and blocks the sidecar.
        _ = Task.Run(async () =>
        {
            try
            {
                var stderr = process.StandardError;
                while (!stderr.EndOfStream)
                {
                    var line = await stderr.ReadLineAsync().ConfigureAwait(false);
                    if (line is not null)
                        System.Diagnostics.Debug.WriteLine($"[ads-cli] {line}");
                }
            }
            catch
            {
                // Process killed mid-read; nothing useful to log here.
            }
        });

        lock (_sync)
        {
            _resolvedBinaryPath = binaryPath;
            _process = process;
            _isExpectedShutdown = false;
        }

        return Task.CompletedTask;
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        int exitCode = 0;
        bool expected;
        lock (_sync)
        {
            try
            {
                exitCode = _process?.ExitCode ?? -1;
            }
            catch
            {
                exitCode = -1;
            }
            expected = _isExpectedShutdown;
        }
        ProcessExited?.Invoke(this, new ProcessExitedEventArgs(exitCode, expected));
    }

    private async Task StopProcessAsync(bool expected)
    {
        Process? process;
        lock (_sync)
        {
            _isExpectedShutdown = expected;
            process = _process;
            _process = null;
        }

        if (process is null)
            return;

        try
        {
            if (!process.HasExited)
            {
                try
                {
                    // Closing stdin makes `serve --stdio` shut down gracefully.
                    process.StandardInput.Close();
                }
                catch
                {
                    // ignore — fall back to kill
                }

                if (!await WaitForExitAsync(process, TimeSpan.FromSeconds(3)).ConfigureAwait(false))
                {
                    try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
                    await WaitForExitAsync(process, TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            process.Exited -= OnProcessExited;
            process.Dispose();
        }
    }

    private static Task<bool> WaitForExitAsync(Process process, TimeSpan timeout)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        process.WaitForExitAsync(CancellationToken.None).ContinueWith(t =>
        {
            tcs.TrySetResult(true);
        }, TaskScheduler.Default);
        return Task.WhenAny(tcs.Task, Task.Delay(timeout)).ContinueWith(
            t => t.Result == tcs.Task, TaskScheduler.Default);
    }

    /// <summary>
    /// Binary discovery order (docs/GUI.md):
    /// 1. next to the GUI executable, 2. ../cli/target/release/ (dev layout), 3. PATH.
    /// </summary>
    private string? FindBinary()
    {
        lock (_sync)
        {
            if (_resolvedBinaryPath is not null && File.Exists(_resolvedBinaryPath))
                return _resolvedBinaryPath;
        }

        if (!string.IsNullOrEmpty(ExplicitBinaryPath) && File.Exists(ExplicitBinaryPath))
            return ExplicitBinaryPath;

        var binaryName = OperatingSystem.IsWindows() ? "ads-cli.exe" : "ads-cli";
        var candidates = new List<string>();

        // 1. Next to the GUI executable.
        var baseDir = AppContext.BaseDirectory;
        candidates.Add(Path.Combine(baseDir, binaryName));
        candidates.Add(Path.Combine(baseDir, "bin", binaryName));

        // 2. Development layout: gui/src -> ../../cli/target/release
        try
        {
            var devPath = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..",
                "cli", "target", "release", binaryName));
            candidates.Add(devPath);
            var debugPath = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..",
                "cli", "target", "debug", binaryName));
            candidates.Add(debugPath);
        }
        catch
        {
            // malformed relative path — skip
        }

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
                return candidate;
        }

        // 3. PATH lookup.
        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var full = Path.Combine(dir.Trim(), binaryName);
                if (File.Exists(full))
                    return full;
            }
            catch
            {
                // invalid PATH entry — skip
            }
        }

        return null;
    }

    public async ValueTask DisposeAsync()
    {
        bool alreadyDisposed;
        lock (_sync)
        {
            alreadyDisposed = _disposed;
            _disposed = true;
        }

        if (!alreadyDisposed)
        {
            await StopProcessAsync(expected: true).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Emergency fallback used on application exit when graceful shutdown timed out:
    /// kills the sidecar process tree synchronously so it never outlives the GUI.
    /// </summary>
    public void ForceTerminate()
    {
        Process? process;
        lock (_sync)
        {
            process = _process;
        }

        if (process is null)
            return;

        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // already gone or access denied — nothing else we can do at exit time
        }
    }
}
