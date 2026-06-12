using ImGuiNET;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using XREngine.Components;
using XREngine.Data.Core;
using XREngine.Editor.Mcp;
using XREngine.Rendering;
using XREngine.Rendering.OpenGL;
using XREngine.Rendering.Vulkan;
using XREngine.Scene;
using XREngine.Rendering.UI;


namespace XREngine.Editor.UI.Tools;

public sealed partial class McpAssistantWindow
{
    // ── OpenAI Realtime WebSocket — Already Streams ──────────────────────

    private async Task StreamOpenAiRealtimeAsync(string prompt, ChatMessage target, CancellationToken ct, McpUsageEntry usage)
    {
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", $"Bearer {OpenAiApiKey.Trim()}");
        socket.Options.SetRequestHeader("OpenAI-Beta", "realtime=v1");

        string model = string.IsNullOrWhiteSpace(OpenAiRealtimeModel)
            ? "gpt-4o-realtime-preview"
            : OpenAiRealtimeModel.Trim();

        var uri = new Uri($"wss://api.openai.com/v1/realtime?model={Uri.EscapeDataString(model)}");
        await socket.ConnectAsync(uri, ct);

        await SendWsJsonAsync(socket, BuildRealtimeSessionUpdate(), ct);
        await SendWsJsonAsync(socket, BuildRealtimeUserMessage(prompt), ct);
        await SendWsJsonAsync(socket, BuildRealtimeResponseCreate(), ct);

        var sb = new StringBuilder();
        var toolLog = new StringBuilder();
        var pendingCallsByOutputIndex = new Dictionary<int, PendingFunctionCall>();
        var pendingCallsByItemId = new Dictionary<string, PendingFunctionCall>(StringComparer.Ordinal);

        while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            string? json = await ReceiveWsJsonAsync(socket, ct);
            if (string.IsNullOrWhiteSpace(json))
                continue;

            using var doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            if (!root.TryGetProperty("type", out var typeEl))
                continue;

            string eventType = typeEl.GetString() ?? string.Empty;

            if (string.Equals(eventType, "response.output_text.delta", StringComparison.OrdinalIgnoreCase)
                && root.TryGetProperty("delta", out var deltaEl))
            {
                sb.Append(deltaEl.GetString());
                target.Content = BuildAssistantContent(sb, toolLog);
                SyncTextSegment(target, sb);
            }
            else if (string.Equals(eventType, "response.output_item.added", StringComparison.OrdinalIgnoreCase)
                && root.TryGetProperty("output_index", out var outputIndexEl)
                && outputIndexEl.TryGetInt32(out int outputIndex)
                && root.TryGetProperty("item", out var itemEl)
                && itemEl.TryGetProperty("type", out var itemTypeEl)
                && string.Equals(itemTypeEl.GetString(), "function_call", StringComparison.OrdinalIgnoreCase))
            {
                string itemId = itemEl.TryGetProperty("id", out var itemIdEl) ? itemIdEl.GetString() ?? string.Empty : string.Empty;
                string callId = itemEl.TryGetProperty("call_id", out var callIdEl) ? callIdEl.GetString() ?? string.Empty : string.Empty;
                string name = itemEl.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? string.Empty : string.Empty;

                var pending = new PendingFunctionCall
                {
                    CallId = callId,
                    Name = name,
                };

                pendingCallsByOutputIndex[outputIndex] = pending;
                if (!string.IsNullOrWhiteSpace(itemId))
                    pendingCallsByItemId[itemId] = pending;

                target.Content = BuildAssistantContent(sb, toolLog, $"Calling tool: {name}...");
                usage.ToolEventCount++;
            }
            else if (string.Equals(eventType, "response.function_call_arguments.delta", StringComparison.OrdinalIgnoreCase)
                && root.TryGetProperty("output_index", out var outputIndexDeltaEl)
                && outputIndexDeltaEl.TryGetInt32(out int outputIndexDelta)
                && root.TryGetProperty("delta", out var argDeltaEl)
                && argDeltaEl.ValueKind == JsonValueKind.String
                && pendingCallsByOutputIndex.TryGetValue(outputIndexDelta, out var pendingDeltaCall))
            {
                pendingDeltaCall.Arguments.Append(argDeltaEl.GetString());
            }
            else if (string.Equals(eventType, "response.function_call_arguments.done", StringComparison.OrdinalIgnoreCase)
                && root.TryGetProperty("output_index", out var outputIndexDoneEl)
                && outputIndexDoneEl.TryGetInt32(out int outputIndexDone)
                && root.TryGetProperty("arguments", out var argsDoneEl)
                && argsDoneEl.ValueKind == JsonValueKind.String
                && pendingCallsByOutputIndex.TryGetValue(outputIndexDone, out var pendingDoneCall))
            {
                pendingDoneCall.Arguments.Clear();
                pendingDoneCall.Arguments.Append(argsDoneEl.GetString());
            }
            else if (string.Equals(eventType, "response.output_item.done", StringComparison.OrdinalIgnoreCase)
                && root.TryGetProperty("item", out var doneItemEl)
                && doneItemEl.TryGetProperty("type", out var doneItemTypeEl)
                && string.Equals(doneItemTypeEl.GetString(), "function_call", StringComparison.OrdinalIgnoreCase))
            {
                string? doneItemId = doneItemEl.TryGetProperty("id", out var doneItemIdEl) ? doneItemIdEl.GetString() : null;
                PendingFunctionCall? pendingDone = null;

                if (!string.IsNullOrWhiteSpace(doneItemId)
                    && pendingCallsByItemId.TryGetValue(doneItemId!, out var callByItem))
                {
                    pendingDone = callByItem;
                    pendingCallsByItemId.Remove(doneItemId!);
                }

                if (pendingDone is null
                    && root.TryGetProperty("output_index", out var outputIndexDoneItemEl)
                    && outputIndexDoneItemEl.TryGetInt32(out int outputIndexDoneItem)
                    && pendingCallsByOutputIndex.TryGetValue(outputIndexDoneItem, out var callByOutputIndex))
                {
                    pendingDone = callByOutputIndex;
                    pendingCallsByOutputIndex.Remove(outputIndexDoneItem);
                }

                if (pendingDone is null)
                    continue;

                if (doneItemEl.TryGetProperty("arguments", out var finalArgsEl) && finalArgsEl.ValueKind == JsonValueKind.String)
                {
                    pendingDone.Arguments.Clear();
                    pendingDone.Arguments.Append(finalArgsEl.GetString());
                }

                var tcEntry = new ToolCallEntry
                {
                    ToolName = FormatToolName(pendingDone.Name),
                    ArgsSummary = SummarizeToolArguments(pendingDone.Name, pendingDone.Arguments.ToString()),
                };
                AddToolCallSegmented(target, tcEntry);

                var toolResult = await ExecuteRealtimeFunctionCallAsync(pendingDone, ct);
                usage.McpEventCount++;

                tcEntry.ResultSummary = SummarizeToolResult(toolResult.FunctionOutput);
                tcEntry.ContextResultSummary = SummarizeToolResultForContext(toolResult.FunctionOutput);
                tcEntry.IsError = toolResult.IsError;
                tcEntry.IsComplete = true;

                if (toolResult.IsError)
                    Debug.LogWarning($"MCP tool call '{pendingDone.Name}' failed: {toolResult.FunctionOutput}");

                AppendToolCallLog(toolLog, pendingDone.Name, pendingDone.Arguments.ToString(), toolResult.FunctionOutput);
                target.Content = BuildAssistantContent(sb, toolLog, "Sending tool result...");

                await SendWsJsonAsync(socket, BuildRealtimeFunctionCallOutputItem(pendingDone.CallId, toolResult.FunctionOutput), ct);

                if (!string.IsNullOrWhiteSpace(toolResult.ImageDataUrl))
                {
                    await SendWsJsonAsync(socket, BuildRealtimeScreenshotContextMessage(toolResult), ct);
                }

                await SendWsJsonAsync(socket, BuildRealtimeResponseCreate(), ct);
                target.Content = BuildAssistantContent(sb, toolLog, "Continuing model response...");
            }
            else if (string.Equals(eventType, "response.completed", StringComparison.OrdinalIgnoreCase))
            {
                usage.Result = "Done";
                break;
            }
            else if (string.Equals(eventType, "error", StringComparison.OrdinalIgnoreCase))
            {
                usage.Result = "Failed";
                target.Content = BuildAssistantContent(sb, toolLog, json);
                break;
            }
        }

