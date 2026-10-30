using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace NtfsStreamsViewer.Services;

/// <summary>A server-sent JSON-RPC notification (a message without an `id`).</summary>
public sealed class JsonRpcNotificationEventArgs : EventArgs
{
    public string Method { get; }
    public JsonElement Params { get; }

    public JsonRpcNotificationEventArgs(string method, JsonElement parameters)
    {
        Method = method;
        Params = parameters;
    }
}

/// <summary>Error carried by a JSON-RPC error response (SPEC.md §5.2).</summary>
public sealed class JsonRpcException : Exception
{
    public int Code { get; }

    /// <summary>
    /// Structured error data from the JSON-RPC error object (SPEC.md §5.2).
    /// Declared with <c>new</c> because it hides <see cref="Exception.Data"/>.
    /// </summary>
    [JsonPropertyName("data")]
    public new JsonElement? Data { get; }

    public JsonRpcException(int code, string message, JsonElement? data = null)
        : base($"JSON-RPC error {code}: {message}")
    {
        Code = code;
        Data = data;
    }

    // Standard JSON-RPC codes
    public const int ParseError = -32700;
    public const int InvalidRequest = -32600;
    public const int MethodNotFound = -32601;
    public const int InvalidParams = -32602;
    public const int InternalError = -32603;

    // Application codes (SPEC.md §5.2)
    public const int AccessDenied = -32001;
    public const int NotFound = -32002;
    public const int NotSupported = -32003;
    public const int VolumeReadOnly = -32004;
    public const int Cancelled = -32005;
}

/// <summary>
/// Minimal JSON-RPC 2.0 client over line-delimited stdin/stdout provided by
/// <see cref="ICoreProcessService"/>. Correlates requests by id and surfaces
/// server notifications via <see cref="NotificationReceived"/>.
/// </summary>
public interface IJsonRpcService : IAsyncDisposable
{
    Task<T> SendRequestAsync<T>(string method, object? parameters, CancellationToken ct = default);

    /// <summary>Sends a request whose result is not consumed (used for probes/shutdown).</summary>
    Task SendRequestAsync(string method, object? parameters, CancellationToken ct = default);

    event EventHandler<JsonRpcNotificationEventArgs>? NotificationReceived;

    /// <summary>Default per-request timeout. All operations must have timeouts (SPEC.md §8).</summary>
    TimeSpan RequestTimeout { get; set; }
}

