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
    // ── Anthropic Messages API — Streaming SSE with Local Tool-Use Loop ─

    private async Task StreamAnthropicAsync(string prompt, ChatMessage target, CancellationToken ct, McpUsageEntry usage)
    {
        JsonArray anthropicTools = ConvertOpenAiFunctionsToAnthropicTools(BuildLocalAssistantFunctionTools());

        // If MCP is enabled, fetch tools from local MCP server and convert to Anthropic format.
        if (AttachMcpServer)
        {
            JsonArray? mcpTools = await FetchMcpToolListAsync(ct);
            if (mcpTools is null || mcpTools.Count == 0)
            {
                usage.McpPayloadIncluded = false;
                usage.McpDiscoveryFailed = true;
                usage.Result = "MCP Fetch Failed";
                target.Content = "MCP is enabled but no tools could be fetched from the local MCP server.";
                return;
            }

            AppendTools(anthropicTools, ConvertMcpToolsToAnthropicTools(mcpTools));
            usage.McpPayloadIncluded = true;
            usage.ToolChoice = "auto";
        }

        bool requestLikelyNeedsTools = IsLikelySceneMutationPrompt(prompt);
        usage.RequireToolUse = requestLikelyNeedsTools;

        string model = string.IsNullOrWhiteSpace(AnthropicModel) ? "claude-sonnet-4-5" : AnthropicModel;
        string instructions = BuildSystemInstructions(ProviderType.ClaudeCode, requestLikelyNeedsTools, attachMcp: AttachMcpServer, keepCameraOnWorkingArea: AutoCameraView);

        // Build conversation messages — starts with user prompt, grows with tool results.
        // If a viewport screenshot was captured, send multimodal content (image + text).
        JsonNode userContent;
        string? screenshotBase64 = Interlocked.Exchange(ref _pendingScreenshotBase64, null);
        if (screenshotBase64 is not null)
        {
            userContent = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "image",
                    ["source"] = new JsonObject
                    {
                        ["type"] = "base64",
                        ["media_type"] = "image/png",
                        ["data"] = screenshotBase64
                    }
                },
                new JsonObject { ["type"] = "text", ["text"] = prompt }
            };
        }
        else
        {
            userContent = JsonValue.Create(prompt)!;
        }
        var messages = new JsonArray
        {
            new JsonObject { ["role"] = "user", ["content"] = userContent }
        };

        const int maxToolRounds = 10;
        var sb = new StringBuilder();
        var toolLog = new StringBuilder(); // Human-readable log of tool calls shown in the response.
        var seenEventTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int sseLineCount = 0;

        for (int round = 0; round < maxToolRounds; round++)
        {
            // Track where this round's text starts in the shared StringBuilder
            // so that SyncTextSegment only writes this round's text to the current segment.
            int sbRoundStart = sb.Length;

            var payload = new JsonObject
            {
                ["model"] = model,
                ["max_tokens"] = MaxTokens,
                ["stream"] = true,
                ["system"] = instructions,
                ["messages"] = JsonNode.Parse(messages.ToJsonString())
            };

            if (anthropicTools.Count > 0)
                payload["tools"] = JsonNode.Parse(anthropicTools.ToJsonString());

            using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages")
            {
                Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
            };
            request.Headers.Add("x-api-key", AnthropicApiKey.Trim());
            request.Headers.Add("anthropic-version", "2023-06-01");

            using HttpResponseMessage response = await SharedHttp.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

            if (!response.IsSuccessStatusCode)
            {
                string errorBody = await response.Content.ReadAsStringAsync(ct);
                usage.Result = $"HTTP {(int)response.StatusCode}";
                target.Content = sb.Length > 0
                    ? sb + $"\n\n--- API Error (round {round + 1}) ---\n{errorBody}"
                    : errorBody;
                return;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream);

            // Track tool_use blocks: index → (id, name, input JSON)
            var pendingToolUse = new List<(string Id, string Name, StringBuilder InputJson)>();
            int currentBlockIndex = -1;
            string? currentToolUseId = null;
            string? currentToolUseName = null;
            StringBuilder? currentToolUseInput = null;
            string? stopReason = null;
            string? line;

            while (!ct.IsCancellationRequested && (line = await reader.ReadLineAsync(ct)) is not null)
            {
                if (!line.StartsWith("data: ", StringComparison.Ordinal))
                    continue;

                string data = line[6..];
                if (data is "[DONE]")
                    break;

                sseLineCount++;

                try
                {
                    using var doc = JsonDocument.Parse(data);
                    JsonElement root = doc.RootElement;

                    if (!root.TryGetProperty("type", out var typeEl))
                    {
                        seenEventTypes.Add("(no-type)");
                        continue;
                    }

                    string eventType = typeEl.GetString() ?? string.Empty;
                    seenEventTypes.Add(eventType);

                    // Text delta
                    if (string.Equals(eventType, "content_block_delta", StringComparison.OrdinalIgnoreCase)
                        && root.TryGetProperty("delta", out var deltaEl))
                    {
                        if (deltaEl.TryGetProperty("type", out var dtEl))
                        {
                            string deltaType = dtEl.GetString() ?? string.Empty;

                            if (string.Equals(deltaType, "text_delta", StringComparison.OrdinalIgnoreCase)
                                && deltaEl.TryGetProperty("text", out var textEl))
                            {
                                string? text = textEl.GetString();
                                if (text is not null)
                                {
                                    sb.Append(text);
                                    target.Content = sb.ToString();
                                    SyncTextSegment(target, sb, sbRoundStart);
                                }
                            }
                            else if (string.Equals(deltaType, "input_json_delta", StringComparison.OrdinalIgnoreCase)
                                && deltaEl.TryGetProperty("partial_json", out var pjEl))
                            {
                                currentToolUseInput?.Append(pjEl.GetString());
                            }
                        }
                        else if (deltaEl.TryGetProperty("text", out var textEl2))
                        {
                            // Fallback: delta without explicit type
                            string? text = textEl2.GetString();
                            if (text is not null)
                            {
                                sb.Append(text);
                                target.Content = sb.ToString();
                                SyncTextSegment(target, sb, sbRoundStart);
                            }
                        }
                    }

                    // Content block start — check if it's a tool_use block
                    if (string.Equals(eventType, "content_block_start", StringComparison.OrdinalIgnoreCase)
                        && root.TryGetProperty("content_block", out var blockEl)
                        && blockEl.TryGetProperty("type", out var blockTypeEl)
                        && string.Equals(blockTypeEl.GetString(), "tool_use", StringComparison.OrdinalIgnoreCase))
                    {
                        currentToolUseId = blockEl.TryGetProperty("id", out var bIdEl) ? bIdEl.GetString() ?? "" : "";
                        currentToolUseName = blockEl.TryGetProperty("name", out var bNameEl) ? bNameEl.GetString() ?? "" : "";
                        currentToolUseInput = new StringBuilder();
                        currentBlockIndex = root.TryGetProperty("index", out var idxEl) ? idxEl.GetInt32() : -1;
                    }

                    // Content block stop — finalize tool_use if applicable
                    if (string.Equals(eventType, "content_block_stop", StringComparison.OrdinalIgnoreCase)
                        && currentToolUseId is not null)
                    {
                        pendingToolUse.Add((currentToolUseId, currentToolUseName ?? "", currentToolUseInput ?? new StringBuilder()));
                        currentToolUseId = null;
                        currentToolUseName = null;
                        currentToolUseInput = null;
                    }

                    // Message delta — check for stop_reason
                    if (string.Equals(eventType, "message_delta", StringComparison.OrdinalIgnoreCase)
                        && root.TryGetProperty("delta", out var msgDelta)
                        && msgDelta.TryGetProperty("stop_reason", out var srEl))
                    {
                        stopReason = srEl.GetString();
                    }

                    if (string.Equals(eventType, "message_stop", StringComparison.OrdinalIgnoreCase))
                        break;

                    if (string.Equals(eventType, "error", StringComparison.OrdinalIgnoreCase))
                    {
                        usage.Result = "Failed";
                        sb.Append($"\n\n--- SSE Error ---\n{data}");
                        target.Content = sb.ToString();
                        return;
                    }
                }
                catch (JsonException)
                {
                    // Malformed SSE line — skip.
                }
            }

            // If the model didn't request any tool calls, we're done.
            if (pendingToolUse.Count == 0 || !string.Equals(stopReason, "tool_use", StringComparison.OrdinalIgnoreCase))
            {
                usage.Result = "Done";
                break;
            }

            // Execute tool calls locally and prepare the next conversation turn.
            target.Content = BuildAssistantContent(sb, toolLog, "Executing tool calls...");
            usage.ToolEventCount += pendingToolUse.Count;

            // Build assistant message content (text + tool_use blocks)
            var assistantContent = new JsonArray();
            if (sb.Length > 0)
                assistantContent.Add(new JsonObject { ["type"] = "text", ["text"] = sb.ToString() });

            var toolResultContent = new JsonArray();

            foreach (var (toolId, toolName, inputJsonSb) in pendingToolUse)
            {
                string inputStr = inputJsonSb.ToString();

                // Add tool_use block to assistant message
                JsonNode? inputNode;
                try { inputNode = JsonNode.Parse(inputStr); }
                catch { inputNode = new JsonObject(); }

                assistantContent.Add(new JsonObject
                {
                    ["type"] = "tool_use",
                    ["id"] = toolId,
                    ["name"] = toolName,
                    ["input"] = inputNode
                });

                // Execute the tool call
                var tcEntry = new ToolCallEntry
                {
                    ToolName = FormatToolName(toolName),
                    ArgsSummary = SummarizeToolArguments(toolName, inputStr),
                };
                AddToolCallSegmented(target, tcEntry);

                var toolResult = await ExecuteMcpToolCallAsync(toolName, inputStr, ct);
                usage.McpEventCount++;

                tcEntry.ResultSummary = SummarizeToolResult(toolResult.Text);
                tcEntry.ContextResultSummary = SummarizeToolResultForContext(toolResult.Text);
                tcEntry.IsError = toolResult.IsError;
                tcEntry.ResultFilePath = toolResult.ImagePath;
                tcEntry.IsComplete = true;

                if (toolResult.IsError)
                    Debug.LogWarning($"MCP tool call '{toolName}' failed: {toolResult.Text}");

                AppendToolCallLog(toolLog, toolName, inputStr, toolResult.Text);
                target.Content = BuildAssistantContent(sb, toolLog, "Executing tool calls...");

                // Add tool_result block for user message, with optional image content.
                if (toolResult.HasImage)
                {
                    toolResultContent.Add(new JsonObject
                    {
                        ["type"] = "tool_result",
                        ["tool_use_id"] = toolId,
                        ["content"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["type"] = "text",
                                ["text"] = toolResult.Text
                            },
                            new JsonObject
                            {
                                ["type"] = "image",
                                ["source"] = new JsonObject
                                {
                                    ["type"] = "base64",
                                    ["media_type"] = "image/png",
                                    ["data"] = toolResult.ImageBase64
                                }
                            }
                        }
                    });
                }
                else
                {
                    toolResultContent.Add(new JsonObject
                    {
                        ["type"] = "tool_result",
                        ["tool_use_id"] = toolId,
                        ["content"] = toolResult.Text
                    });
                }
            }

            // Add assistant turn and user turn with tool results
            messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = assistantContent });
            messages.Add(new JsonObject { ["role"] = "user", ["content"] = toolResultContent });

            target.Content = BuildAssistantContent(sb, toolLog, "Waiting for model response...");
        }

        if (sb.Length > 0)
            target.Content = BuildAssistantContent(sb, toolLog);
        else if (string.IsNullOrWhiteSpace(target.Content))
        {
            string events = seenEventTypes.Count > 0
                ? string.Join(", ", seenEventTypes)
                : "none";
            target.Content = $"(No response content received from the API. SSE lines: {sseLineCount}, event types seen: {events})";
        }
    }
}