        if (sb.Length > 0)
            target.Content = BuildAssistantContent(sb, toolLog);

        if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
    }

    // ── WebSocket Helpers ────────────────────────────────────────────────

    private static async Task SendWsJsonAsync(ClientWebSocket socket, JsonObject payload, CancellationToken ct)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(payload.ToJsonString());
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
    }

    private static async Task<string?> ReceiveWsJsonAsync(ClientWebSocket socket, CancellationToken ct)
    {
        byte[] buffer = new byte[8192];
        var sb = new StringBuilder();

        while (true)
        {
            WebSocketReceiveResult result = await socket.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close)
                return null;

            sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
            if (result.EndOfMessage)
                break;
        }

        return sb.ToString();
    }

    // ── Realtime Payload Builders ────────────────────────────────────────

    private JsonObject BuildRealtimeSessionUpdate()
    {
        string instructions = BuildSystemInstructions(
            ProviderType.Codex,
            requireToolUse: false,
            attachMcp: AttachMcpServer,
            keepCameraOnWorkingArea: AutoCameraView,
            isRealtimeSession: true);

        return new JsonObject
        {
            ["type"] = "session.update",
            ["session"] = new JsonObject
            {
                ["instructions"] = instructions,
                ["modalities"] = new JsonArray("text"),
                ["tool_choice"] = "auto",
                ["tools"] = BuildRealtimeFunctionTools()
            }
        };
    }

    private static JsonObject BuildRealtimeUserMessage(string prompt) => new()
    {
        ["type"] = "conversation.item.create",
        ["item"] = new JsonObject
        {
            ["type"] = "message",
            ["role"] = "user",
            ["content"] = new JsonArray
            {
                new JsonObject { ["type"] = "input_text", ["text"] = prompt }
            }
        }
    };

    private static JsonObject BuildRealtimeResponseCreate() => new()
    {
        ["type"] = "response.create",
        ["response"] = new JsonObject { ["modalities"] = new JsonArray("text") }
    };

    private static JsonArray BuildRealtimeFunctionTools() =>
    [
        new JsonObject
        {
            ["type"] = "function",
            ["name"] = "request_view_screenshot",
            ["description"] = "Capture a screenshot from the current editor view or a camera and return it for visual reasoning.",
            ["parameters"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["camera_node_id"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Optional camera scene node GUID."
                    },
                    ["camera_name"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Optional camera scene node name when ID is unknown."
                    },
                    ["window_index"] = new JsonObject
                    {
                        ["type"] = "integer",
                        ["description"] = "Optional editor window index. Defaults to 0."
                    },
                    ["viewport_index"] = new JsonObject
                    {
                        ["type"] = "integer",
                        ["description"] = "Optional viewport index within the window. Defaults to 0."
                    },
                    ["note"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Optional reason for the screenshot request."
                    }
                },
                ["additionalProperties"] = false
            }
        }
    ];

    private static JsonObject BuildRealtimeFunctionCallOutputItem(string callId, string output) => new()
    {
        ["type"] = "conversation.item.create",
        ["item"] = new JsonObject
        {
            ["type"] = "function_call_output",
            ["call_id"] = callId,
            ["output"] = output
        }
    };

    private static JsonObject BuildRealtimeScreenshotContextMessage(RealtimeFunctionResult result)
    {
        string text = string.IsNullOrWhiteSpace(result.ContextText)
            ? "Requested screenshot attached."
            : result.ContextText;

        return new JsonObject
        {
            ["type"] = "conversation.item.create",
            ["item"] = new JsonObject
            {
                ["type"] = "message",
                ["role"] = "user",
                ["content"] = new JsonArray
                {
                    new JsonObject { ["type"] = "input_text", ["text"] = text },
                    new JsonObject { ["type"] = "input_image", ["image_url"] = result.ImageDataUrl }
                }
            }
        };
    }

    private async Task<RealtimeFunctionResult> ExecuteRealtimeFunctionCallAsync(PendingFunctionCall call, CancellationToken ct)
    {
        if (!string.Equals(call.Name, "request_view_screenshot", StringComparison.Ordinal))
        {
            return new RealtimeFunctionResult
            {
                IsError = true,
                FunctionOutput = JsonSerializer.Serialize(new
                {
                    ok = false,
                    error = $"Unsupported realtime function '{call.Name}'."
                })
            };
        }

        try
        {
            JsonElement args = default;
            bool hasArgs = false;
            string argsRaw = call.Arguments.ToString();
            if (!string.IsNullOrWhiteSpace(argsRaw))
            {
                using var argsDoc = JsonDocument.Parse(argsRaw);
                args = argsDoc.RootElement.Clone();
                hasArgs = true;
            }

            string? cameraNodeId = hasArgs && args.TryGetProperty("camera_node_id", out var cameraNodeIdEl) && cameraNodeIdEl.ValueKind == JsonValueKind.String
                ? cameraNodeIdEl.GetString()
                : null;

            string? cameraName = hasArgs && args.TryGetProperty("camera_name", out var cameraNameEl) && cameraNameEl.ValueKind == JsonValueKind.String
                ? cameraNameEl.GetString()
                : null;

            int windowIndex = hasArgs && args.TryGetProperty("window_index", out var windowIndexEl) && windowIndexEl.TryGetInt32(out int parsedWindow)
                ? parsedWindow
                : 0;

            int viewportIndex = hasArgs && args.TryGetProperty("viewport_index", out var viewportIndexEl) && viewportIndexEl.TryGetInt32(out int parsedViewport)
                ? parsedViewport
                : 0;

            string note = hasArgs && args.TryGetProperty("note", out var noteEl) && noteEl.ValueKind == JsonValueKind.String
                ? noteEl.GetString() ?? string.Empty
                : string.Empty;

            var capture = await CaptureRealtimeScreenshotAsync(cameraNodeId, cameraName, windowIndex, viewportIndex, ct);
            byte[] bytes = await File.ReadAllBytesAsync(capture.Path, ct);
            string dataUrl = "data:image/png;base64," + Convert.ToBase64String(bytes);

            string functionOutput = JsonSerializer.Serialize(new
            {
                ok = true,
                path = capture.Path,
                camera_node_id = capture.CameraNodeId,
                camera_name = capture.CameraName,
                window_index = capture.WindowIndex,
                viewport_index = capture.ViewportIndex,
                note
            });

            string contextText = string.IsNullOrWhiteSpace(capture.CameraName)
                ? "Requested screenshot from the current viewport."
                : $"Requested screenshot from camera '{capture.CameraName}'.";

            return new RealtimeFunctionResult
            {
                FunctionOutput = functionOutput,
                ImageDataUrl = dataUrl,
                ContextText = contextText,
            };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new RealtimeFunctionResult
            {
                IsError = true,
                FunctionOutput = JsonSerializer.Serialize(new
                {
                    ok = false,
                    error = "Screenshot request timed out."
                })
            };
        }
        catch (Exception ex)
        {
            return new RealtimeFunctionResult
            {
                IsError = true,
                FunctionOutput = JsonSerializer.Serialize(new
                {
                    ok = false,
                    error = ex.Message
                })
            };
        }
    }

    /// <summary>
    /// Captures the primary editor viewport and returns the image as a base64-encoded PNG string.
    /// Returns <c>null</c> if no viewport is available or the capture fails.
    /// </summary>
    private static async Task<string?> CaptureViewportScreenshotBase64Async(CancellationToken ct)
    {
        var viewport = Engine.Windows.FirstOrDefault()?.Viewports.FirstOrDefault();
        if (viewport is null)
            return null;

        var window = viewport.Window ?? Engine.Windows.FirstOrDefault(w => w.Viewports.Contains(viewport));
        if (window is null)
            return null;

        var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        Action? deferredHandler = null;

        static void BeginCapture(AbstractRenderer renderer, XRViewport viewport, TaskCompletionSource<byte[]> tcs)
        {
            renderer.GetScreenshotAsync(viewport.Region, false, (img, _) =>
            {
                if (img is null)
                {
                    tcs.TrySetException(new InvalidOperationException("Screenshot capture returned null."));
                    return;
                }

                try
                {
                    if (renderer.ScreenshotRequiresVerticalFlip)
                        img.Flip();
                    tcs.TrySetResult(img.ToByteArray(ImageMagick.MagickFormat.Png));
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            });
        }

        void ScheduleCaptureOnRenderThread()
        {
            if (AbstractRenderer.Current is not null)
            {
                BeginCapture(AbstractRenderer.Current, viewport, tcs);
                return;
            }

            int captureStarted = 0;
            deferredHandler = () =>
            {
                var renderer = AbstractRenderer.Current;
                if (renderer is null)
                    return;

                if (Interlocked.CompareExchange(ref captureStarted, 1, 0) != 0)
                    return;

                window.RenderViewportsCallback -= deferredHandler;
                BeginCapture(renderer, viewport, tcs);
            };

            window.RenderViewportsCallback += deferredHandler;
        }

        if (Engine.IsRenderThread)
        {
            ScheduleCaptureOnRenderThread();
        }
        else
        {
            Engine.InvokeOnMainThread(ScheduleCaptureOnRenderThread, "MCP Assistant: Capture viewport screenshot", executeNowIfAlreadyMainThread: true);
        }

        using var reg = ct.Register(() =>
        {
            if (deferredHandler is not null)
            {
                var cancelWindow = viewport.Window ?? Engine.Windows.FirstOrDefault(w => w.Viewports.Contains(viewport));
                cancelWindow?.RenderViewportsCallback -= deferredHandler;
            }

            tcs.TrySetCanceled(ct);
        });

        try
        {
            byte[] bytes = await tcs.Task;
            return Convert.ToBase64String(bytes);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<RealtimeScreenshotCapture> CaptureRealtimeScreenshotAsync(string? cameraNodeId, string? cameraName, int windowIndex, int viewportIndex, CancellationToken ct)
    {
        XRWorldInstance? world = McpWorldResolver.TryGetActiveWorldInstance();
        XRViewport? viewport = ResolveRealtimeViewport(world, cameraNodeId, cameraName, windowIndex, viewportIndex, out SceneNode? cameraNode);
        if (viewport is null)
            throw new InvalidOperationException("No viewport found to capture.");

        string folder = Path.Combine(Environment.CurrentDirectory, "McpCaptures");
        string fileName = $"RealtimeScreenshot_{DateTime.Now:yyyyMMdd_HHmmss_fff}.png";
        string path = Path.Combine(folder, fileName);

        Directory.CreateDirectory(folder);

        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        Action? deferredHandler = null;

        var window = viewport.Window ?? Engine.Windows.FirstOrDefault(w => w.Viewports.Contains(viewport));
        if (window is null)
            throw new InvalidOperationException("No window found to capture from.");

        static void BeginCapture(AbstractRenderer renderer, XRViewport viewport, string path, TaskCompletionSource<string> tcs)
        {
            renderer.GetScreenshotAsync(viewport.Region, false, (img, _) =>
            {
                if (img is null)
                {
                    tcs.TrySetException(new InvalidOperationException("Screenshot capture returned null."));
                    return;
                }

                try
                {
                    if (renderer.ScreenshotRequiresVerticalFlip)
                        img.Flip();
                    img.Write(path);
                    tcs.TrySetResult(path);
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            });
        }

        void ScheduleCaptureOnRenderThread()
        {
            if (AbstractRenderer.Current is not null)
            {
                BeginCapture(AbstractRenderer.Current, viewport, path, tcs);
                return;
            }

            int captureStarted = 0;
            deferredHandler = () =>
            {
                var renderer = AbstractRenderer.Current;
                if (renderer is null)
                    return;

                if (Interlocked.CompareExchange(ref captureStarted, 1, 0) != 0)
                    return;

                window.RenderViewportsCallback -= deferredHandler;
                BeginCapture(renderer, viewport, path, tcs);
            };

            window.RenderViewportsCallback += deferredHandler;
        }

        if (Engine.IsRenderThread)
        {
            ScheduleCaptureOnRenderThread();
        }
        else
        {
            Engine.InvokeOnMainThread(ScheduleCaptureOnRenderThread, "Realtime: Capture screenshot", executeNowIfAlreadyMainThread: true);
        }

        using var reg = ct.Register(() =>
        {
            if (deferredHandler is not null)
            {
                var cancelWindow = viewport.Window ?? Engine.Windows.FirstOrDefault(w => w.Viewports.Contains(viewport));
                cancelWindow?.RenderViewportsCallback -= deferredHandler;
            }

            tcs.TrySetCanceled(ct);
        });

        string savedPath = await tcs.Task;
        return new RealtimeScreenshotCapture
        {
            Path = savedPath,
            CameraNodeId = cameraNode?.ID.ToString(),
            CameraName = cameraNode?.Name,
            WindowIndex = ResolveWindowIndex(window),
            ViewportIndex = window.Viewports.IndexOf(viewport)
        };
    }

    private static int ResolveWindowIndex(XRWindow window)
    {
        int index = 0;
        foreach (var entry in Engine.Windows)
        {
            if (ReferenceEquals(entry, window))
                return index;
            index++;
        }

        return -1;
    }

    private static XRViewport? ResolveRealtimeViewport(
        XRWorldInstance? world,
        string? cameraNodeId,
        string? cameraName,
        int windowIndex,
        int viewportIndex,
        out SceneNode? cameraNode)
    {
        cameraNode = null;

        CameraComponent? camera = null;
        if (world is not null)
            camera = ResolveCameraComponent(world, cameraNodeId, cameraName, out cameraNode);

        if (camera is not null)
        {
            foreach (var activeViewport in Engine.EnumerateActiveViewports())
            {
                if (ReferenceEquals(activeViewport.CameraComponent, camera))
                    return activeViewport;
            }
        }

        if (windowIndex < 0 || windowIndex >= Engine.Windows.Count)
            return Engine.Windows.FirstOrDefault()?.Viewports.FirstOrDefault();

        var window = Engine.Windows[windowIndex];
        if (window.Viewports.Count == 0)
            return null;

        if (viewportIndex < 0 || viewportIndex >= window.Viewports.Count)
            return window.Viewports.FirstOrDefault();

        return window.Viewports[viewportIndex];
    }

    private static CameraComponent? ResolveCameraComponent(XRWorldInstance world, string? cameraNodeId, string? cameraName, out SceneNode? cameraNode)
    {
        cameraNode = null;

        if (!string.IsNullOrWhiteSpace(cameraNodeId)
            && Guid.TryParse(cameraNodeId, out var guid)
            && XRObjectBase.ObjectsCache.TryGetValue(guid, out var obj)
            && obj is SceneNode idNode
            && idNode.World == world)
        {
            var camera = idNode.GetComponent<CameraComponent>();
            if (camera is not null)
            {
                cameraNode = idNode;
                return camera;
            }
        }

        if (!string.IsNullOrWhiteSpace(cameraName))
        {
            foreach (var node in EnumerateWorldNodes(world))
            {
                if (!string.Equals(node.Name, cameraName, StringComparison.OrdinalIgnoreCase))
                    continue;

                var camera = node.GetComponent<CameraComponent>();
                if (camera is not null)
                {
                    cameraNode = node;
                    return camera;
                }
            }
        }

        return null;
    }

    private static IEnumerable<SceneNode> EnumerateWorldNodes(XRWorldInstance world)
    {
        foreach (var root in world.RootNodes)
        {
            if (root is null)
                continue;

            foreach (var node in EnumerateNodeAndDescendants(root))
                yield return node;
        }
    }

    private static IEnumerable<SceneNode> EnumerateNodeAndDescendants(SceneNode root)
    {
        yield return root;

        foreach (var childTransform in root.Transform.Children)
        {
            var childNode = childTransform.SceneNode;
            if (childNode is null)
                continue;

            foreach (var child in EnumerateNodeAndDescendants(childNode))
                yield return child;
        }
    }

    /// <summary>
    /// Builds the system / instructions prompt shared by every provider.
    /// Provider-specific wording (e.g. Anthropic's tool_use block format) is
    /// appended only when <paramref name="provider"/> indicates it is relevant.
}
