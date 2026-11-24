using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LocalAgentBroker.Orchestration;

namespace XREngine.AgentRuntimes;

/// <summary>
/// Exposes only host-approved run tools to one local client over loopback MCP.
/// Every call passes through the run's authorization and evidence session.
/// </summary>
internal sealed class AgentRuntimeMcpBridge : IAsyncDisposable
{
    private const int MaximumBodyBytes = 8 * 1024 * 1024;
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _callGate = new(1, 1);
    private readonly SemaphoreSlim _clientSlots = new(8, 8);
    private readonly AgentRuntimeToolSession _tools;
    private readonly HashSet<Task> _clients = [];
    private readonly string _path = "/" + Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
    private readonly string _bearer = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private Task? _loop;

    public AgentRuntimeMcpBridge(AgentRuntimeToolSession tools) => _tools = tools;

    public string Url { get; private set; } = string.Empty;
    public string Bearer => _bearer;
    public AgentToolProviderException? Failure { get; private set; }

    public void Start()
    {
        _listener.Start(4);
        int port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        Url = $"http://127.0.0.1:{port}{_path}";
        _loop = AcceptAsync(_stop.Token);
    }

    private async Task AcceptAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            TcpClient client;
            bool slotAcquired = false;
            try
            {
                await _clientSlots.WaitAsync(token);
                slotAcquired = true;
                client = await _listener.AcceptTcpClientAsync(token);
            }
            catch (OperationCanceledException)
            {
                if (slotAcquired)
                    _clientSlots.Release();
                break;
            }
            catch (ObjectDisposedException)
            {
                if (slotAcquired)
                    _clientSlots.Release();
                break;
            }
            catch (SocketException) when (token.IsCancellationRequested)
            {
                if (slotAcquired)
                    _clientSlots.Release();
                break;
            }
            Task task = ServeAndReleaseAsync(client, token);
            lock (_clients)
                _clients.Add(task);
            _ = task.ContinueWith(completed =>
            {
                lock (_clients)
                    _clients.Remove(completed);
            }, TaskScheduler.Default);
        }
    }

    private async Task ServeAndReleaseAsync(TcpClient client, CancellationToken token)
    {
        try
        {
            await ServeAsync(client, token);
        }
        finally
        {
            _clientSlots.Release();
        }
    }

    private async Task ServeAsync(TcpClient client, CancellationToken token)
    {
        using (client)
        {
            bool authorized = false;
            try
            {
                using NetworkStream stream = client.GetStream();
                using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                readTimeout.CancelAfter(TimeSpan.FromSeconds(60));
                byte[] header = new byte[16_384];
                int used = 0;
                while (used < header.Length)
                {
                    int count = await stream.ReadAsync(header.AsMemory(used, 1), readTimeout.Token);
                    if (count == 0)
                        return;
                    used += count;
                    if (used >= 4 && header.AsSpan(used - 4, 4).SequenceEqual("\r\n\r\n"u8))
                        break;
                }
                if (used >= header.Length)
                    return;
                string requestHeader = Encoding.ASCII.GetString(header, 0, used);
                string[] lines = requestHeader.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
                if (lines.Length == 0 || lines[0] != $"POST {_path} HTTP/1.1")
                {
                    await WriteResponseAsync(stream, 404, null, token);
                    return;
                }

                int length = -1;
                bool sawLength = false;
                bool sawAuthorization = false;
                bool unsupportedTransfer = false;
                foreach (string line in lines.Skip(1))
                {
                    int colon = line.IndexOf(':');
                    if (colon < 1)
                        continue;
                    string name = line[..colon];
                    string value = line[(colon + 1)..].Trim();
                    if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                    {
                        if (sawLength || !int.TryParse(value, System.Globalization.NumberStyles.None,
                            System.Globalization.CultureInfo.InvariantCulture, out length))
                        {
                            await WriteResponseAsync(stream, 400, null, token);
                            return;
                        }
                        sawLength = true;
                    }
                    else if (name.Equals("Authorization", StringComparison.OrdinalIgnoreCase))
                    {
                        if (sawAuthorization)
                        {
                            await WriteResponseAsync(stream, 400, null, token);
                            return;
                        }
                        authorized = string.Equals(value, "Bearer " + _bearer, StringComparison.Ordinal);
                        sawAuthorization = true;
                    }
                    else if (name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase))
                        unsupportedTransfer = true;
                }
                if (unsupportedTransfer)
                {
                    await WriteResponseAsync(stream, 400, null, token);
                    return;
                }
                if (!authorized)
                {
                    await WriteResponseAsync(stream, 401, null, token);
                    return;
                }
                if (length is < 0 or > MaximumBodyBytes)
                {
                    await WriteResponseAsync(stream, 413, null, token);
                    return;
                }
                byte[] body = new byte[length];
                await stream.ReadExactlyAsync(body, readTimeout.Token);
                readTimeout.CancelAfter(Timeout.InfiniteTimeSpan);
                JsonObject? request = JsonNode.Parse(body, documentOptions: new JsonDocumentOptions { MaxDepth = 64 }) as JsonObject;
                if (request is null || request["jsonrpc"]?.GetValue<string>() != "2.0")
                {
                    await WriteResponseAsync(stream, 400, null, token);
                    return;
                }
                JsonNode? id = request["id"]?.DeepClone();
                string method = request["method"]?.GetValue<string>() ?? string.Empty;
                if (id is null && method.StartsWith("notifications/", StringComparison.Ordinal))
                {
                    await WriteResponseAsync(stream, 202, null, token);
                    return;
                }
                if (id is null)
                {
                    await WriteResponseAsync(stream, 400, null, token);
                    return;
                }
                JsonObject result = method switch
                {
                    "initialize" => new JsonObject
                    {
                        ["protocolVersion"] = "2025-03-26",
                        ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
                        ["serverInfo"] = new JsonObject { ["name"] = "xrengine-run", ["version"] = "1" },
                    },
                    "tools/list" => ListTools(),
                    "tools/call" => await CallToolAsync(request["params"] as JsonObject, token),
                    _ => new JsonObject { ["isError"] = true, ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "Unsupported MCP method." }) },
                };
                await WriteResponseAsync(stream, 200,
                    new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result }, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // The owning run stopped this client and its tool call.
            }
            catch (Exception exception) when (exception is IOException or JsonException or OperationCanceledException or ArgumentException or SocketException or InvalidOperationException or AgentToolProviderException)
            {
                if (authorized)
                    Failure ??= new AgentToolProviderException(AgentFailureCategory.ToolError,
                        "The host tool bridge could not complete an authorized request.");
            }
        }
    }

    private JsonObject ListTools()
    {
        var array = new JsonArray();
        foreach (AgentToolDefinition tool in _tools.Tools)
        {
            JsonNode schema = JsonNode.Parse(tool.InputSchemaJson, documentOptions: new JsonDocumentOptions { MaxDepth = 64 })
                ?? throw new InvalidDataException("A host tool has an invalid input schema.");
            array.Add(new JsonObject
            {
                ["name"] = tool.Name,
                ["description"] = tool.Description,
                ["inputSchema"] = schema,
                ["annotations"] = new JsonObject { ["readOnlyHint"] = tool.IsReadOnly, ["destructiveHint"] = tool.IsDestructive },
            });
        }
        return new JsonObject { ["tools"] = array };
    }

    private async Task<JsonObject> CallToolAsync(JsonObject? parameters, CancellationToken token)
    {
        if (parameters is null)
            throw new InvalidDataException("The MCP tool request is missing parameters.");
        string name = parameters["name"]?.GetValue<string>() ?? string.Empty;
        string arguments = parameters["arguments"]?.ToJsonString() ?? "{}";
        await _callGate.WaitAsync(token);
        try
        {
            AgentToolResult result = await _tools.ExecuteAsync(new AgentToolCall
            {
                CallId = Guid.NewGuid().ToString("N"), Name = name, ArgumentsJson = arguments,
            }, token);
            var content = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = result.Content });
            if (result.ImageDataUri is { } image
                && image.StartsWith("data:image/png;base64,", StringComparison.Ordinal))
                content.Add(new JsonObject { ["type"] = "image", ["mimeType"] = "image/png", ["data"] = image[22..] });
            return new JsonObject { ["isError"] = result.IsError, ["content"] = content };
        }
        catch (AgentToolProviderException exception)
        {
            Failure ??= exception;
            return new JsonObject
            {
                ["isError"] = true,
                ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "The host denied or could not complete this tool call." }),
            };
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            Failure ??= new AgentToolProviderException(AgentFailureCategory.ToolError,
                "The host tool could not complete its request.");
            return new JsonObject
            {
                ["isError"] = true,
                ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "The host tool could not complete its request." }),
            };
        }
        finally
        {
            _callGate.Release();
        }
    }

    private static async Task WriteResponseAsync(Stream stream, int status, JsonObject? body, CancellationToken token)
    {
        byte[] content = body is null ? [] : Encoding.UTF8.GetBytes(body.ToJsonString());
        string reason = status switch { 200 => "OK", 202 => "Accepted", 401 => "Unauthorized", 404 => "Not Found", 413 => "Payload Too Large", _ => "Bad Request" };
        byte[] header = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} {reason}\r\nContent-Type: application/json\r\nContent-Length: {content.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(header, token);
        if (content.Length > 0)
            await stream.WriteAsync(content, token);
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();
        if (_loop is not null)
            await _loop;
        Task[] clients;
        lock (_clients)
            clients = [.. _clients];
        await Task.WhenAll(clients);
        _clientSlots.Dispose();
        _callGate.Dispose();
        _stop.Dispose();
    }
}