public sealed class JsonRpcService : IJsonRpcService
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly ICoreProcessService _process;
    private readonly object _writeLock = new();
    private readonly Dictionary<long, PendingRequest> _pending = new();
    private readonly CancellationTokenSource _lifetimeCts = new();
    private Task? _readerTask;
    private long _nextId;
    private volatile bool _connected;

    public event EventHandler<JsonRpcNotificationEventArgs>? NotificationReceived;

    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);

    public JsonRpcService(ICoreProcessService process)
    {
        _process = process;
        _process.ProcessExited += OnProcessExited;
    }

    private sealed class PendingRequest
    {
        public required TaskCompletionSource<JsonElement> Tcs { get; init; }
    }

    private void EnsureReader()
    {
        if (_connected && _readerTask is not null)
            return;

        _readerTask = Task.Run(ReadLoopAsync);
    }

    public async Task<T> SendRequestAsync<T>(string method, object? parameters, CancellationToken ct = default)
    {
        var result = await SendInternalAsync(method, parameters, ct).ConfigureAwait(false);
        return result.ValueKind == JsonValueKind.Undefined
            ? default!
            : result.Deserialize<T>(SerializerOptions)!;
    }

    public async Task SendRequestAsync(string method, object? parameters, CancellationToken ct = default)
    {
        await SendInternalAsync(method, parameters, ct).ConfigureAwait(false);
    }

    private async Task<JsonElement> SendInternalAsync(string method, object? parameters, CancellationToken ct)
    {
        EnsureReader();

        var stdin = await _process.GetStdinAsync().ConfigureAwait(false);
        var id = Interlocked.Increment(ref _nextId);

        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method,
        };
        if (parameters is not null)
        {
            request["params"] = JsonSerializer.SerializeToNode(parameters, SerializerOptions);
        }
        else
        {
            request["params"] = new JsonObject();
        }

        var line = request.ToJsonString() + "\n";

        if (AppLogger.IsEnabled(LogLevel.Debug))
            AppLogger.Debug("jsonrpc", $"→ {method} (id {id}): {request.ToJsonString()}");

        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_writeLock)
        {
            _pending[id] = new PendingRequest { Tcs = tcs };
        }

        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetimeCts.Token);
            linked.CancelAfter(RequestTimeout);

            var bytes = System.Text.Encoding.UTF8.GetBytes(line);
            await stdin.WriteAsync(bytes, linked.Token).ConfigureAwait(false);
            await stdin.FlushAsync(linked.Token).ConfigureAwait(false);

            using var reg = linked.Token.Register(() => tcs.TrySetCanceled(linked.Token));
            JsonElement result;
            try
            {
                result = await tcs.Task.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && !tcs.Task.IsCompleted)
            {
                // The request timed out but the sidecar may still answer it later.
                // Drop it from the pending table so a stale response is ignored instead of leaking.
                AppLogger.Warning("jsonrpc", $"Request '{method}' (id {id}) timed out after {RequestTimeout}.");
                throw new TimeoutException($"Request '{method}' timed out after {RequestTimeout}.");
            }
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            AppLogger.Info("jsonrpc", $"Request '{method}' was cancelled by the user.");
            throw;
        }
        catch (OperationCanceledException)
        {
            AppLogger.Warning("jsonrpc", $"Request '{method}' timed out after {RequestTimeout}.");
            throw new TimeoutException($"Request '{method}' timed out after {RequestTimeout}.");
        }
        catch (Exception ex)
        {
            AppLogger.Error("jsonrpc", $"Failed to send request '{method}'.", ex);
            throw;
        }
        finally
        {
            lock (_writeLock)
            {
                _pending.Remove(id);
            }
        }
    }

    private async Task ReadLoopAsync()
    {
        _connected = true;
        try
        {
            var stdout = await GetStdoutSafeAsync().ConfigureAwait(false);
            using var reader = new StreamReader(stdout, System.Text.Encoding.UTF8,
                detectEncodingFromByteOrderMarks: false, leaveOpen: true);

            while (!_lifetimeCts.IsCancellationRequested)
            {
                string? line;
                try
                {
                    line = await reader.ReadLineAsync(_lifetimeCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break; // shutting down
                }
                if (line is null)
                {
                    AppLogger.Info("jsonrpc", "stdout reached EOF — sidecar closed the connection.");
                    break; // EOF — process exited
                }
                if (line.Length == 0)
                    continue;
                ProcessLine(line);
            }
        }
        catch (ObjectDisposedException)
        {
            // shutting down
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
        catch (Exception ex)
        {
            // Broken pipe / invalid stream state — fail pending requests below.
            AppLogger.Error("jsonrpc", "Reader loop terminated.", ex);
        }
        finally
        {
            _connected = false;
            FailAllPending(new IOException("Connection to ads-cli was lost."));
        }
    }

    /// <summary>
    /// Gets the stdout stream, but never blocks forever on a process that failed to start:
    /// if shutdown was requested first, we throw instead of hanging in EnsureStartedAsync.
    /// </summary>
    private async Task<Stream> GetStdoutSafeAsync()
    {
        var getStdout = _process.GetStdoutAsync();
        var delay = Task.Delay(Timeout.Infinite, _lifetimeCts.Token);
        var completed = await Task.WhenAny(getStdout, delay).ConfigureAwait(false);
        if (completed != getStdout)
        {
            // Don't leave the original task unobserved.
            _ = getStdout.ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
            throw new OperationCanceledException("Shutting down before the sidecar started.");
        }
        return await getStdout.ConfigureAwait(false);
    }

    private void ProcessLine(string line)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            // Invalid JSON → log and continue (docs/GUI.md error handling).
            System.Diagnostics.Debug.WriteLine($"[jsonrpc] Skipping invalid JSON: {line}");
            return;
        }

        using (doc)
        {
            var root = doc.RootElement;

            // Notification: has "method" but no "id".
            if (root.TryGetProperty("method", out var methodEl) &&
                !root.TryGetProperty("id", out _))
            {
                var methodName = methodEl.GetString() ?? string.Empty;
                // scan.progress fires many times per second on large trees — don't log each one.
                if (AppLogger.IsEnabled(LogLevel.Debug) && methodName != "scan.progress")
                    AppLogger.Debug("jsonrpc", $"← notification {methodName}: {Truncate(line, 400)}");
                var args = new JsonRpcNotificationEventArgs(
                    methodName,
                    root.TryGetProperty("params", out var p) ? p.Clone() : default);
                try
                {
                    NotificationReceived?.Invoke(this, args);
                }
                catch (Exception ex)
                {
                    // A buggy handler must never tear down the reader loop.
                    AppLogger.Error("jsonrpc", $"Notification handler for '{methodName}' threw.", ex);
                }
                return;
            }

            if (!root.TryGetProperty("id", out var idEl))
            {
                AppLogger.Warning("jsonrpc", $"Ignoring message without id: {Truncate(line, 200)}");
                return; // not something we understand
            }

            long id;
            try
            {
                id = idEl.GetInt64();
            }
            catch
            {
                AppLogger.Warning("jsonrpc", $"Ignoring response with non-numeric id: {Truncate(line, 200)}");
                return; // we only ever send numeric ids
            }

            PendingRequest? pending;
            lock (_writeLock)
            {
                _pending.TryGetValue(id, out pending);
            }

            if (pending is null)
            {
                if (AppLogger.IsEnabled(LogLevel.Debug))
                    AppLogger.Debug("jsonrpc", $"Response for unknown/expired id {id}: {Truncate(line, 200)}");
                return; // response for a cancelled/timed-out request
            }

            if (root.TryGetProperty("error", out var errorEl))
            {
                var code = errorEl.TryGetProperty("code", out var c) ? c.GetInt32() : JsonRpcException.InternalError;
                var message = errorEl.TryGetProperty("message", out var m) ? m.GetString() ?? "Unknown error" : "Unknown error";
                JsonElement? data = errorEl.TryGetProperty("data", out var d) ? d.Clone() : null;
                AppLogger.Error("jsonrpc", $"← error response (id {id}, code {code}): {message}");
                pending.Tcs.TrySetException(new JsonRpcException(code, message, data));
            }
            else if (root.TryGetProperty("result", out var resultEl))
            {
                if (AppLogger.IsEnabled(LogLevel.Debug))
                    AppLogger.Debug("jsonrpc", $"← result (id {id}): {Truncate(resultEl.GetRawText(), 300)}");
                pending.Tcs.TrySetResult(resultEl.Clone());
            }
            else
            {
                AppLogger.Error("jsonrpc", $"Malformed response (id {id}): {Truncate(line, 300)}");
                pending.Tcs.TrySetException(new JsonRpcException(
                    JsonRpcException.InternalError, "Malformed response: neither result nor error."));
            }
        }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    private void FailAllPending(Exception ex)
    {
        PendingRequest[] snapshot;
        lock (_writeLock)
        {
            snapshot = _pending.Values.ToArray();
            _pending.Clear();
        }
        foreach (var p in snapshot)
            p.Tcs.TrySetException(ex);
    }

    private void OnProcessExited(object? sender, ProcessExitedEventArgs e)
    {
        // A crashed sidecar must not crash the GUI — surface the failure to callers.
        FailAllPending(new IOException(
            e.IsExpectedShutdown
                ? "ads-cli process was shut down."
                : $"ads-cli process exited unexpectedly (code {e.ExitCode})."));
    }

    public async ValueTask DisposeAsync()
    {
        _process.ProcessExited -= OnProcessExited;

        // Ask the sidecar to shut down gracefully (it exits on stdin EOF) *before* cancelling
        // our own lifetime token — otherwise a pending EnsureStartedAsync inside the reader
        // loop could block forever and hang application exit.
        try
        {
            var stdin = await _process.GetStdinAsync().ConfigureAwait(false);
            stdin.Close();
        }
        catch (Exception ex)
        {
            AppLogger.Debug("jsonrpc", $"closing stdin during dispose: {ex.Message}");
        }

        _lifetimeCts.Cancel();
        FailAllPending(new ObjectDisposedException(nameof(JsonRpcService)));

        if (_readerTask is not null)
        {
            try
            {
                var finished = await Task.WhenAny(_readerTask, Task.Delay(2000)).ConfigureAwait(false);
                if (finished != _readerTask)
                    AppLogger.Warning("jsonrpc", "Reader task did not finish within 2 s during shutdown.");
                else
                    await _readerTask.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                AppLogger.Debug("jsonrpc", $"reader task ended with: {ex.Message}");
            }
        }

        _lifetimeCts.Dispose();
    }
}
