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
    // ── OpenAI Responses API — Streaming SSE with Local Tool-Use Loop ───

    private async Task StreamOpenAiResponsesAsync(string prompt, ChatMessage target, CancellationToken ct, McpUsageEntry usage)
    {
        bool requestLikelyNeedsTools = IsLikelySceneMutationPrompt(prompt);
        usage.RequireToolUse = requestLikelyNeedsTools;

        // Local workspace tools are always available (file search, apply patch).
        JsonArray openAiFunctionTools = BuildLocalAssistantFunctionTools();

        // If MCP is enabled, fetch tools from local MCP server and convert to OpenAI function tools.
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

            AppendTools(openAiFunctionTools, ConvertMcpToolsToOpenAiFunctions(mcpTools));
            usage.McpPayloadIncluded = true;
            usage.ToolChoice = requestLikelyNeedsTools ? "required" : "auto";
        }

        string model = string.IsNullOrWhiteSpace(OpenAiModel) ? "gpt-4o" : OpenAiModel;

        // OpenAI-native hosted tools (Responses API) — model-dependent.
        JsonArray openAiHostedTools = BuildOpenAiHostedTools(model);

        // Build the conversation input — starts with just the user prompt, grows with tool call results.
        // If a viewport screenshot was captured, send multimodal content (text + image).
        JsonObject userMessage;
        string? screenshotBase64 = Interlocked.Exchange(ref _pendingScreenshotBase64, null);
        if (screenshotBase64 is not null)
        {
            userMessage = new JsonObject
            {
                ["role"] = "user",
                ["content"] = new JsonArray
                {
                    new JsonObject { ["type"] = "input_text", ["text"] = prompt },
                    new JsonObject
                    {
                        ["type"] = "input_image",
                        ["image_url"] = $"data:image/png;base64,{screenshotBase64}"
                    }
                }
            };
        }
        else
        {
            userMessage = new JsonObject { ["role"] = "user", ["content"] = prompt };
        }
        var conversationInput = new JsonArray { userMessage };

        string instructions = BuildSystemInstructions(ProviderType.Codex, requestLikelyNeedsTools, attachMcp: AttachMcpServer, keepCameraOnWorkingArea: AutoCameraView);

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
            bool forceTextFinalRound = round == maxToolRounds - 1;

            var payload = new JsonObject
            {
                ["model"] = model,
                ["input"] = JsonNode.Parse(conversationInput.ToJsonString()),
                ["stream"] = true,
                ["instructions"] = instructions
            };

            var allTools = new JsonArray();
            AppendTools(allTools, openAiFunctionTools);
            AppendTools(allTools, openAiHostedTools);

            if (allTools.Count > 0)
            {
                payload["tools"] = JsonNode.Parse(allTools.ToJsonString());
                if (forceTextFinalRound)
                    payload["tool_choice"] = "none";
                // Only require function tool usage on first round when mutation is likely.
                else if (round == 0 && requestLikelyNeedsTools && AttachMcpServer && openAiFunctionTools.Count > 0)
                    payload["tool_choice"] = "required";
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/responses")
            {
                Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", OpenAiApiKey.Trim());

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

            // If the response isn't SSE, read the whole body and extract text.
            string? contentType = response.Content.Headers.ContentType?.MediaType;
            if (contentType is not null && !contentType.Contains("event-stream", StringComparison.OrdinalIgnoreCase))
            {
                string body = await response.Content.ReadAsStringAsync(ct);
                string extracted = ExtractOpenAiResponseText(body);
                sb.Append(extracted);
                target.Content = sb.ToString();
                SyncTextSegment(target, sb, sbRoundStart);
                return;
            }

            // Stream SSE, collecting text deltas and function calls.
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream);

            var pendingCalls = new List<PendingFunctionCall>();
            var callsByIndex = new Dictionary<int, PendingFunctionCall>();
            string? responseId = null;
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
                        // Legacy shape without "type" — try delta extraction
                        if (TryExtractDelta(root, out string? legacyDelta) && !string.IsNullOrEmpty(legacyDelta))
                        {
                            sb.Append(legacyDelta);
                            target.Content = sb.ToString();
                            SyncTextSegment(target, sb, sbRoundStart);
                        }
                        continue;
                    }

                    string eventType = typeEl.GetString() ?? string.Empty;
                    seenEventTypes.Add(eventType);

                    // Track MCP/tool event counts for diagnostics
                    if (eventType.Contains("tool", StringComparison.OrdinalIgnoreCase)
                        || eventType.Contains("function", StringComparison.OrdinalIgnoreCase))
                        usage.ToolEventCount++;

                    // Extract response ID
                    if (string.Equals(eventType, "response.created", StringComparison.OrdinalIgnoreCase)
                        && root.TryGetProperty("response", out var respEl)
                        && respEl.TryGetProperty("id", out var idEl))
                    {
                        responseId = idEl.GetString();
                    }

                    // Text delta — but skip function_call event types whose
                    // "delta" field contains argument JSON, not user-visible text.
                    if (!eventType.StartsWith("response.function_call", StringComparison.OrdinalIgnoreCase)
                        && TryExtractOpenAiSseText(root, out string? deltaText))
                    {
                        sb.Append(deltaText);
                        target.Content = sb.ToString();
                        SyncTextSegment(target, sb, sbRoundStart);
                        continue;
                    }

                    // Function call started
                    if (string.Equals(eventType, "response.output_item.added", StringComparison.OrdinalIgnoreCase)
                        && root.TryGetProperty("item", out var itemEl)
                        && itemEl.TryGetProperty("type", out var itemTypeEl)
                        && string.Equals(itemTypeEl.GetString(), "function_call", StringComparison.OrdinalIgnoreCase))
                    {
                        string callId = itemEl.TryGetProperty("call_id", out var cidEl) ? cidEl.GetString() ?? "" : "";
                        string name = itemEl.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? "" : "";
                        int outputIndex = root.TryGetProperty("output_index", out var oiEl) ? oiEl.GetInt32() : -1;

                        var pending = new PendingFunctionCall { CallId = callId, Name = name };
                        pendingCalls.Add(pending);
                        if (outputIndex >= 0)
                            callsByIndex[outputIndex] = pending;

                        // Show the user which tools the model is planning to call.
                        target.Content = sb.Length > 0
                            ? sb + $"\n\n[Calling tool: {name}...]" 
                            : $"[Calling tool: {name}...]";
                    }

                    // Function call arguments delta
                    if (string.Equals(eventType, "response.function_call_arguments.delta", StringComparison.OrdinalIgnoreCase)
                        && root.TryGetProperty("delta", out var argDelta)
                        && argDelta.ValueKind == JsonValueKind.String)
                    {
                        int idx = root.TryGetProperty("output_index", out var oIdx) ? oIdx.GetInt32() : -1;
                        if (callsByIndex.TryGetValue(idx, out var pc))
                            pc.Arguments.Append(argDelta.GetString());
                    }

                    // Function call arguments done (complete string)
                    if (string.Equals(eventType, "response.function_call_arguments.done", StringComparison.OrdinalIgnoreCase)
                        && root.TryGetProperty("arguments", out var argsDone)
                        && argsDone.ValueKind == JsonValueKind.String)
                    {
                        int idx = root.TryGetProperty("output_index", out var oIdx) ? oIdx.GetInt32() : -1;
                        if (callsByIndex.TryGetValue(idx, out var pc))
                        {
                            pc.Arguments.Clear();
                            pc.Arguments.Append(argsDone.GetString());
                        }
                    }

                    // Response completed — use exact event types to avoid
                    // breaking on per-item events like response.output_item.done
                    // or response.function_call_arguments.done.
                    if (string.Equals(eventType, "response.completed", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(eventType, "response.done", StringComparison.OrdinalIgnoreCase))
                    {
                        if (sb.Length == 0)
                            ExtractCompletedResponseText(root, sb);

                        if (TryExtractGeneratedImagePath(root, out string? imagePath))
                        {
                            if (sb.Length > 0)
                                sb.Append("\n\n");
                            sb.Append($"[Image generated: {imagePath}]");
                        }

                        break;
                    }

                    // Response failed
                    if (eventType.Contains("failed", StringComparison.OrdinalIgnoreCase))
                    {
                        string? errMsg = TryExtractEventErrorMessage(root);
                        if (!string.IsNullOrWhiteSpace(errMsg))
                            sb.Append($"\n\n--- API Error ---\n{errMsg}");
                        break;
                    }
                }
                catch (JsonException)
                {
                    // Malformed SSE line — skip.
                }
            }

            // If no function calls were collected, we're done.
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

                // Append the function call and its result to the conversation for the next round.
                conversationInput.Add(new JsonObject
                {
                    ["type"] = "function_call",
                    ["call_id"] = call.CallId,
                    ["name"] = call.Name,
                    ["arguments"] = call.Arguments.ToString()
                });
                conversationInput.Add(new JsonObject
                {
                    ["type"] = "function_call_output",
                    ["call_id"] = call.CallId,
                    ["output"] = toolResult.Text
                });

                // If the tool produced an image (e.g. screenshot), attach it as visual context.
                if (toolResult.HasImage)
                {
                    conversationInput.Add(new JsonObject
                    {
                        ["type"] = "message",
                        ["role"] = "user",
                        ["content"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["type"] = "input_image",
                                ["image_url"] = $"data:image/png;base64,{toolResult.ImageBase64}"
                            }
                        }
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

    /// <summary>
    /// Tries to extract delta text from an SSE event JSON object.
    /// Handles multiple OpenAI event shapes:
    ///   { "delta": "text" }
    ///   { "delta": { "text": "text" } }
    ///   { "delta": { "content": "text" } }
    ///   { "delta": { "value": "text" } }
    /// </summary>
    private static bool TryExtractDelta(JsonElement root, out string? text)
    {
        text = null;
        if (!root.TryGetProperty("delta", out var delta))
            return false;

        // Direct string delta: { "delta": "hello" }
        if (delta.ValueKind == JsonValueKind.String)
        {
            text = delta.GetString();
            return text is not null;
        }

        // Object delta — try common field names
        if (delta.ValueKind == JsonValueKind.Object)
        {
            string[] fieldNames = ["text", "content", "value"];
            foreach (string field in fieldNames)
            {
                if (delta.TryGetProperty(field, out var inner) && inner.ValueKind == JsonValueKind.String)
                {
                    text = inner.GetString();
                    if (text is not null)
                        return true;
                }
            }
        }

        return false;
    }

    private static bool TryExtractOpenAiSseText(JsonElement root, out string? text)
    {
        text = null;

        if (TryExtractDelta(root, out text) && !string.IsNullOrEmpty(text))
            return true;

        if (root.TryGetProperty("type", out var typeEl))
        {
            string eventType = typeEl.GetString() ?? string.Empty;

            if ((string.Equals(eventType, "response.output_text.delta", StringComparison.OrdinalIgnoreCase)
                || string.Equals(eventType, "response.text.delta", StringComparison.OrdinalIgnoreCase))
                && root.TryGetProperty("delta", out var deltaEl)
                && deltaEl.ValueKind == JsonValueKind.String)
            {
                text = deltaEl.GetString();
                return !string.IsNullOrEmpty(text);
            }

            if (string.Equals(eventType, "response.output_item.added", StringComparison.OrdinalIgnoreCase)
                && root.TryGetProperty("item", out var itemEl)
                && itemEl.ValueKind == JsonValueKind.Object)
            {
                if (TryExtractTextFromOutputItem(itemEl, out text))
                    return true;
            }
        }

        return false;
    }

    private static bool TryExtractTextFromOutputItem(JsonElement item, out string? text)
    {
        text = null;

        if (item.TryGetProperty("type", out var itemTypeEl))
        {
            string itemType = itemTypeEl.GetString() ?? string.Empty;
            if (!string.Equals(itemType, "message", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(itemType, "output_text", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        var sb = new StringBuilder();
        if (item.TryGetProperty("content", out var contentEl) && contentEl.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement block in contentEl.EnumerateArray())
            {
                if (block.TryGetProperty("text", out var textEl) && textEl.ValueKind == JsonValueKind.String)
                    sb.Append(textEl.GetString());
            }
        }

        if (sb.Length > 0)
        {
            text = sb.ToString();
            return true;
        }

        return false;
    }

    private static string? TryExtractEventErrorMessage(JsonElement root)
    {
        if (root.TryGetProperty("error", out var errEl) && errEl.ValueKind == JsonValueKind.Object)
        {
            if (errEl.TryGetProperty("message", out var msgEl) && msgEl.ValueKind == JsonValueKind.String)
                return msgEl.GetString();
        }

        if (root.TryGetProperty("message", out var topMsgEl) && topMsgEl.ValueKind == JsonValueKind.String)
            return topMsgEl.GetString();

        return null;
    }

    private static bool IsProviderErrorContent(string content, out string? summary)
    {
        summary = null;
        if (string.IsNullOrWhiteSpace(content))
            return false;

        if (TryExtractJsonErrorSummary(content, out string? jsonSummary))
        {
            summary = jsonSummary;
            return true;
        }

        if (content.Contains("--- Retry Failed", StringComparison.OrdinalIgnoreCase)
            || content.Contains("insufficient_quota", StringComparison.OrdinalIgnoreCase)
            || content.Contains("rate_limit", StringComparison.OrdinalIgnoreCase)
            || content.Contains("authentication", StringComparison.OrdinalIgnoreCase)
            || content.Contains("invalid_api_key", StringComparison.OrdinalIgnoreCase))
        {
            summary = "Provider request failed.";
            return true;
        }

        return false;
    }

    private static string? ExtractOpenAiErrorSummary(string content)
        => TryExtractJsonErrorSummary(content, out string? summary) ? summary : null;

    private static bool TryExtractJsonErrorSummary(string content, out string? summary)
    {
        summary = null;

        // Quick pre-check: valid JSON must start with '{' or '[' (ignoring whitespace).
        ReadOnlySpan<char> trimmed = content.AsSpan().TrimStart();
        if (trimmed.Length == 0 || (trimmed[0] != '{' && trimmed[0] != '['))
            return false;

        // Reject concatenated JSON objects (e.g. "{...}{...}") — find the matching
        // close brace/bracket and ensure nothing significant follows it.
        if (!LooksLikeSingleJsonValue(trimmed))
            return false;

        // Use Utf8JsonReader to probe validity before committing to a full parse.
        // This avoids first-chance JsonReaderException noise in the debugger.
        ReadOnlySpan<byte> utf8 = System.Text.Encoding.UTF8.GetBytes(content);
        var readerCheck = new Utf8JsonReader(utf8);
        try
        {
            if (!JsonDocument.TryParseValue(ref readerCheck, out var maybeDoc) || maybeDoc is null)
                return false;
            using var doc = maybeDoc;
            JsonElement root = doc.RootElement;

            if (!root.TryGetProperty("error", out var errEl) || errEl.ValueKind != JsonValueKind.Object)
                return false;

            string? message = null;
            string? code = null;

            if (errEl.TryGetProperty("message", out var msgEl) && msgEl.ValueKind == JsonValueKind.String)
                message = msgEl.GetString();

            if (errEl.TryGetProperty("code", out var codeEl) && codeEl.ValueKind == JsonValueKind.String)
                code = codeEl.GetString();

            if (string.Equals(code, "insufficient_quota", StringComparison.OrdinalIgnoreCase))
            {
                summary = "OpenAI quota/billing issue (insufficient_quota).";
                return true;
            }

            summary = !string.IsNullOrWhiteSpace(message)
                ? $"OpenAI error: {message}"
                : "OpenAI request failed.";
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Cheap heuristic: counts matched braces/brackets to check that the span
    /// contains a single JSON value and not concatenated objects like "{...}{...}".
    /// </summary>
    private static bool LooksLikeSingleJsonValue(ReadOnlySpan<char> trimmed)
    {
        char open = trimmed[0];
        char close = open == '{' ? '}' : ']';
        int depth = 0;
        bool inString = false;
        bool escaped = false;

        for (int i = 0; i < trimmed.Length; i++)
        {
            char c = trimmed[i];
            if (escaped) { escaped = false; continue; }
            if (c == '\\' && inString) { escaped = true; continue; }
            if (c == '"') { inString = !inString; continue; }
            if (inString) continue;

            if (c == open) depth++;
            else if (c == close)
            {
                depth--;
                if (depth == 0)
                {
                    // Anything non-whitespace after the closing bracket → not single value.
                    ReadOnlySpan<char> rest = trimmed[(i + 1)..].TrimStart();
                    return rest.Length == 0;
                }
            }
        }

        return false; // Unbalanced — not valid.
    }

    /// <summary>
    /// When a response.completed event arrives and we haven't streamed any deltas,
    /// try to pull output_text or output[].content[].text from the completed response object.
    /// </summary>
    private static void ExtractCompletedResponseText(JsonElement root, StringBuilder sb)
    {
        // { "response": { "output_text": "..." } }
        if (root.TryGetProperty("response", out var resp))
        {
            if (resp.TryGetProperty("output_text", out var otEl) && otEl.ValueKind == JsonValueKind.String)
            {
                string? ot = otEl.GetString();
                if (!string.IsNullOrWhiteSpace(ot))
                {
                    sb.Append(ot);
                    return;
                }
            }

            // { "response": { "output": [ { "content": [ { "text": "..." } ] } ] } }
            if (resp.TryGetProperty("output", out var outputArr) && outputArr.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in outputArr.EnumerateArray())
                {
                    if (item.TryGetProperty("content", out var contentArr) && contentArr.ValueKind == JsonValueKind.Array)
                    {
                        foreach (JsonElement block in contentArr.EnumerateArray())
                        {
                            if (block.TryGetProperty("text", out var txt) && txt.ValueKind == JsonValueKind.String)
                                sb.Append(txt.GetString());
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    /// Extracts text from a non-streamed OpenAI Responses API JSON body.
    /// </summary>
    private static string ExtractOpenAiResponseText(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            JsonElement root = doc.RootElement;

            // Top-level output_text
            if (root.TryGetProperty("output_text", out var otEl) && otEl.ValueKind == JsonValueKind.String)
            {
                string? ot = otEl.GetString();
                if (!string.IsNullOrWhiteSpace(ot))
                    return ot;
            }

            // output[].content[].text
            if (root.TryGetProperty("output", out var outputArr) && outputArr.ValueKind == JsonValueKind.Array)
            {
                var sb = new StringBuilder();
                foreach (JsonElement item in outputArr.EnumerateArray())
                {
                    if (item.TryGetProperty("content", out var contentArr) && contentArr.ValueKind == JsonValueKind.Array)
                    {
                        foreach (JsonElement block in contentArr.EnumerateArray())
                        {
                            if (block.TryGetProperty("text", out var txt) && txt.ValueKind == JsonValueKind.String)
                                sb.Append(txt.GetString());
                        }
                    }
                }
                if (sb.Length > 0)
                    return sb.ToString();
            }

            // Deep search fallback
            if (TryExtractFirstTextNode(root, out string extracted))
                return extracted;
        }
        catch (JsonException)
        {
            // Not JSON — return raw.
        }

        return body;
    }

    private static string ExtractAnthropicResponseText(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            JsonElement root = doc.RootElement;
            if (root.TryGetProperty("content", out var contentEl) && contentEl.ValueKind == JsonValueKind.Array)
            {
                var sb = new StringBuilder();
                foreach (JsonElement item in contentEl.EnumerateArray())
                {
                    if (item.TryGetProperty("type", out var typeEl)
                        && string.Equals(typeEl.GetString(), "text", StringComparison.OrdinalIgnoreCase)
                        && item.TryGetProperty("text", out var textEl)
                        && textEl.ValueKind == JsonValueKind.String)
                    {
                        sb.Append(textEl.GetString());
                    }
                }

                if (sb.Length > 0)
                    return sb.ToString();
            }
        }
        catch (JsonException)
        {
        }

        return body;
    }

    private static string ExtractOpenAiCompatibleResponseText(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            JsonElement root = doc.RootElement;
            if (root.TryGetProperty("choices", out var choicesEl)
                && choicesEl.ValueKind == JsonValueKind.Array
                && choicesEl.GetArrayLength() > 0)
            {
                JsonElement choice = choicesEl[0];
                if (choice.TryGetProperty("message", out var messageEl)
                    && messageEl.TryGetProperty("content", out var contentEl))
                {
                    if (contentEl.ValueKind == JsonValueKind.String)
                        return contentEl.GetString() ?? body;

                    if (contentEl.ValueKind == JsonValueKind.Array)
                    {
                        var sb = new StringBuilder();
                        foreach (JsonElement block in contentEl.EnumerateArray())
                        {
                            if (block.TryGetProperty("text", out var textEl)
                                && textEl.ValueKind == JsonValueKind.String)
                            {
                                sb.Append(textEl.GetString());
                            }
                        }

                        if (sb.Length > 0)
                            return sb.ToString();
                    }
                }
            }
        }
        catch (JsonException)
        {
        }

        return body;
    }
}
