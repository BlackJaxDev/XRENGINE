using System;
using System.ComponentModel;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using XREngine;
using XREngine.Data.Core;

namespace XREngine.Editor.Mcp
{
    public sealed class McpServerHost : IDisposable
    {
        private static McpServerHost? _instance;
        private static readonly object _lock = new();

        private HttpListener? _listener;
        private CancellationTokenSource? _cts;
        private readonly JsonSerializerOptions _serializerOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        private Task? _listenerTask;
        private int _port;

        private McpServerHost()
        {
        }

        public string? Prefix { get; private set; }
        public bool IsRunning => _listener?.IsListening ?? false;

        /// <summary>
        /// Gets the singleton MCP server instance.
        /// </summary>
        public static McpServerHost Instance
        {
            get
            {
                if (_instance is null)
                {
                    lock (_lock)
                    {
                        _instance ??= new McpServerHost();
                    }
                }
                return _instance;
            }
        }

        /// <summary>
        /// Initialize the MCP server and subscribe to preference changes.
        /// Call this once during editor startup.
        /// </summary>
        public static void Initialize(string[] args)
        {
            // Check for command-line override to force enable
            bool cliEnabled = args.Any(arg => string.Equals(arg, "--mcp", StringComparison.OrdinalIgnoreCase) ||
                                              string.Equals(arg, "--mcp-server", StringComparison.OrdinalIgnoreCase));
            int? cliPort = TryReadPort(args, out var parsedPort) ? parsedPort : null;

            // Subscribe to preference changes
            Engine.EditorPreferences.PropertyChanged += Instance.OnPreferencesChanged;

            // Apply CLI overrides if specified
            if (cliEnabled)
                Engine.EditorPreferences.McpServerEnabled = true;
            if (cliPort.HasValue)
                Engine.EditorPreferences.McpServerPort = cliPort.Value;

            // Start if enabled in preferences (or via CLI)
            Instance.UpdateServerState();
        }

        private void OnPreferencesChanged(object? sender, IXRPropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(EditorPreferences.McpServerEnabled) or nameof(EditorPreferences.McpServerPort))
                UpdateServerState();
        }

        private void UpdateServerState()
        {
            var prefs = Engine.EditorPreferences;
            bool shouldRun = prefs.McpServerEnabled;
            int desiredPort = prefs.McpServerPort;

            if (shouldRun)
            {
                // If already running on correct port, do nothing
                if (IsRunning && _port == desiredPort)
                    return;

                // Stop if running on different port
                if (IsRunning)
                    Stop();

                Start(desiredPort);
            }
            else
            {
                if (IsRunning)
                    Stop();
            }
        }

        public void Start(int port)
        {
            if (IsRunning)
                return;

            _port = port;
            Prefix = $"http://localhost:{port}/mcp/";
            _cts = new CancellationTokenSource();
            _listener = new HttpListener();
            _listener.Prefixes.Add(Prefix);
            _listener.Start();
            _listenerTask = Task.Run(() => ListenLoopAsync(_cts.Token));
            Debug.Out($"[MCP] Server started on {Prefix}");
        }

        public void Stop()
        {
            if (!IsRunning)
                return;

            Debug.Out("[MCP] Server stopping...");
            _cts?.Cancel();
            try
            {
                _listener?.Stop();
                _listener?.Close();
            }
            catch
            {
                // ignored
            }
            _listener = null;
            _cts = null;
            Prefix = null;
            Debug.Out("[MCP] Server stopped.");
        }

        public void Dispose()
        {
            Engine.EditorPreferences.PropertyChanged -= OnPreferencesChanged;
            Stop();
        }

        private async Task ListenLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested && _listener is not null)
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

        /// <summary>
        /// Shuts down the MCP server. Call during editor shutdown.
        /// </summary>
        public static void Shutdown()
        {
            _instance?.Dispose();
            _instance = null;
        }
    }
}
