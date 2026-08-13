using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace XREngine.RenderBench;

/// <summary>
/// Minimal editor-free MCP transport for RenderBench lifecycle control. The
/// listener is stopped before warmup/capture so RPC work cannot enter measured frames.
/// </summary>
public sealed class RenderBenchMcpHost(
    RenderBenchOptions options,
    RenderBenchProcessState state) : IAsyncDisposable
{
    private static readonly JsonSerializerOptions s_jsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };
    private HttpListener? _listener;
    private CancellationTokenSource? _cancellation;
    private Task? _acceptTask;
    private readonly object _handlersGate = new();
    private readonly HashSet<Task> _handlers = [];

    public bool IsRunning => _listener?.IsListening == true;

    public void Start()
    {
        if (IsRunning)
            return;

        _cancellation = new CancellationTokenSource();
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://localhost:{options.McpPort}/mcp/");
        _listener.Start();
        _acceptTask = AcceptLoopAsync(_listener, _cancellation.Token);
    }

    public async Task StopAsync()
    {
        HttpListener? listener = _listener;
        CancellationTokenSource? cancellation = _cancellation;
        Task? acceptTask = _acceptTask;
        _listener = null;
        _cancellation = null;
        _acceptTask = null;

        if (listener is null)
            return;

        cancellation?.Cancel();
        listener.Close();
        if (acceptTask is not null)
        {
            try { await acceptTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (HttpListenerException) { }
            catch (ObjectDisposedException) { }
        }
        Task[] handlers;
        lock (_handlersGate)
            handlers = [.. _handlers];
        if (handlers.Length > 0)
        {
            try { await Task.WhenAll(handlers).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (HttpListenerException) { }
            catch (ObjectDisposedException) { }
        }
        cancellation?.Dispose();
    }

    private async Task AcceptLoopAsync(HttpListener listener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && listener.IsListening)
        {
            HttpListenerContext context = await listener.GetContextAsync().ConfigureAwait(false);
            TrackHandler(HandleContextAsync(context, cancellationToken));
        }
    }

    private void TrackHandler(Task handler)
    {
        lock (_handlersGate)
            _handlers.Add(handler);
        _ = handler.ContinueWith(
            completed =>
            {
                lock (_handlersGate)
                    _handlers.Remove(completed);
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task HandleContextAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        try
        {
            string path = context.Request.Url?.AbsolutePath ?? string.Empty;
            if (context.Request.HttpMethod == "GET" && path.Equals("/mcp/status", StringComparison.OrdinalIgnoreCase))
            {
                await WriteJsonAsync(context.Response, state.Snapshot(), HttpStatusCode.OK, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (context.Request.HttpMethod == "POST" && path.Equals("/mcp/shutdown", StringComparison.OrdinalIgnoreCase))
            {
                if (!CanControl(context.Request))
                {
                    await WriteJsonAsync(context.Response, new { error = "Control policy and a valid session token are required." }, HttpStatusCode.Forbidden, cancellationToken).ConfigureAwait(false);
                    return;
                }

                state.SetPhase(RenderBenchPhase.Stopping);
                state.RequestShutdown();
                await WriteJsonAsync(context.Response, new { stopping = true, processId = Environment.ProcessId }, HttpStatusCode.OK, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (context.Request.HttpMethod != "POST" || !path.Equals("/mcp/", StringComparison.OrdinalIgnoreCase))
            {
                context.Response.StatusCode = (int)HttpStatusCode.NotFound;
                context.Response.Close();
                return;
            }

            using JsonDocument document = await JsonDocument.ParseAsync(context.Request.InputStream, cancellationToken: cancellationToken).ConfigureAwait(false);
            JsonElement root = document.RootElement;
            JsonElement? id = root.TryGetProperty("id", out JsonElement idElement) ? idElement.Clone() : null;
            string method = root.TryGetProperty("method", out JsonElement methodElement) ? methodElement.GetString() ?? string.Empty : string.Empty;
            bool startAfterResponse = IsAuthorizedStartCall(root, method, context.Request);
            object payload = method switch
            {
                "initialize" => Success(id, new
                {
                    protocolVersion = "2025-03-26",
                    capabilities = new { tools = new { listChanged = false } },
                    serverInfo = new { name = "XREngine.RenderBench", version = "1" },
                }),
                "notifications/initialized" => Success(id, new { }),
                "ping" => Success(id, new { }),
                "tools/list" => Success(id, new { tools = BuildTools() }),
                "tools/call" => HandleToolCall(root, id, context.Request),
                _ => Error(id, -32601, $"Method '{method}' is not supported."),
            };
            await WriteJsonAsync(context.Response, payload, HttpStatusCode.OK, cancellationToken).ConfigureAwait(false);
            if (startAfterResponse)
                state.RequestStart();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (context.Response.OutputStream.CanWrite)
                await WriteJsonAsync(context.Response, Error(null, -32603, exception.Message), HttpStatusCode.InternalServerError, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private object HandleToolCall(JsonElement root, JsonElement? id, HttpListenerRequest request)
    {
        if (!root.TryGetProperty("params", out JsonElement parameters) ||
            !parameters.TryGetProperty("name", out JsonElement nameElement))
            return Error(id, -32602, "tools/call requires params.name.");

        string name = nameElement.GetString() ?? string.Empty;
        return name switch
        {
            "get_render_bench_status" => ToolSuccess(id, state.Snapshot()),
            "start_render_bench" when CanControl(request) => AcceptStart(id),
            "stop_render_bench" when CanControl(request) => RequestStop(id),
            "start_render_bench" or "stop_render_bench" => Error(id, -32001, "Control policy and a valid session token are required."),
            _ => Error(id, -32602, $"Unknown RenderBench tool '{name}'."),
        };
    }

    private object AcceptStart(JsonElement? id)
    {
        if (state.Snapshot().Phase != RenderBenchPhase.Idle)
            return Error(id, -32002, "RenderBench is not idle.");
        return ToolSuccess(id, new { accepted = true });
    }

    private bool IsAuthorizedStartCall(JsonElement root, string method, HttpListenerRequest request)
    {
        if (method != "tools/call" || !CanControl(request) || state.Snapshot().Phase != RenderBenchPhase.Idle)
            return false;
        return root.TryGetProperty("params", out JsonElement parameters) &&
            parameters.TryGetProperty("name", out JsonElement name) &&
            name.ValueKind == JsonValueKind.String &&
            name.GetString() == "start_render_bench";
    }

    private object RequestStop(JsonElement? id)
    {
        state.SetPhase(RenderBenchPhase.Stopping);
        state.RequestShutdown();
        return ToolSuccess(id, new { stopping = true, processId = Environment.ProcessId });
    }

    private object[] BuildTools()
    {
        List<object> tools =
        [
            new
            {
                name = "get_render_bench_status",
                description = "Gets the dedicated Vulkan RenderBench process state and result path.",
                inputSchema = new { type = "object", properties = new { }, additionalProperties = false },
            },
        ];
        if (options.McpPolicy == RenderBenchMcpPolicy.Control)
        {
            tools.Add(new
            {
                name = "start_render_bench",
                description = "Starts the prepared deterministic benchmark. MCP is taken offline during measured frames.",
                inputSchema = new { type = "object", properties = new { }, additionalProperties = false },
            });
            tools.Add(new
            {
                name = "stop_render_bench",
                description = "Requests clean shutdown of this RenderBench process.",
                inputSchema = new { type = "object", properties = new { }, additionalProperties = false },
            });
        }
        return [.. tools];
    }

    private bool CanControl(HttpListenerRequest request)
    {
        if (options.McpPolicy != RenderBenchMcpPolicy.Control || string.IsNullOrWhiteSpace(options.SessionToken))
            return false;
        string supplied = request.Headers["X-XRE-Session-Token"] ?? string.Empty;
        byte[] expectedBytes = Encoding.UTF8.GetBytes(options.SessionToken);
        byte[] suppliedBytes = Encoding.UTF8.GetBytes(supplied);
        return expectedBytes.Length == suppliedBytes.Length && CryptographicOperations.FixedTimeEquals(expectedBytes, suppliedBytes);
    }

    private static object Success(JsonElement? id, object result)
        => new { jsonrpc = "2.0", id, result };

    private static object ToolSuccess(JsonElement? id, object value)
        => Success(id, new { content = new[] { new { type = "text", text = JsonSerializer.Serialize(value, s_jsonOptions) } }, isError = false });

    private static object Error(JsonElement? id, int code, string message)
        => new { jsonrpc = "2.0", id, error = new { code, message } };

    private static async Task WriteJsonAsync(
        HttpListenerResponse response,
        object payload,
        HttpStatusCode statusCode,
        CancellationToken cancellationToken)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(payload, s_jsonOptions);
        response.StatusCode = (int)statusCode;
        response.ContentType = "application/json";
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        response.Close();
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
