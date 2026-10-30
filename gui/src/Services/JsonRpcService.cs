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
            return await tcs.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException($"Request '{method}' timed out after {RequestTimeout}.");
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
            var stdout = await _process.GetStdoutAsync().ConfigureAwait(false);
            using var reader = new StreamReader(stdout, System.Text.Encoding.UTF8,
                detectEncodingFromByteOrderMarks: false, leaveOpen: true);

            while (!_lifetimeCts.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(_lifetimeCts.Token).ConfigureAwait(false);
                if (line is null)
                    break; // EOF — process exited
                if (line.Length == 0)
                    continue;
                ProcessLine(line);
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
        catch (Exception)
        {
            // Broken pipe / invalid stream state — fail pending requests below.
        }
        finally
        {
            _connected = false;
            FailAllPending(new IOException("Connection to ads-cli was lost."));
        }
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
                var args = new JsonRpcNotificationEventArgs(
                    methodEl.GetString() ?? string.Empty,
                    root.TryGetProperty("params", out var p) ? p.Clone() : default);
                NotificationReceived?.Invoke(this, args);
                return;
            }

            if (!root.TryGetProperty("id", out var idEl))
                return; // not something we understand

            long id;
            try
            {
                id = idEl.GetInt64();
            }
            catch
            {
                return; // we only ever send numeric ids
            }

            PendingRequest? pending;
            lock (_writeLock)
            {
                _pending.TryGetValue(id, out pending);
            }

            if (pending is null)
                return; // response for a cancelled/timed-out request

            if (root.TryGetProperty("error", out var errorEl))
            {
                var code = errorEl.TryGetProperty("code", out var c) ? c.GetInt32() : JsonRpcException.InternalError;
                var message = errorEl.TryGetProperty("message", out var m) ? m.GetString() ?? "Unknown error" : "Unknown error";
                JsonElement? data = errorEl.TryGetProperty("data", out var d) ? d.Clone() : null;
                pending.Tcs.TrySetException(new JsonRpcException(code, message, data));
            }
            else if (root.TryGetProperty("result", out var resultEl))
            {
                pending.Tcs.TrySetResult(resultEl.Clone());
            }
            else
            {
                pending.Tcs.TrySetException(new JsonRpcException(
                    JsonRpcException.InternalError, "Malformed response: neither result nor error."));
            }
        }
    }

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
        _lifetimeCts.Cancel();
        FailAllPending(new ObjectDisposedException(nameof(JsonRpcService)));

        if (_readerTask is not null)
        {
            try { await _readerTask.ConfigureAwait(false); }
            catch { /* best effort */ }
        }

        _lifetimeCts.Dispose();
    }
}
