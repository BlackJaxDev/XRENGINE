using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace XREngine.Runtime.Automation.Mcp;

/// <summary>
/// Editor-independent MCP transport with capability checks, mutation authorization,
/// idempotency, and measured-interval suspension.
/// </summary>
public sealed class McpHttpServer(
    McpHttpServerOptions options,
    McpToolRegistry registry,
    Func<McpToolContext> contextFactory) : IAsyncDisposable
{
    private static readonly JsonSerializerOptions s_jsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };
    private readonly object _lifecycleGate = new();
    private readonly object _handlerGate = new();
    private readonly object _idempotencyGate = new();
    private readonly HashSet<Task> _handlers = [];
    private readonly Dictionary<string, object> _idempotency = new(StringComparer.Ordinal);
    private readonly Queue<string> _idempotencyOrder = new();
    private HttpListener? _listener;
    private CancellationTokenSource? _cancellation;
    private Task? _acceptTask;
    private bool _disposed;

    public bool IsRunning => _listener?.IsListening == true;

    public void Start()
    {
        lock (_lifecycleGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (IsRunning)
                return;

            _cancellation = new CancellationTokenSource();
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://localhost:{options.Port}/mcp/");
            _listener.Start();
            _acceptTask = AcceptLoopAsync(_listener, _cancellation.Token);
        }
    }

    public async Task StopAsync()
    {
        HttpListener? listener;
        CancellationTokenSource? cancellation;
        Task? acceptTask;
        lock (_lifecycleGate)
        {
            listener = _listener;
            cancellation = _cancellation;
            acceptTask = _acceptTask;
            _listener = null;
            _cancellation = null;
            _acceptTask = null;
        }

        if (listener is null)
            return;

        cancellation?.Cancel();
        listener.Close();
        await IgnoreListenerShutdownAsync(acceptTask).ConfigureAwait(false);

        Task[] handlers;
        lock (_handlerGate)
            handlers = [.. _handlers.Where(static task => !task.IsCompleted)];
        if (handlers.Length > 0)
        {
            try { await Task.WhenAll(handlers).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (HttpListenerException) { }
            catch (ObjectDisposedException) { }
        }

        cancellation?.Dispose();
    }

    /// <summary>
    /// Quiesces the transport while a measured operation runs, then restores it. This is intended
    /// for background jobs which contain multiple independently measured intervals.
    /// </summary>
    public async Task RunWithTransportSuspendedAsync(Func<Task> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        await StopAsync().ConfigureAwait(false);
        try
        {
            await operation().ConfigureAwait(false);
        }
        finally
        {
            bool restart;
            lock (_lifecycleGate)
                restart = !_disposed;
            if (restart)
                Start();
        }
    }

    private async Task AcceptLoopAsync(HttpListener listener, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested && listener.IsListening)
            {
                HttpListenerContext context = await listener.GetContextAsync().ConfigureAwait(false);
                Track(HandleContextAsync(context, cancellationToken));
            }
        }
        catch (Exception exception) when (
            cancellationToken.IsCancellationRequested &&
            exception is HttpListenerException or ObjectDisposedException)
        {
        }
    }

    private void Track(Task task)
    {
        lock (_handlerGate)
            _handlers.Add(task);
        _ = task.ContinueWith(
            completed =>
            {
                lock (_handlerGate)
                    _handlers.Remove(completed);
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task HandleContextAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        McpToolResponse? toolResponse = null;
        try
        {
            string path = context.Request.Url?.AbsolutePath ?? string.Empty;
            if (context.Request.HttpMethod == "GET" &&
                path.Equals("/mcp/status", StringComparison.OrdinalIgnoreCase) &&
                options.StatusProvider is not null)
            {
                await WriteJsonAsync(context.Response, options.StatusProvider(), HttpStatusCode.OK, cancellationToken).ConfigureAwait(false);
                return;
            }
            if (context.Request.HttpMethod == "POST" &&
                path.Equals("/mcp/shutdown", StringComparison.OrdinalIgnoreCase) &&
                options.ShutdownRequested is not null)
            {
                if (!IsAuthorized(context.Request, McpPermissionLevel.Mutating))
                {
                    await WriteJsonAsync(context.Response, new { error = "A valid control session token is required." }, HttpStatusCode.Forbidden, cancellationToken).ConfigureAwait(false);
                    return;
                }
                await WriteJsonAsync(context.Response, new { stopping = true, processId = Environment.ProcessId }, HttpStatusCode.OK, cancellationToken).ConfigureAwait(false);
                options.ShutdownRequested();
                return;
            }
            if (context.Request.HttpMethod != "POST" || !path.Equals("/mcp/", StringComparison.OrdinalIgnoreCase))
            {
                context.Response.StatusCode = (int)HttpStatusCode.NotFound;
                context.Response.Close();
                return;
            }

            using JsonDocument document = await JsonDocument.ParseAsync(
                context.Request.InputStream,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            JsonElement root = document.RootElement;
            JsonElement? id = root.TryGetProperty("id", out JsonElement idElement) ? idElement.Clone() : null;
            string method = root.TryGetProperty("method", out JsonElement methodElement)
                ? methodElement.GetString() ?? string.Empty
                : string.Empty;
            object payload;
            if (method == "tools/call")
                (payload, toolResponse) = await HandleToolCallAsync(root, id, context.Request, cancellationToken).ConfigureAwait(false);
            else
                payload = method switch
                {
                    "initialize" => Success(id, new
                    {
                        protocolVersion = "2025-03-26",
                        capabilities = new { tools = new { listChanged = false } },
                        serverInfo = new { name = options.ServerName, version = options.ServerVersion },
                    }),
                    "notifications/initialized" => Success(id, new { }),
                    "ping" => Success(id, new { }),
                    "tools/list" => Success(id, new { tools = BuildToolList() }),
                    _ => Error(id, -32601, $"Method '{method}' is not supported."),
                };

            await WriteJsonAsync(context.Response, payload, HttpStatusCode.OK, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (context.Response.OutputStream.CanWrite)
                await WriteJsonAsync(context.Response, Error(null, -32603, exception.Message), HttpStatusCode.InternalServerError, CancellationToken.None).ConfigureAwait(false);
        }

        if (toolResponse?.SuspendTransportUntil is not null)
            ScheduleSuspension(toolResponse.SuspendTransportUntil, toolResponse.AfterResponse);
        else if (toolResponse?.AfterResponse is not null)
            await toolResponse.AfterResponse().ConfigureAwait(false);
    }

    private async Task<(object Payload, McpToolResponse? Response)> HandleToolCallAsync(
        JsonElement root,
        JsonElement? id,
        HttpListenerRequest request,
        CancellationToken cancellationToken)
    {
        if (!root.TryGetProperty("params", out JsonElement parameters) ||
            !parameters.TryGetProperty("name", out JsonElement nameElement) ||
            nameElement.ValueKind != JsonValueKind.String)
            return (Error(id, -32602, "tools/call requires params.name."), null);

        string name = nameElement.GetString() ?? string.Empty;
        if (!registry.TryGet(name, out McpToolDefinition? tool) || tool is null)
            return (Error(id, -32601, $"Tool '{name}' was not found."), null);
        if (!IsAuthorized(request, tool.Permission))
            return (Error(id, -32001, $"Tool '{name}' requires {tool.Permission} permission and a valid session token."), null);

        McpToolContext toolContext = contextFactory();
        McpCapability missing = tool.RequiredCapabilities & ~toolContext.Capabilities;
        if (missing != McpCapability.None)
        {
            string message = $"Tool '{name}' requires unavailable capabilities: {McpCapabilityNames.Format(missing)}.";
            return (Error(id, -32002, message), null);
        }

        JsonElement arguments = parameters.TryGetProperty("arguments", out JsonElement argumentElement)
            ? argumentElement
            : default;
        if (arguments.ValueKind == JsonValueKind.Undefined)
            arguments = JsonDocument.Parse("{}").RootElement.Clone();
        else if (arguments.ValueKind != JsonValueKind.Object)
            return (Error(id, -32602, "Tool arguments must be a JSON object."), null);

        string? idempotencyKey = parameters.TryGetProperty("idempotency_key", out JsonElement keyElement) &&
            keyElement.ValueKind == JsonValueKind.String
                ? keyElement.GetString()
                : null;
        string? scopedIdempotencyKey = string.IsNullOrWhiteSpace(idempotencyKey)
            ? null
            : $"{name}:{idempotencyKey}";
        if (scopedIdempotencyKey is not null && TryGetIdempotent(scopedIdempotencyKey, out object? cached))
            return (Success(id, cached!), null);

        McpToolResponse response;
        try
        {
            response = await tool.Handler(toolContext, arguments, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            response = new McpToolResponse(exception.Message, IsError: true);
        }

        object result = new
        {
            content = BuildContent(response),
            isError = response.IsError,
            structuredContent = response.Data,
        };
        if (!response.IsError && scopedIdempotencyKey is not null)
            StoreIdempotent(scopedIdempotencyKey, result);
        return (Success(id, result), response);
    }

    private object[] BuildToolList()
        => registry.Tools.Select(static tool => (object)new
        {
            name = tool.Name,
            description = tool.Description,
            inputSchema = tool.InputSchema,
        }).ToArray();

    private static object[] BuildContent(McpToolResponse response)
    {
        if (response.Data is null)
            return [new { type = "text", text = response.Message }];
        return
        [
            new { type = "text", text = response.Message },
            new { type = "text", text = JsonSerializer.Serialize(response.Data, s_jsonOptions) },
        ];
    }

    private bool IsAuthorized(HttpListenerRequest request, McpPermissionLevel permission)
    {
        if (permission == McpPermissionLevel.ReadOnly)
            return true;
        if (!options.AllowMutations || string.IsNullOrWhiteSpace(options.SessionToken))
            return false;

        string supplied = request.Headers["X-XRE-Session-Token"] ?? string.Empty;
        byte[] expectedBytes = Encoding.UTF8.GetBytes(options.SessionToken);
        byte[] suppliedBytes = Encoding.UTF8.GetBytes(supplied);
        return expectedBytes.Length == suppliedBytes.Length &&
            CryptographicOperations.FixedTimeEquals(expectedBytes, suppliedBytes);
    }

    private bool TryGetIdempotent(string key, out object? value)
    {
        lock (_idempotencyGate)
            return _idempotency.TryGetValue(key, out value);
    }

    private void StoreIdempotent(string key, object value)
    {
        lock (_idempotencyGate)
        {
            if (_idempotency.ContainsKey(key))
                return;
            while (_idempotency.Count >= options.MaxIdempotencyEntries && _idempotencyOrder.TryDequeue(out string? oldest))
                _idempotency.Remove(oldest);
            _idempotency.Add(key, value);
            _idempotencyOrder.Enqueue(key);
        }
    }

    private void ScheduleSuspension(Task completion, Func<Task>? afterResponse)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Yield();
                await StopAsync().ConfigureAwait(false);
                if (afterResponse is not null)
                    await afterResponse().ConfigureAwait(false);
                try { await completion.ConfigureAwait(false); }
                catch { }
            }
            finally
            {
                bool restart;
                lock (_lifecycleGate)
                    restart = !_disposed;
                if (restart)
                    Start();
            }
        });
    }

    private static object Success(JsonElement? id, object result) => new { jsonrpc = "2.0", id, result };

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

    private static async Task IgnoreListenerShutdownAsync(Task? task)
    {
        if (task is null)
            return;
        try { await task.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (HttpListenerException) { }
        catch (ObjectDisposedException) { }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_lifecycleGate)
            _disposed = true;
        await StopAsync().ConfigureAwait(false);
    }
}
