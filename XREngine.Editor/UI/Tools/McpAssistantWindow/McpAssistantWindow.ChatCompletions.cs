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
    // ── OpenAI-Compatible Chat Completions API — Streaming SSE with Tool-Use Loop ─
    // Used by Gemini (Google) and GitHub Models, which expose OpenAI-compatible
    // chat/completions endpoints with standard SSE streaming and function calling.

    private async Task StreamChatCompletionsAsync(
        string prompt,
        ChatMessage target,
        CancellationToken ct,
        McpUsageEntry usage,
        ProviderType provider,
        string baseUrl,
        string model,
        Action<HttpRequestMessage> configureAuth)
    {
        bool requestLikelyNeedsTools = IsLikelySceneMutationPrompt(prompt);
        usage.RequireToolUse = requestLikelyNeedsTools;

        // Local workspace tools are always available (file search, apply patch).
        JsonArray functionTools = BuildLocalAssistantFunctionTools();

        // If MCP is enabled, fetch tools as OpenAI function tools.
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

            AppendTools(functionTools, ConvertMcpToolsToOpenAiFunctions(mcpTools));
            usage.McpPayloadIncluded = true;
            usage.ToolChoice = requestLikelyNeedsTools ? "required" : "auto";
        }

        string instructions = BuildSystemInstructions(provider, requestLikelyNeedsTools, attachMcp: AttachMcpServer, keepCameraOnWorkingArea: AutoCameraView);

        // Build conversation messages — starts with system + user prompt, grows with tool results.
        // If a viewport screenshot was captured, send multimodal content (text + image_url).
        JsonNode userContent;
        string? screenshotBase64 = Interlocked.Exchange(ref _pendingScreenshotBase64, null);
        if (screenshotBase64 is not null)
        {
            userContent = new JsonArray
            {
                new JsonObject { ["type"] = "text", ["text"] = prompt },
                new JsonObject
                {
                    ["type"] = "image_url",
                    ["image_url"] = new JsonObject
                    {
                        ["url"] = $"data:image/png;base64,{screenshotBase64}"
                    }
                }
            };
        }
        else
        {
            userContent = JsonValue.Create(prompt)!;
        }
        var messages = new JsonArray
        {
            new JsonObject { ["role"] = "system", ["content"] = instructions },
            new JsonObject { ["role"] = "user", ["content"] = userContent }
        };

        const int maxToolRounds = 10;
        var sb = new StringBuilder();
        var toolLog = new StringBuilder();
        var seenEventTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int sseLineCount = 0;

        for (int round = 0; round < maxToolRounds; round++)
        {
            // Track where this round's text starts in the shared StringBuilder
            // so that SyncTextSegment only writes this round's text to the current segment.
            int sbRoundStart = sb.Length;
            bool forceTextFinalRound = round == maxToolRounds - 1;

            var payload = new JsonObject
            {
                ["model"] = model,
                ["messages"] = JsonNode.Parse(messages.ToJsonString()),
                ["stream"] = true,
                ["max_tokens"] = MaxTokens
            };

            if (functionTools.Count > 0)
            {
                payload["tools"] = JsonNode.Parse(functionTools.ToJsonString());
                if (forceTextFinalRound)
                    payload["tool_choice"] = "none";
                else if (round == 0 && requestLikelyNeedsTools && AttachMcpServer)
                    payload["tool_choice"] = "required";
            }

            string url = baseUrl.TrimEnd('/') + "/chat/completions";
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
            };
            configureAuth(request);

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

            // Stream SSE — Chat Completions returns delta chunks with choices[0].delta.
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream);

            // Track pending tool calls by index within the choices[0] delta.
            var pendingCalls = new List<PendingFunctionCall>();
            var callsByIndex = new Dictionary<int, PendingFunctionCall>();
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

                    // Chat Completions SSE shape: { choices: [{ delta: { content, tool_calls }, finish_reason }] }
                    if (!root.TryGetProperty("choices", out var choicesEl)
                        || choicesEl.GetArrayLength() == 0)
                        continue;

                    JsonElement choice = choicesEl[0];

                    if (!choice.TryGetProperty("delta", out var deltaEl))
                        continue;

                    // Text content delta
                    if (deltaEl.TryGetProperty("content", out var contentEl)
                        && contentEl.ValueKind == JsonValueKind.String)
                    {
                        string? text = contentEl.GetString();
                        if (text is not null)
                        {
                            sb.Append(text);
                            target.Content = BuildAssistantContent(sb, toolLog);
                            SyncTextSegment(target, sb, sbRoundStart);
                        }
                    }

                    // Tool call deltas (streamed incrementally)
                    if (deltaEl.TryGetProperty("tool_calls", out var tcArrayEl)
                        && tcArrayEl.ValueKind == JsonValueKind.Array)
                    {
                        usage.ToolEventCount++;
                        foreach (JsonElement tcEl in tcArrayEl.EnumerateArray())
                        {
                            int idx = tcEl.TryGetProperty("index", out var idxEl) ? idxEl.GetInt32() : 0;

                            if (!callsByIndex.TryGetValue(idx, out var pending))
                            {
                                string? callId = tcEl.TryGetProperty("id", out var cidEl) ? cidEl.GetString() : $"call_{idx}";
                                string? name = null;
                                if (tcEl.TryGetProperty("function", out var fnEl)
                                    && fnEl.TryGetProperty("name", out var nameEl))
                                    name = nameEl.GetString();

                                pending = new PendingFunctionCall { CallId = callId ?? $"call_{idx}", Name = name ?? string.Empty };
                                callsByIndex[idx] = pending;
                                pendingCalls.Add(pending);
                            }

                            // Accumulate function name if streamed in parts
                            if (tcEl.TryGetProperty("function", out var fnEl2))
                            {
                                if (fnEl2.TryGetProperty("arguments", out var argsEl)
                                    && argsEl.ValueKind == JsonValueKind.String)
                                {
                                    pending.Arguments.Append(argsEl.GetString());
                                }
                            }
                        }
                    }
                }
                catch (JsonException)
                {
                    // Malformed SSE line — skip.
                }
            }

            // If no function calls, we're done.
            if (pendingCalls.Count == 0)
                break;

            if (forceTextFinalRound)
            {
                target.Content = BuildAssistantContent(sb, toolLog, "Tool-call round limit reached. Stopping further tool calls.");
                break;
            }

            // Execute function calls locally against the MCP server.
            string toolNames = string.Join(", ", pendingCalls.Select(c => c.Name));
            target.Content = sb.Length > 0
                ? sb + $"\n\n[Executing {pendingCalls.Count} tool(s): {toolNames}...]"
                : $"[Executing {pendingCalls.Count} tool(s): {toolNames}...]";
            usage.ToolEventCount += pendingCalls.Count;

            // Add assistant message with tool_calls to conversation
            var assistantToolCallsArray = new JsonArray();
            foreach (var call in pendingCalls)
            {
                assistantToolCallsArray.Add(new JsonObject
                {
                    ["id"] = call.CallId,
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = call.Name,
                        ["arguments"] = call.Arguments.ToString()
                    }
                });
            }
            messages.Add(new JsonObject
            {
                ["role"] = "assistant",
                ["tool_calls"] = assistantToolCallsArray
            });

            foreach (var call in pendingCalls)
            {
                var tcEntry = new ToolCallEntry
                {
                    ToolName = FormatToolName(call.Name),
                    ArgsSummary = SummarizeToolArguments(call.Name, call.Arguments.ToString()),
                };
                AddToolCallSegmented(target, tcEntry);

                var toolResult = await ExecuteMcpToolCallAsync(call.Name, call.Arguments.ToString(), ct);
                usage.McpEventCount++;

                tcEntry.ResultSummary = SummarizeToolResult(toolResult.Text);
                tcEntry.ContextResultSummary = SummarizeToolResultForContext(toolResult.Text);
                tcEntry.IsError = toolResult.IsError;
                tcEntry.ResultFilePath = toolResult.ImagePath;
                tcEntry.IsComplete = true;

                if (toolResult.IsError)
                    Debug.LogWarning($"MCP tool call '{call.Name}' failed: {toolResult.Text}");

                AppendToolCallLog(toolLog, call.Name, call.Arguments.ToString(), toolResult.Text);
                target.Content = BuildAssistantContent(sb, toolLog, "Executing tool calls...");

                // Append tool result to conversation, with optional image for multimodal context.
                if (toolResult.HasImage)
                {
                    messages.Add(new JsonObject
                    {
                        ["role"] = "tool",
                        ["tool_call_id"] = call.CallId,
                        ["content"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["type"] = "text",
                                ["text"] = toolResult.Text
                            },
                            new JsonObject
                            {
                                ["type"] = "image_url",
                                ["image_url"] = new JsonObject
                                {
                                    ["url"] = $"data:image/png;base64,{toolResult.ImageBase64}"
                                }
                            }
                        }
                    });
                }
                else
                {
                    messages.Add(new JsonObject
                    {
                        ["role"] = "tool",
                        ["tool_call_id"] = call.CallId,
                        ["content"] = toolResult.Text
                    });
                }
            }

            // Continue the loop — next round will send conversation with tool results.
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
