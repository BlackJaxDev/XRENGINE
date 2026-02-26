using System;
using System.IO;
using System.ComponentModel;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Collections.Generic;
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
        private DateTimeOffset _startedUtc;
        private long _requestCounter;
        private readonly McpRateLimiter _rateLimiter = new();
        private const int DefaultMaxRequestBytes = 1024 * 1024;
        private const int DefaultRequestTimeoutMs = 30000;
        private const int MaxIdempotencyEntries = 512;
        private static readonly string[] s_supportedMethods =
        [
            "initialize",
            "tools/list",
            "tools/call",
            "resources/list",
            "resources/read",
            "prompts/list",
            "prompts/get",
            "ping"
        ];
        private static readonly object s_idempotencyLock = new();
        private static readonly Dictionary<string, object> s_idempotencyResults = new(StringComparer.Ordinal);
        private static readonly Dictionary<string, string> s_toolAliases = new(StringComparer.OrdinalIgnoreCase)
        {
            ["get_scene_hierarchy"] = "list_scene_nodes",
            ["select_scene_node"] = "select_node_by_name",
            ["delete_selected"] = "delete_selected_nodes"
        };
        private static readonly HashSet<string> s_readToolNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "get_selection",
            "get_engine_state",
            "get_time_state",
            "get_job_manager_state",
            "get_undo_history",
            "list_tools"
        };

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
            if (e.PropertyName is nameof(EditorPreferences.McpServerEnabled)
                or nameof(EditorPreferences.McpServerPort)
                or nameof(EditorPreferences.McpServerRequireAuth)
                or nameof(EditorPreferences.McpServerAuthToken)
                or nameof(EditorPreferences.McpServerCorsAllowlist)
                or nameof(EditorPreferences.McpServerMaxRequestBytes)
                or nameof(EditorPreferences.McpServerRequestTimeoutMs)
                or nameof(EditorPreferences.McpServerReadOnly)
                or nameof(EditorPreferences.McpServerAllowedTools)
                or nameof(EditorPreferences.McpServerDeniedTools)
                or nameof(EditorPreferences.McpServerRateLimitEnabled)
                or nameof(EditorPreferences.McpServerRateLimitRequests)
                or nameof(EditorPreferences.McpServerRateLimitWindowSeconds)
                or nameof(EditorPreferences.McpServerIncludeStatusInPing))
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
            _startedUtc = DateTimeOffset.UtcNow;
            _listenerTask = Task.Run(() => ListenLoopAsync(_cts.Token));
            Debug.Out($"[MCP] Server started on {Prefix}");
            LogStartupDiagnostics(Engine.EditorPreferences);
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
            var prefs = Engine.EditorPreferences;
            long requestId = Interlocked.Increment(ref _requestCounter);
            string clientKey = ResolveClientKey(context.Request);
            var response = context.Response;
            response.ContentType = "application/json";

            LogRequest(requestId, "Incoming", context.Request, null, null);

            if (!ApplyCorsHeaders(context.Request, response, prefs))
            {
                response.StatusCode = (int)HttpStatusCode.Forbidden;
                LogRequest(requestId, "BlockedByCors", context.Request, response.StatusCode, null);
                response.Close();
                return;
            }

            if (string.Equals(context.Request.HttpMethod, "GET", StringComparison.OrdinalIgnoreCase)
                && string.Equals(context.Request.Url?.AbsolutePath, "/mcp/status", StringComparison.OrdinalIgnoreCase))
            {
                response.StatusCode = (int)HttpStatusCode.OK;
                await WriteJsonResponseAsync(response, BuildStatusResult(prefs), token);
                LogRequest(requestId, "Status", context.Request, response.StatusCode, null);
                return;
            }

            if (string.Equals(context.Request.HttpMethod, "OPTIONS", StringComparison.OrdinalIgnoreCase))
            {
                response.StatusCode = (int)HttpStatusCode.NoContent;
                LogRequest(requestId, "Preflight", context.Request, response.StatusCode, null);
                response.Close();
                return;
            }

            if (!string.Equals(context.Request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
            {
                response.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
                LogRequest(requestId, "MethodNotAllowed", context.Request, response.StatusCode, null);
                response.Close();
                return;
            }

            if (!IsAuthorized(context.Request, prefs, out var authError))
            {
                response.StatusCode = (int)HttpStatusCode.Unauthorized;
                response.Headers.Add("WWW-Authenticate", "Bearer");
                await WriteErrorResponseAsync(response, null, -32600, authError ?? "Unauthorized.", token);
                LogRequest(requestId, "Unauthorized", context.Request, response.StatusCode, authError);
                return;
            }

            if (prefs.McpServerRateLimitEnabled)
            {
                int maxRequests = Math.Max(1, prefs.McpServerRateLimitRequests);
                int windowSeconds = Math.Max(1, prefs.McpServerRateLimitWindowSeconds);
                if (!_rateLimiter.TryAcquire(clientKey, maxRequests, TimeSpan.FromSeconds(windowSeconds), DateTimeOffset.UtcNow, out var retryAfter))
                {
                    response.StatusCode = (int)HttpStatusCode.TooManyRequests;
                    response.Headers["Retry-After"] = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds)).ToString();
                    await WriteErrorResponseAsync(response, null, -32029, "Rate limit exceeded.", token);
                    LogRequest(requestId, "RateLimited", context.Request, response.StatusCode, $"retry_after={response.Headers["Retry-After"]}s");
                    return;
                }
            }

            int maxRequestBytes = Math.Max(1024, prefs.McpServerMaxRequestBytes <= 0 ? DefaultMaxRequestBytes : prefs.McpServerMaxRequestBytes);
            if (context.Request.ContentLength64 > maxRequestBytes)
            {
                response.StatusCode = (int)HttpStatusCode.RequestEntityTooLarge;
                LogRequest(requestId, "PayloadTooLarge", context.Request, response.StatusCode, $"content_length={context.Request.ContentLength64}");
                response.Close();
                return;
            }

            int timeoutMs = Math.Max(100, prefs.McpServerRequestTimeoutMs <= 0 ? DefaultRequestTimeoutMs : prefs.McpServerRequestTimeoutMs);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeoutCts.CancelAfter(timeoutMs);
            CancellationToken requestToken = timeoutCts.Token;

            byte[] requestBody;
            try
            {
                requestBody = await ReadRequestBodyAsync(context.Request.InputStream, maxRequestBytes, requestToken);
            }
            catch (InvalidDataException)
            {
                response.StatusCode = (int)HttpStatusCode.RequestEntityTooLarge;
                LogRequest(requestId, "PayloadTooLarge", context.Request, response.StatusCode, "stream_limit_exceeded");
                response.Close();
                return;
            }
            catch (OperationCanceledException)
            {
                response.StatusCode = (int)HttpStatusCode.RequestTimeout;
                LogRequest(requestId, "Timeout", context.Request, response.StatusCode, "read_body_timeout");
                response.Close();
                return;
            }

            JsonDocument? document = null;
            try
            {
                document = JsonDocument.Parse(requestBody);
            }
            catch
            {
                response.StatusCode = (int)HttpStatusCode.BadRequest;
                LogRequest(requestId, "InvalidJson", context.Request, response.StatusCode, null);
                response.Close();
                return;
            }

            if (document is null)
            {
                response.StatusCode = (int)HttpStatusCode.BadRequest;
                LogRequest(requestId, "InvalidJson", context.Request, response.StatusCode, "document_null");
                response.Close();
                return;
            }

            object? result;
            try
            {
                result = await HandleRpcAsync(document.RootElement, requestToken, prefs);
            }
            catch (OperationCanceledException)
            {
                response.StatusCode = (int)HttpStatusCode.RequestTimeout;
                LogRequest(requestId, "Timeout", context.Request, response.StatusCode, "rpc_timeout");
                response.Close();
                return;
            }

            if (result is null)
            {
                response.StatusCode = (int)HttpStatusCode.NoContent;
                LogRequest(requestId, "Notification", context.Request, response.StatusCode, null);
                response.Close();
                return;
            }

            string payload = JsonSerializer.Serialize(result, _serializerOptions);
            byte[] bytes = Encoding.UTF8.GetBytes(payload);
            response.ContentLength64 = bytes.Length;
            await response.OutputStream.WriteAsync(bytes, 0, bytes.Length, requestToken);
            LogRequest(requestId, "Ok", context.Request, (int)HttpStatusCode.OK, $"bytes={bytes.Length}");
            response.Close();
        }

        private async Task<object?> HandleRpcAsync(JsonElement root, CancellationToken token, EditorPreferences prefs)
        {
            if (root.ValueKind != JsonValueKind.Object)
                return CreateError(root, -32600, "Invalid JSON-RPC request object.");

            if (root.TryGetProperty("jsonrpc", out var jsonRpcElement))
            {
                if (jsonRpcElement.ValueKind != JsonValueKind.String || !string.Equals(jsonRpcElement.GetString(), "2.0", StringComparison.Ordinal))
                    return CreateError(root, -32600, "Unsupported JSON-RPC version. Expected '2.0'.");
            }

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
                "resources/list" => BuildResourcesListResult(),
                "resources/read" => await HandleResourcesReadAsync(root, token),
                "prompts/list" => BuildPromptsListResult(),
                "prompts/get" => await HandlePromptsGetAsync(root, token),
                "ping" => BuildPingResult(prefs),
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
                    tools = new { listChanged = false },
                    resources = new { listChanged = false },
                    prompts = new { listChanged = false }
                }
            };
        }

        private object BuildPingResult(EditorPreferences prefs)
        {
            if (!prefs.McpServerIncludeStatusInPing)
                return new { ok = true };

            return new
            {
                ok = true,
                status = BuildStatusResult(prefs)
            };
        }

        private object BuildStatusResult(EditorPreferences prefs)
        {
            string[] allowed = ParseList(prefs.McpServerAllowedTools);
            string[] denied = ParseList(prefs.McpServerDeniedTools);

            return new
            {
                protocolVersion = "2024-11-05",
                serverInfo = new { name = "XREngine.Editor", version = "0.1.0" },
                isRunning = IsRunning,
                startedUtc = _startedUtc,
                uptimeMs = _startedUtc == default ? 0 : (long)(DateTimeOffset.UtcNow - _startedUtc).TotalMilliseconds,
                endpoint = Prefix,
                methods = s_supportedMethods,
                security = new
                {
                    requireAuth = prefs.McpServerRequireAuth,
                    authConfigured = !string.IsNullOrWhiteSpace(prefs.McpServerAuthToken),
                    corsAllowlist = prefs.McpServerCorsAllowlist,
                    readOnly = prefs.McpServerReadOnly,
                    allowedToolsCount = allowed.Length,
                    deniedToolsCount = denied.Length,
                    rateLimit = new
                    {
                        enabled = prefs.McpServerRateLimitEnabled,
                        requests = prefs.McpServerRateLimitRequests,
                        windowSeconds = prefs.McpServerRateLimitWindowSeconds
                    }
                },
                capabilities = new
                {
                    tools = new { listChanged = false },
                    resources = new { listChanged = false },
                    prompts = new { listChanged = false }
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

        private object BuildResourcesListResult()
        {
            var resources = new[]
            {
                new
                {
                    uri = "xrengine://tools",
                    name = "Tool Registry",
                    description = "All available MCP tools and input schemas.",
                    mimeType = "application/json"
                },
                new
                {
                    uri = "xrengine://engine/state",
                    name = "Engine State",
                    description = "Current editor/play mode state snapshot.",
                    mimeType = "application/json"
                },
                new
                {
                    uri = "xrengine://selection",
                    name = "Selection",
                    description = "Current editor scene-node selection.",
                    mimeType = "application/json"
                },
                new
                {
                    uri = "xrengine://world/scenes",
                    name = "World Scenes",
                    description = "Current world and scene listing.",
                    mimeType = "application/json"
                }
            };

            return new { resources };
        }

        private object BuildPromptsListResult()
        {
            var prompts = new[]
            {
                new
                {
                    name = "scene-editing-assistant",
                    description = "Guides an assistant to safely inspect and edit the active scene.",
                    arguments = new[]
                    {
                        new { name = "goal", description = "High-level scene task to perform.", required = false }
                    }
                },
                new
                {
                    name = "world-debug-assistant",
                    description = "Guides an assistant through engine/world diagnostics.",
                    arguments = new[]
                    {
                        new { name = "focus", description = "Specific subsystem to debug.", required = false }
                    }
                }
            };

            return new { prompts };
        }

        private async Task<object> HandleResourcesReadAsync(JsonElement root, CancellationToken token)
        {
            if (!TryGetParamsObject(root, out var paramsElement, out var paramsError))
                return new McpError(-32602, paramsError ?? "Missing params.");

            if (!paramsElement.TryGetProperty("uri", out var uriElement) || uriElement.ValueKind != JsonValueKind.String)
                return new McpError(-32602, "Missing resource URI.");

            string uri = uriElement.GetString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(uri))
                return new McpError(-32602, "Invalid resource URI.");

            object payload = uri switch
            {
                "xrengine://tools" => BuildToolsListResult(),
                "xrengine://engine/state" => BuildEngineStateResource(),
                "xrengine://selection" => BuildSelectionResource(),
                "xrengine://world/scenes" => BuildWorldScenesResource(),
                _ => new McpError(-32602, $"Unknown resource URI '{uri}'.")
            };

            if (payload is McpError error)
                return error;

            string text = JsonSerializer.Serialize(payload, _serializerOptions);
            await Task.CompletedTask;
            return new
            {
                contents = new[]
                {
                    new
                    {
                        uri,
                        mimeType = "application/json",
                        text
                    }
                }
            };
        }

        private async Task<object> HandlePromptsGetAsync(JsonElement root, CancellationToken token)
        {
            if (!TryGetParamsObject(root, out var paramsElement, out var paramsError))
                return new McpError(-32602, paramsError ?? "Missing params.");

            if (!paramsElement.TryGetProperty("name", out var nameElement) || nameElement.ValueKind != JsonValueKind.String)
                return new McpError(-32602, "Missing prompt name.");

            string name = nameElement.GetString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(name))
                return new McpError(-32602, "Invalid prompt name.");

            string goal = string.Empty;
            string focus = string.Empty;
            if (paramsElement.TryGetProperty("arguments", out var argumentsElement) && argumentsElement.ValueKind == JsonValueKind.Object)
            {
                if (argumentsElement.TryGetProperty("goal", out var goalElement) && goalElement.ValueKind == JsonValueKind.String)
                    goal = goalElement.GetString() ?? string.Empty;
                if (argumentsElement.TryGetProperty("focus", out var focusElement) && focusElement.ValueKind == JsonValueKind.String)
                    focus = focusElement.GetString() ?? string.Empty;
            }

            object? result = name switch
            {
                "scene-editing-assistant" => new
                {
                    description = "Use inspection tools first, then apply minimal scene edits.",
                    messages = new[]
                    {
                        new
                        {
                            role = "user",
                            content = new[]
                            {
                                new
                                {
                                    type = "text",
                                    text = string.IsNullOrWhiteSpace(goal)
                                        ? "Inspect the active scene, summarize findings, and then perform only required edits."
                                        : $"Inspect the active scene and accomplish this goal: {goal}. Use minimal edits and validate results."
                                }
                            }
                        }
                    }
                },
                "world-debug-assistant" => new
                {
                    description = "Collect diagnostics and propose targeted fixes.",
                    messages = new[]
                    {
                        new
                        {
                            role = "user",
                            content = new[]
                            {
                                new
                                {
                                    type = "text",
                                    text = string.IsNullOrWhiteSpace(focus)
                                        ? "Gather engine and world diagnostics, identify likely issues, and propose focused remediation steps."
                                        : $"Debug this subsystem: {focus}. Gather diagnostics first, then propose and apply focused fixes."
                                }
                            }
                        }
                    }
                },
                _ => null
            };

            await Task.CompletedTask;
            return result ?? new McpError(-32602, $"Unknown prompt '{name}'.");
        }

        private async Task<object> HandleToolCallAsync(JsonElement root, CancellationToken token)
        {
            if (!TryGetParamsObject(root, out var paramsElement, out var paramsError))
                return new McpError(-32602, paramsError ?? "Missing params.");

            if (!paramsElement.TryGetProperty("name", out var nameElement) || nameElement.ValueKind != JsonValueKind.String)
                return new McpError(-32602, "Missing tool name.");

            string name = nameElement.GetString() ?? string.Empty;
            name = ResolveToolName(name);

            if (!CanInvokeTool(name, Engine.EditorPreferences, out var policyError))
                return new McpError(-32602, policyError ?? $"Tool '{name}' is disallowed by server policy.");

            if (!McpToolRegistry.TryGetTool(name, out var tool))
                return new McpError(-32601, $"Tool '{name}' not found.");

            var world = McpWorldResolver.TryGetActiveWorldInstance();
            if (world is null)
                return new McpError(-32000, "No active world instance.");

            JsonElement argsElement;
            if (paramsElement.TryGetProperty("arguments", out var arguments))
            {
                if (arguments.ValueKind != JsonValueKind.Object)
                    return new McpError(-32602, "Invalid arguments. Expected an object.");

                argsElement = arguments.Clone();
            }
            else
            {
                argsElement = JsonDocument.Parse("{}").RootElement.Clone();
            }

            string? idempotencyKey = null;
            if (paramsElement.TryGetProperty("idempotency_key", out var idempotencyElement) && idempotencyElement.ValueKind == JsonValueKind.String)
                idempotencyKey = idempotencyElement.GetString();

            if (!string.IsNullOrWhiteSpace(idempotencyKey) && TryGetIdempotentResponse(idempotencyKey!, out var cached))
                return cached!;

            var context = new McpToolContext(world);
            var response = await tool!.Handler(context, argsElement, token);

            var toolResponse = new
            {
                content = new[]
                {
                    new { type = "text", text = response.Message }
                },
                isError = response.IsError,
                data = response.Data
            };

            if (!string.IsNullOrWhiteSpace(idempotencyKey) && !response.IsError)
                StoreIdempotentResponse(idempotencyKey!, toolResponse);

            return toolResponse;
        }

        private static bool TryGetParamsObject(JsonElement root, out JsonElement paramsElement, out string? error)
        {
            error = null;
            if (!root.TryGetProperty("params", out paramsElement))
            {
                error = "Missing params.";
                return false;
            }

            if (paramsElement.ValueKind != JsonValueKind.Object)
            {
                error = "Invalid params. Expected an object.";
                return false;
            }

            return true;
        }

        private static string ResolveToolName(string requestedName)
        {
            if (string.IsNullOrWhiteSpace(requestedName))
                return string.Empty;

            return s_toolAliases.TryGetValue(requestedName, out var alias) ? alias : requestedName;
        }

        private static bool CanInvokeTool(string toolName, EditorPreferences prefs, out string? error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(toolName))
            {
                error = "Tool name cannot be empty.";
                return false;
            }

            var denied = ParseList(prefs.McpServerDeniedTools);
            if (denied.Contains(toolName, StringComparer.OrdinalIgnoreCase))
            {
                error = $"Tool '{toolName}' is in the denied tools list.";
                return false;
            }

            var allowed = ParseList(prefs.McpServerAllowedTools);
            if (allowed.Length > 0 && !allowed.Contains(toolName, StringComparer.OrdinalIgnoreCase))
            {
                error = $"Tool '{toolName}' is not in the allowed tools list.";
                return false;
            }

            if (prefs.McpServerReadOnly && IsMutatingTool(toolName))
            {
                error = $"Tool '{toolName}' is blocked because MCP read-only mode is enabled.";
                return false;
            }

            return true;
        }

        private static bool IsMutatingTool(string toolName)
        {
            if (s_readToolNames.Contains(toolName))
                return false;

            if (toolName.StartsWith("get_", StringComparison.OrdinalIgnoreCase) ||
                toolName.StartsWith("list_", StringComparison.OrdinalIgnoreCase) ||
                toolName.StartsWith("find_", StringComparison.OrdinalIgnoreCase) ||
                toolName.StartsWith("validate_", StringComparison.OrdinalIgnoreCase))
                return false;

            return true;
        }

        private static string[] ParseList(string? value)
        {
            return (value ?? string.Empty)
                .Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .ToArray();
        }

        private static bool TryGetIdempotentResponse(string key, out object? response)
        {
            lock (s_idempotencyLock)
                return s_idempotencyResults.TryGetValue(key, out response);
        }

        private static void StoreIdempotentResponse(string key, object response)
        {
            lock (s_idempotencyLock)
            {
                if (s_idempotencyResults.Count >= MaxIdempotencyEntries)
                    s_idempotencyResults.Clear();

                s_idempotencyResults[key] = response;
            }
        }

        private static object BuildEngineStateResource()
        {
            return new
            {
                isEditor = Engine.IsEditor,
                isPlaying = Engine.IsPlaying,
                playModeState = EditorState.CurrentState.ToString(),
                inEditMode = EditorState.InEditMode,
                inPlayMode = EditorState.InPlayMode,
                isPaused = EditorState.IsPaused,
                worldId = (Guid?)null,
                worldName = (string?)null
            };
        }

        private static object BuildSelectionResource()
        {
            var selection = Selection.SceneNodes
                .Select(node => new
                {
                    id = node.ID,
                    name = node.Name
                })
                .ToArray();

            return new { selection };
        }

        private static object BuildWorldScenesResource()
        {
            var worlds = Array.Empty<object>();

            return new
            {
                worlds
            };
        }

        private static object CreateError(JsonElement root, int code, string message)
        {
            return new McpError(code, message);
        }

        private static bool IsAuthorized(HttpListenerRequest request, EditorPreferences prefs, out string? error)
        {
            error = null;
            if (!prefs.McpServerRequireAuth)
                return true;

            string token = prefs.McpServerAuthToken?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(token))
            {
                error = "MCP authentication is enabled, but no auth token is configured.";
                return false;
            }

            string authHeader = request.Headers["Authorization"] ?? string.Empty;
            const string bearerPrefix = "Bearer ";
            if (!authHeader.StartsWith(bearerPrefix, StringComparison.OrdinalIgnoreCase))
            {
                error = "Missing bearer token.";
                return false;
            }

            string suppliedToken = authHeader[bearerPrefix.Length..].Trim();
            if (!string.Equals(suppliedToken, token, StringComparison.Ordinal))
            {
                error = "Invalid bearer token.";
                return false;
            }

            return true;
        }

        private static bool ApplyCorsHeaders(HttpListenerRequest request, HttpListenerResponse response, EditorPreferences prefs)
        {
            string? requestOrigin = request.Headers["Origin"];
            if (!TryResolveCorsOrigin(requestOrigin, prefs.McpServerCorsAllowlist, out var responseOrigin))
                return false;

            if (!string.IsNullOrWhiteSpace(responseOrigin))
                response.Headers["Access-Control-Allow-Origin"] = responseOrigin;

            response.Headers["Access-Control-Allow-Methods"] = "POST, OPTIONS, GET";
            response.Headers["Access-Control-Allow-Headers"] = "Content-Type, Authorization";
            response.Headers["Access-Control-Max-Age"] = "600";

            if (!string.IsNullOrWhiteSpace(requestOrigin) && !string.Equals(responseOrigin, "*", StringComparison.Ordinal))
                response.Headers["Vary"] = "Origin";

            return true;
        }

        private static bool TryResolveCorsOrigin(string? requestOrigin, string? allowlistRaw, out string responseOrigin)
        {
            responseOrigin = "*";

            var allowlist = (allowlistRaw ?? string.Empty)
                .Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .ToArray();

            if (allowlist.Length == 0 || allowlist.Contains("*", StringComparer.Ordinal))
            {
                responseOrigin = "*";
                return true;
            }

            if (string.IsNullOrWhiteSpace(requestOrigin))
            {
                responseOrigin = string.Empty;
                return true;
            }

            if (allowlist.Any(allowed => string.Equals(allowed, requestOrigin, StringComparison.OrdinalIgnoreCase)))
            {
                responseOrigin = requestOrigin;
                return true;
            }

            return false;
        }

        private static async Task<byte[]> ReadRequestBodyAsync(Stream stream, int maxBytes, CancellationToken token)
        {
            using var ms = new MemoryStream();
            byte[] buffer = new byte[8192];
            int total = 0;

            while (true)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), token);
                if (read <= 0)
                    break;

                total += read;
                if (total > maxBytes)
                    throw new InvalidDataException("Request payload exceeds maximum size.");

                ms.Write(buffer, 0, read);
            }

            return ms.ToArray();
        }

        private async Task WriteJsonResponseAsync(HttpListenerResponse response, object payload, CancellationToken token)
        {
            string json = JsonSerializer.Serialize(payload, _serializerOptions);
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            response.ContentLength64 = bytes.Length;
            await response.OutputStream.WriteAsync(bytes, 0, bytes.Length, token);
            response.Close();
        }

        private async Task WriteErrorResponseAsync(HttpListenerResponse response, JsonElement? id, int code, string message, CancellationToken token)
        {
            var payload = JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = id,
                error = new
                {
                    code,
                    message
                }
            }, _serializerOptions);

            byte[] bytes = Encoding.UTF8.GetBytes(payload);
            response.ContentLength64 = bytes.Length;
            await response.OutputStream.WriteAsync(bytes, 0, bytes.Length, token);
            response.Close();
        }

        private static string ResolveClientKey(HttpListenerRequest request)
        {
            string? forwarded = request.Headers["X-Forwarded-For"];
            if (!string.IsNullOrWhiteSpace(forwarded))
            {
                string first = forwarded.Split(',')[0].Trim();
                if (!string.IsNullOrWhiteSpace(first))
                    return first;
            }

            return request.RemoteEndPoint?.Address.ToString() ?? "unknown";
        }

        private void LogRequest(long requestId, string phase, HttpListenerRequest request, int? statusCode, string? detail)
        {
            string method = request.HttpMethod;
            string path = request.Url?.AbsolutePath ?? string.Empty;
            string origin = request.Headers["Origin"] ?? string.Empty;
            string authHeader = request.Headers["Authorization"] ?? string.Empty;
            string redactedAuth = string.IsNullOrWhiteSpace(authHeader) ? "none" : "<redacted>";
            string client = ResolveClientKey(request);

            Debug.Out($"[MCP] req={requestId} phase={phase} client={client} method={method} path={path} status={statusCode?.ToString() ?? "-"} origin={origin} auth={redactedAuth} {detail}");
        }

        private static void LogStartupDiagnostics(EditorPreferences prefs)
        {
            Debug.Out($"[MCP] methods={string.Join(",", s_supportedMethods)} capabilities=tools/resources/prompts static_list_changed=false");
            Debug.Out($"[MCP] security auth_required={prefs.McpServerRequireAuth} auth_token_configured={!string.IsNullOrWhiteSpace(prefs.McpServerAuthToken)} read_only={prefs.McpServerReadOnly} cors_allowlist='{prefs.McpServerCorsAllowlist}'");
            Debug.Out($"[MCP] policy allowed='{prefs.McpServerAllowedTools}' denied='{prefs.McpServerDeniedTools}' rate_limit_enabled={prefs.McpServerRateLimitEnabled} rate_limit={prefs.McpServerRateLimitRequests}/{prefs.McpServerRateLimitWindowSeconds}s");
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
