using System;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using XREngine;

namespace XREngine.Editor.Mcp
{
    public sealed class McpServerHost : IDisposable
    {
        private readonly HttpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly JsonSerializerOptions _serializerOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        private Task? _listenerTask;

        private McpServerHost(string prefix)
        {
            _listener = new HttpListener();
            _listener.Prefixes.Add(prefix);
            Prefix = prefix;
        }

        public string Prefix { get; }

        public static McpServerHost? TryStartFromArgs(string[] args)
        {
            bool enabled = args.Any(arg => string.Equals(arg, "--mcp", StringComparison.OrdinalIgnoreCase) ||
                                           string.Equals(arg, "--mcp-server", StringComparison.OrdinalIgnoreCase));
            string? envEnabled = Environment.GetEnvironmentVariable("XRE_MCP_ENABLE");
            if (!enabled && !string.Equals(envEnabled, "1", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(envEnabled, "true", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            int port = 5467;
            if (TryReadPort(args, out var parsedPort))
                port = parsedPort;
            else if (int.TryParse(Environment.GetEnvironmentVariable("XRE_MCP_PORT"), out var envPort))
                port = envPort;

            string prefix = $"http://localhost:{port}/mcp/";
            var host = new McpServerHost(prefix);
            host.Start();
            return host;
        }

        public void Start()
        {
            _listener.Start();
            _listenerTask = Task.Run(() => ListenLoopAsync(_cts.Token));
            Debug.Out($"[MCP] Listening on {Prefix}");
        }

        public void Dispose()
        {
            _cts.Cancel();
            try
            {
                _listener.Stop();
                _listener.Close();
            }
            catch
            {
                // ignored
            }
        }

        private async Task ListenLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                HttpListenerContext? context = null;
                try
                {
                    context = await _listener.GetContextAsync();
                }
                catch (HttpListenerException ex) when (ex.ErrorCode == 995 || ex.ErrorCode == 64)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }

                if (context is null)
                    continue;

                _ = Task.Run(() => HandleContextAsync(context, token), token);
            }
        }

        private async Task HandleContextAsync(HttpListenerContext context, CancellationToken token)
        {
            var response = context.Response;
            response.ContentType = "application/json";
            response.Headers.Add("Access-Control-Allow-Origin", "*");

            if (!string.Equals(context.Request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
            {
                response.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
                response.Close();
                return;
            }

            JsonDocument? document = null;
            try
            {
                document = await JsonDocument.ParseAsync(context.Request.InputStream, cancellationToken: token);
            }
            catch
            {
                response.StatusCode = (int)HttpStatusCode.BadRequest;
                response.Close();
                return;
            }

            if (document is null)
            {
                response.StatusCode = (int)HttpStatusCode.BadRequest;
                response.Close();
                return;
            }

            var result = await HandleRpcAsync(document.RootElement, token);
            if (result is null)
            {
                response.StatusCode = (int)HttpStatusCode.NoContent;
                response.Close();
                return;
            }

            string payload = JsonSerializer.Serialize(result, _serializerOptions);
            byte[] bytes = Encoding.UTF8.GetBytes(payload);
            response.ContentLength64 = bytes.Length;
            await response.OutputStream.WriteAsync(bytes, 0, bytes.Length, token);
            response.Close();
        }

        private async Task<object?> HandleRpcAsync(JsonElement root, CancellationToken token)
        {
            if (!root.TryGetProperty("method", out var methodElement))
                return CreateError(root, -32600, "Missing method.");

            string? method = methodElement.GetString();
            if (string.IsNullOrWhiteSpace(method))
                return CreateError(root, -32600, "Invalid method.");

            bool hasId = root.TryGetProperty("id", out var idElement);

            object? result = method switch
            {
                "initialize" => BuildInitializeResult(),
                "tools/list" => BuildToolsListResult(),
                "tools/call" => await HandleToolCallAsync(root, token),
                "ping" => new { },
                _ => CreateError(root, -32601, $"Unknown method '{method}'.")
            };

            if (!hasId)
                return null;

            if (result is McpError error)
                return new { jsonrpc = "2.0", id = idElement, error = new { code = error.Code, message = error.Message } };

            return new { jsonrpc = "2.0", id = idElement, result };
        }

        private object BuildInitializeResult()
        {
            return new
            {
                protocolVersion = "2024-11-05",
                serverInfo = new { name = "XREngine.Editor", version = "0.1.0" },
                capabilities = new
                {
                    tools = new { listChanged = false }
                }
            };
        }

        private object BuildToolsListResult()
        {
            var tools = McpToolRegistry.Tools.Select(tool => new
            {
                name = tool.Name,
                description = tool.Description,
                inputSchema = tool.InputSchema
            });

            return new { tools };
        }

        private async Task<object> HandleToolCallAsync(JsonElement root, CancellationToken token)
        {
            if (!root.TryGetProperty("params", out var paramsElement))
                return new McpError(-32602, "Missing params.");

            if (!paramsElement.TryGetProperty("name", out var nameElement) || nameElement.ValueKind != JsonValueKind.String)
                return new McpError(-32602, "Missing tool name.");

            string name = nameElement.GetString() ?? string.Empty;
            if (!McpToolRegistry.TryGetTool(name, out var tool))
                return new McpError(-32601, $"Tool '{name}' not found.");

            var world = McpWorldResolver.TryGetActiveWorldInstance();
            if (world is null)
                return new McpError(-32000, "No active world instance.");

            JsonElement argsElement = paramsElement.TryGetProperty("arguments", out var arguments)
                ? arguments
                : JsonDocument.Parse("{}").RootElement;

            var context = new McpToolContext(world);
            var response = await tool!.Handler(context, argsElement, token);

            return new
            {
                content = new[]
                {
                    new { type = "text", text = response.Message }
                },
                isError = response.IsError,
                data = response.Data
            };
        }

        private static object CreateError(JsonElement root, int code, string message)
        {
            return new McpError(code, message);
        }

        private static bool TryReadPort(string[] args, out int port)
        {
            port = 0;
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (!string.Equals(args[i], "--mcp-port", StringComparison.OrdinalIgnoreCase))
                    continue;

                return int.TryParse(args[i + 1], out port);
            }
            return false;
        }

        private readonly record struct McpError(int Code, string Message);
    }
}
