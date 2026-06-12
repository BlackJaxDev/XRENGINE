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
    private bool ShouldRequestSelfSummary(ProviderType provider, string currentPrompt, ChatMessage assistantMsg)
    {
        if (!AutoSummarizeNearContextLimit)
            return false;

        int contextWindowTokens = ResolveContextWindowTokens(provider);
        if (contextWindowTokens <= 0)
            return false;

        int contextCharsBudget = ResolveConversationContextCharsBudget(provider, currentPrompt);
        string context = BuildConversationContextBlock(assistantMsg, contextCharsBudget);
        int estimatedTokens = EstimateTokens(context) + EstimateTokens(currentPrompt);
        return estimatedTokens >= (int)(contextWindowTokens * ContextSummaryTriggerRatio);
    }

    private int ResolveConversationContextCharsBudget(ProviderType provider, string currentPrompt)
    {
        int contextWindowTokens = ResolveContextWindowTokens(provider);
        if (contextWindowTokens <= 0)
            return 24_000;

        int responseReserveTokens = Math.Clamp(MaxTokens, 1_024, Math.Max(2_048, contextWindowTokens / 2));
        int envelopeReserveTokens = Math.Clamp(contextWindowTokens / 20, 2_048, 24_000);
        int promptTokens = EstimateTokens(currentPrompt);
        int availableInputTokens = Math.Max(4_096, contextWindowTokens - responseReserveTokens - envelopeReserveTokens - promptTokens);
        return Math.Max(16_000, availableInputTokens * 4);
    }

    private async Task AutoCompactConversationContextIfNeededAsync(ProviderType provider, string currentPrompt, ChatMessage? activeAssistantMessage, int minTailMessagesToKeep, CancellationToken ct)
    {
        if (!AutoSummarizeNearContextLimit)
            return;

        if (_historyCompactedThroughExclusive < 0 || _historyCompactedThroughExclusive > _history.Count)
            _historyCompactedThroughExclusive = Math.Clamp(_historyCompactedThroughExclusive, 0, _history.Count);

        if (_history.Count <= Math.Max(1, minTailMessagesToKeep))
            return;

        int contextCharsBudget = ResolveConversationContextCharsBudget(provider, currentPrompt);
        int fullContextChars = EstimateRawHistoryContextCharsForCompaction(activeAssistantMessage, contextCharsBudget);
        if (fullContextChars <= contextCharsBudget)
            return;

        int minRawIndexToKeep = Math.Max(_historyCompactedThroughExclusive, _history.Count - Math.Max(1, minTailMessagesToKeep));
        if (minRawIndexToKeep <= _historyCompactedThroughExclusive)
            return;

        int summaryBudget = Math.Clamp(contextCharsBudget / 6, 3_000, 24_000);
        int toolBudget = Math.Clamp((int)(contextCharsBudget * ContextToolReferenceBudgetRatio), 4_000, 40_000);
        int rawBudget = Math.Max(2_048, contextCharsBudget - summaryBudget - toolBudget);
        int newCutoff = FindRawHistoryCutoffForBudget(activeAssistantMessage, rawBudget, minRawIndexToKeep);
        if (newCutoff <= _historyCompactedThroughExclusive)
            return;

        string compactedSummary = await BuildCompactedConversationSummaryAsync(provider, _historyCompactedThroughExclusive, newCutoff, summaryBudget, ct);
        if (string.IsNullOrWhiteSpace(compactedSummary))
            return;

        _conversationSummary = compactedSummary;
        _historyCompactedThroughExclusive = newCutoff;
    }

    private int ResolveContextWindowTokens(ProviderType provider)
    {
        // For OpenAI Codex, use the dynamic lookup chain (API fetch → cache → well-known table).
        if (provider == ProviderType.Codex)
        {
            if (_openAiSelectedContextWindowTokens > 0)
                return _openAiSelectedContextWindowTokens;

            string model = OpenAiModel.Trim();
            if (!string.IsNullOrWhiteSpace(model)
                && _openAiContextWindowByModel.TryGetValue(model, out int mapped)
                && mapped > 0)
                return mapped;

            return DefaultOpenAiContextWindowTokens;
        }

        // For all other providers, look up the selected model in the well-known table.
        string selectedModel = provider switch
        {
            ProviderType.ClaudeCode => AnthropicModel.Trim(),
            ProviderType.Gemini => GeminiModel.Trim(),
            ProviderType.GitHubModels => GitHubModelsModel.Trim(),
            _ => string.Empty
        };

        if (!string.IsNullOrWhiteSpace(selectedModel) && TryLookupKnownContextWindow(selectedModel, out int known))
            return known;

        // Sensible defaults per provider when the model isn't in the table.
        return provider switch
        {
            ProviderType.ClaudeCode => 200_000,
            ProviderType.Gemini => 1_048_576,
            ProviderType.GitHubModels => 128_000,
            _ => DefaultOpenAiContextWindowTokens
        };
    }

    private string BuildProviderPromptEnvelope(string userPrompt, ChatMessage assistantMsg, int autoRepromptIndex, bool requestSelfSummary)
    {
        ProviderType provider = (ProviderType)ProviderIndex;
        int contextWindowTokens = ResolveContextWindowTokens(provider);
        int contextCharsBudget = ResolveConversationContextCharsBudget(provider, userPrompt);

        string contextBlock = BuildConversationContextBlock(assistantMsg, contextCharsBudget);
        var sb = new StringBuilder();

        sb.AppendLine("Protocol requirements (must follow exactly):");
        sb.AppendLine("- Complete ALL parts of the user's request before finishing. Do NOT stop at an intermediate step.");
        sb.AppendLine("- If you have a multi-step plan, execute every step — do not list remaining steps and stop.");
        sb.AppendLine("- If a tool call fails, retry or use an alternative approach. Do not abandon the task.");
        sb.AppendLine("- Do NOT claim missing/denied editor or tool access unless a tool call in this conversation explicitly returned an access/permission error.");
        sb.AppendLine("- During tool use, include brief progress text; do not output only raw tool activity.");
        sb.AppendLine("- Avoid repeated read-only probes that return the same data. If no new information appears, stop probing and complete with best-effort results plus explicit blockers.");
        sb.AppendLine($"- When ALL work is done: output 'Completed Work' summary, 'Suggested Follow-ups' (optional), then {AssistantDoneMarker} on the very last line.");
        sb.AppendLine($"- When you have more steps remaining but your current response is long or you've hit a tool-call limit: output a brief progress summary of what was done and what remains, then {AssistantContinueMarker} on the last line. This triggers an automatic re-prompt so you can continue.");
        sb.AppendLine($"- If any work remains, do NOT output the done marker — either keep working or emit the continue marker.");
        sb.AppendLine($"- Never emit both {AssistantDoneMarker} and {AssistantContinueMarker} in the same response.");
        sb.AppendLine("- Keep continuity with prior tool calls/results and avoid restarting work.");

        if (requestSelfSummary)
        {
            sb.AppendLine($"- Context is approaching limit. First output a concise summary wrapped in {ContextSummaryStartMarker}...{ContextSummaryEndMarker}, then continue with the task.");
        }

        if (contextWindowTokens > 0)
            sb.AppendLine($"- Selected model context window: {contextWindowTokens:n0} tokens.");

        if (autoRepromptIndex > 0)
        {
            sb.AppendLine($"- This is automatic continuation #{autoRepromptIndex}. You stopped before the work was fully complete.");
            sb.AppendLine("- Pick up exactly where you left off and finish ALL remaining work from the original request.");
            sb.AppendLine("- Do NOT re-explain what was already done. Do NOT ask the user what to do next. Just continue working.");
        }

        sb.AppendLine();
        sb.AppendLine("Conversation context:");
        sb.AppendLine(string.IsNullOrWhiteSpace(contextBlock) ? "(none)" : contextBlock);
        sb.AppendLine();
        sb.AppendLine("Current request:");
        sb.AppendLine(userPrompt);

        if (autoRepromptIndex > 0)
            sb.AppendLine("IMPORTANT: Complete all remaining work now. Do not stop until every part of the original request is fulfilled.");

        return sb.ToString();
    }

    private string BuildConversationContextBlock(ChatMessage? activeAssistantMessage, int maxChars)
    {
        if (maxChars < 1024)
            maxChars = 1024;

        var chunks = new List<string>();
        int usedChars = 0;

        static bool TryAddChunk(List<string> list, ref int used, int budget, string chunk)
        {
            if (string.IsNullOrWhiteSpace(chunk))
                return true;

            if (used + chunk.Length > budget)
                return false;

            list.Add(chunk);
            used += chunk.Length;
            return true;
        }

        if (!string.IsNullOrWhiteSpace(_conversationSummary))
        {
            string summaryChunk = $"SUMMARY:\n{_conversationSummary.Trim()}";
            TryAddChunk(chunks, ref usedChars, maxChars, summaryChunk);
        }

        if (_mcpUsageHistory.Count > 0)
        {
            var usageLines = new List<string>(_mcpUsageHistory.Count + 1)
            {
                "MCP USAGE LEDGER:"
            };

            for (int i = Math.Max(0, _mcpUsageHistory.Count - 120); i < _mcpUsageHistory.Count; i++)
            {
                McpUsageEntry u = _mcpUsageHistory[i];
                string line = $"- {u.Timestamp:HH:mm:ss} provider={u.Provider} attach={u.AttachRequested} requireToolUse={u.RequireToolUse} result={u.Result} mcpCalls={u.McpEventCount} toolCalls={u.ToolEventCount}";

                if (!string.IsNullOrWhiteSpace(u.PromptPreview))
                    line += $" prompt='{Truncate(u.PromptPreview, 240)}'";
                if (!string.IsNullOrWhiteSpace(u.Note))
                    line += $" note='{Truncate(u.Note.Replace('\n', ' '), 400)}'";

                usageLines.Add(line);
            }

            TryAddChunk(chunks, ref usedChars, maxChars, string.Join("\n", usageLines));
        }

        int remainingForToolReferences = Math.Max(2_048, maxChars - usedChars);
        if (remainingForToolReferences > 0)
        {
            int preferredToolBudget = Math.Clamp((int)(maxChars * ContextToolReferenceBudgetRatio), 4_000, 40_000);
            string toolReferenceChunk = BuildToolResponseReferenceBlock(Math.Min(remainingForToolReferences, preferredToolBudget));
            TryAddChunk(chunks, ref usedChars, maxChars, toolReferenceChunk);
        }

        // Build message lines from oldest->newest, then keep the tail that fits budget.
        // This preserves chronological append behavior while prioritizing recent context.
        var messageLines = new List<string>(_history.Count);
        for (int i = _history.Count - 1; i >= _historyCompactedThroughExclusive; i--)
        {
            ChatMessage msg = _history[i];
            string? line = BuildConversationMessageLine(msg, activeAssistantMessage, messageCharLimit: 8000, toolSummaryCharLimit: 8000, includeToolCalls: false);
            if (!string.IsNullOrWhiteSpace(line))
                messageLines.Add(line);
        }

        messageLines.Reverse();

        // Append recent message lines first (newest->oldest), then restore chronological order.
        // This preserves high-value recent turns while keeping summary/usage chunks intact.
        var selectedMessageLines = new Stack<string>();
        for (int i = messageLines.Count - 1; i >= 0; i--)
        {
            string line = messageLines[i];
            if (usedChars + line.Length <= maxChars)
            {
                selectedMessageLines.Push(line);
                usedChars += line.Length;
                continue;
            }

            if (selectedMessageLines.Count == 0 && usedChars < maxChars)
            {
                string truncated = Truncate(line, Math.Max(512, maxChars - usedChars));
                selectedMessageLines.Push(truncated);
                usedChars += truncated.Length;
            }

            break;
        }

        while (selectedMessageLines.Count > 0)
            chunks.Add(selectedMessageLines.Pop());

        return string.Join("\n", chunks);
    }

    private int FindRawHistoryCutoffForBudget(ChatMessage? activeAssistantMessage, int rawCharsBudget, int minRawIndexToKeep)
    {
        int usedChars = 0;
        int messageCharLimit = ResolveCompactionProbeMessageCharLimit(rawCharsBudget);
        for (int i = _history.Count - 1; i >= _historyCompactedThroughExclusive; i--)
        {
            string? line = BuildConversationMessageLine(_history[i], activeAssistantMessage, messageCharLimit, toolSummaryCharLimit: 8000, includeToolCalls: false);
            if (string.IsNullOrWhiteSpace(line))
                continue;

            if (usedChars + line.Length > rawCharsBudget)
                return Math.Max(_historyCompactedThroughExclusive, Math.Min(i + 1, minRawIndexToKeep));

            usedChars += line.Length;
        }

        return _historyCompactedThroughExclusive;
    }

    private int EstimateRawHistoryContextCharsForCompaction(ChatMessage? activeAssistantMessage, int contextCharsBudget)
    {
        int usedChars = 0;
        int messageCharLimit = ResolveCompactionProbeMessageCharLimit(contextCharsBudget);

        for (int i = _historyCompactedThroughExclusive; i < _history.Count; i++)
        {
            string? line = BuildConversationMessageLine(_history[i], activeAssistantMessage, messageCharLimit, toolSummaryCharLimit: 8000, includeToolCalls: false);
            if (string.IsNullOrWhiteSpace(line))
                continue;

            usedChars += line.Length;
            if (usedChars > contextCharsBudget)
                return usedChars;
        }

        return usedChars;
    }

    private static int ResolveCompactionProbeMessageCharLimit(int budget)
    {
        long limit = (long)budget + 1L;
        return (int)Math.Clamp(limit, 8_000L, 1_000_000L);
    }

    private async Task<string> BuildCompactedConversationSummaryAsync(ProviderType provider, int startIndex, int endExclusive, int maxChars, CancellationToken ct)
    {
        string transcript = BuildCompactedConversationTranscript(startIndex, endExclusive, Math.Clamp(maxChars * 6, 8_192, 96_000));
        if (string.IsNullOrWhiteSpace(transcript))
            return string.Empty;

        try
        {
            string? aiSummary = await GenerateConversationSummaryAsync(provider, transcript, maxChars, ct);
            if (!string.IsNullOrWhiteSpace(aiSummary))
                return aiSummary.Trim();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"Failed to generate AI compaction summary: {ex.Message}");
        }

        return BuildCompactedConversationSummaryFallback(startIndex, endExclusive, maxChars);
    }

    private string BuildCompactedConversationSummaryFallback(int startIndex, int endExclusive, int maxChars)
    {
        if (maxChars < 512)
            maxChars = 512;

        var lines = new List<string>(Math.Max(4, endExclusive - startIndex + 2));
        if (!string.IsNullOrWhiteSpace(_conversationSummary))
            lines.Add($"Earlier summary: {Truncate(_conversationSummary.Trim().Replace('\n', ' '), Math.Min(4_000, Math.Max(512, maxChars / 3)))}");

        lines.Add($"Compacted {Math.Max(0, endExclusive - startIndex)} earlier messages:");

        for (int i = startIndex; i < endExclusive; i++)
        {
            string? line = BuildConversationMessageLine(_history[i], activeAssistantMessage: null, messageCharLimit: 280, toolSummaryCharLimit: 320, includeToolCalls: false);
            if (!string.IsNullOrWhiteSpace(line))
                lines.Add(Truncate(line, 420));
        }

        var sb = new StringBuilder(Math.Min(maxChars, 8_192));
        for (int i = 0; i < lines.Count; i++)
        {
            string line = lines[i];
            if (string.IsNullOrWhiteSpace(line))
                continue;

            string next = sb.Length == 0 ? line : $"\n{line}";
            if (sb.Length + next.Length > maxChars)
            {
                if (sb.Length == 0)
                    sb.Append(Truncate(line, maxChars));
                break;
            }

            sb.Append(next);
        }

        return sb.ToString().Trim();
    }

    private string BuildCompactedConversationTranscript(int startIndex, int endExclusive, int maxChars)
    {
        if (maxChars < 1_024)
            maxChars = 1_024;

        var sb = new StringBuilder(Math.Min(maxChars, 16_384));

        if (!string.IsNullOrWhiteSpace(_conversationSummary))
            sb.AppendLine($"Earlier summary: {Truncate(_conversationSummary.Trim().Replace('\n', ' '), Math.Min(maxChars / 3, 8_000))}");

        sb.AppendLine("Transcript to summarize (tool responses are stored separately and must stay out of the summary):");

        for (int i = startIndex; i < endExclusive; i++)
        {
            string? line = BuildConversationMessageLine(_history[i], activeAssistantMessage: null, messageCharLimit: 12_000, toolSummaryCharLimit: 0, includeToolCalls: false);
            if (string.IsNullOrWhiteSpace(line))
                continue;

            string next = sb.Length == 0 ? line : $"\n{line}";
            if (sb.Length + next.Length > maxChars)
            {
                if (sb.Length == 0)
                    sb.Append(Truncate(line, maxChars));
                break;
            }

            sb.Append(next);
        }

        return sb.ToString().Trim();
    }

    private string BuildToolResponseReferenceBlock(int maxChars)
    {
        if (maxChars < 512)
            maxChars = 512;

        var lines = new List<string>();
        for (int i = _history.Count - 1; i >= 0; i--)
        {
            ChatMessage message = _history[i];
            ToolCallEntry[] toolCalls = SnapshotToolCalls(message);
            if (toolCalls.Length == 0)
                continue;

            foreach (var toolCall in toolCalls)
            {
                string? line = BuildToolResponseReferenceLine(message, toolCall);
                if (!string.IsNullOrWhiteSpace(line))
                    lines.Add(line);
            }
        }

        if (lines.Count == 0)
            return string.Empty;

        var sb = new StringBuilder(Math.Min(maxChars, 16_384));
        sb.Append("TOOL RESPONSE REFERENCES:");

        for (int i = 0; i < lines.Count; i++)
        {
            string next = $"\n{lines[i]}";
            if (sb.Length + next.Length > maxChars)
                break;

            sb.Append(next);
        }

        return sb.ToString().Trim();
    }

    private static string? BuildToolResponseReferenceLine(ChatMessage message, ToolCallEntry toolCall)
    {
        string result = toolCall.ContextResultSummary;
        if (string.IsNullOrWhiteSpace(result))
            result = toolCall.ResultSummary;

        if (string.IsNullOrWhiteSpace(result) && string.IsNullOrWhiteSpace(toolCall.ResultFilePath))
            return null;

        var sb = new StringBuilder();
        sb.Append($"- {message.Timestamp:HH:mm:ss} {message.Role.ToUpperInvariant()} tool {toolCall.ToolName}");
        if (!string.IsNullOrWhiteSpace(toolCall.ArgsSummary))
            sb.Append($"({toolCall.ArgsSummary})");
        if (!string.IsNullOrWhiteSpace(result))
            sb.Append($": {result}");
        if (!string.IsNullOrWhiteSpace(toolCall.ResultFilePath))
            sb.Append($" [file:{toolCall.ResultFilePath}]");
        if (toolCall.IsError)
            sb.Append(" [ERROR]");

        return sb.ToString();
    }

    private static string? BuildConversationMessageLine(ChatMessage msg, ChatMessage? activeAssistantMessage, int messageCharLimit, int toolSummaryCharLimit, bool includeToolCalls = true)
    {
        string cleaned = StripProtocolMarkers(msg.Content);
        ToolCallEntry[] toolCalls = SnapshotToolCalls(msg);

        if (ReferenceEquals(msg, activeAssistantMessage)
            && string.IsNullOrWhiteSpace(cleaned)
            && toolCalls.Length == 0)
        {
            return null;
        }

        string line = $"{msg.Role.ToUpperInvariant()} [{msg.Timestamp:HH:mm:ss}]: ";
        if (!string.IsNullOrWhiteSpace(cleaned))
            line += Truncate(cleaned.Replace('\n', ' '), Math.Max(64, messageCharLimit));
        else
            line += "(tool-only response)";

        if (includeToolCalls && toolCalls.Length > 0)
        {
            var toolParts = new List<string>(toolCalls.Length);
            foreach (var tc in toolCalls)
            {
                var part = tc.ToolName;
                if (!string.IsNullOrEmpty(tc.ArgsSummary))
                    part += $"({tc.ArgsSummary})";
                if (!string.IsNullOrEmpty(tc.ContextResultSummary))
                    part += $" -> {tc.ContextResultSummary}";
                else if (!string.IsNullOrEmpty(tc.ResultSummary))
                    part += $" -> {tc.ResultSummary}";
                if (!string.IsNullOrEmpty(tc.ResultFilePath))
                    part += $" [file:{tc.ResultFilePath}]";
                if (tc.IsError)
                    part += " [ERROR]";
                toolParts.Add(part);
            }

            string toolSummary = string.Join("; ", toolParts);
            if (!string.IsNullOrWhiteSpace(toolSummary))
                line += $" | tools: {Truncate(toolSummary, Math.Max(64, toolSummaryCharLimit))}";
        }

        return line;
    }

    private async Task<string?> GenerateConversationSummaryAsync(ProviderType provider, string transcript, int maxChars, CancellationToken ct)
    {
        string summaryPrompt = BuildConversationSummaryPrompt(transcript, maxChars);
        return provider switch
        {
            ProviderType.Codex => await RequestOpenAiConversationSummaryAsync(summaryPrompt, ct),
            ProviderType.ClaudeCode => await RequestAnthropicConversationSummaryAsync(summaryPrompt, ct),
            ProviderType.Gemini => await RequestOpenAiCompatibleConversationSummaryAsync(
                baseUrl: "https://generativelanguage.googleapis.com/v1beta/openai/",
                model: string.IsNullOrWhiteSpace(GeminiModel) ? "gemini-2.5-pro" : GeminiModel.Trim(),
                configureAuth: req => req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", GeminiApiKey.Trim()),
                prompt: summaryPrompt,
                ct: ct),
            ProviderType.GitHubModels => await RequestOpenAiCompatibleConversationSummaryAsync(
                baseUrl: "https://models.inference.ai.azure.com/",
                model: string.IsNullOrWhiteSpace(GitHubModelsModel) ? "openai/gpt-4.1" : GitHubModelsModel.Trim(),
                configureAuth: req => req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", GitHubModelsToken.Trim()),
                prompt: summaryPrompt,
                ct: ct),
            _ => null
        };
    }

    private static string BuildConversationSummaryPrompt(string transcript, int maxChars)
    {
        int safeMaxChars = Math.Max(512, maxChars);
        return $"""
Summarize the earlier conversation turns for continuation context.

Requirements:
- Summarize only the user's prompts, constraints, corrections, and questions.
- Summarize only the assistant's responses, completed work, conclusions, promises, and remaining work.
- Do not summarize tool calls or tool results. They are preserved separately.
- Keep important file names, IDs, settings, and decisions when they matter to future work.
- Write concise continuation notes, not prose narration.
- Keep the final summary under {safeMaxChars} characters.

Conversation transcript:
{transcript}
""";
    }

    private async Task<string?> RequestOpenAiConversationSummaryAsync(string prompt, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(OpenAiApiKey))
            return null;

        string model = string.IsNullOrWhiteSpace(OpenAiModel) ? "gpt-5-codex" : OpenAiModel.Trim();
        var payload = new JsonObject
        {
            ["model"] = model,
            ["input"] = prompt,
            ["instructions"] = "Return only the compacted conversation summary. Do not call tools.",
            ["max_output_tokens"] = Math.Clamp(MaxTokens / 2, 256, 2048)
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/responses")
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", OpenAiApiKey.Trim());

        using HttpResponseMessage response = await SharedHttp.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            return null;

        string body = await response.Content.ReadAsStringAsync(ct);
        return NormalizeGeneratedSummary(ExtractOpenAiResponseText(body));
    }

    private async Task<string?> RequestAnthropicConversationSummaryAsync(string prompt, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(AnthropicApiKey))
            return null;

        string model = string.IsNullOrWhiteSpace(AnthropicModel) ? "claude-sonnet-4-5" : AnthropicModel.Trim();
        var payload = new JsonObject
        {
            ["model"] = model,
            ["max_tokens"] = Math.Clamp(MaxTokens / 2, 256, 2048),
            ["system"] = "Return only the compacted conversation summary. Do not call tools.",
            ["messages"] = new JsonArray
            {
                new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = prompt
                }
            }
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages")
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
        };
        request.Headers.Add("x-api-key", AnthropicApiKey.Trim());
        request.Headers.Add("anthropic-version", "2023-06-01");

        using HttpResponseMessage response = await SharedHttp.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            return null;

        string body = await response.Content.ReadAsStringAsync(ct);
        return NormalizeGeneratedSummary(ExtractAnthropicResponseText(body));
    }

    private async Task<string?> RequestOpenAiCompatibleConversationSummaryAsync(string baseUrl, string model, Action<HttpRequestMessage> configureAuth, string prompt, CancellationToken ct)
    {
        var payload = new JsonObject
        {
            ["model"] = model,
            ["messages"] = new JsonArray
            {
                new JsonObject
                {
                    ["role"] = "system",
                    ["content"] = "Return only the compacted conversation summary. Do not call tools."
                },
                new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = prompt
                }
            },
            ["max_tokens"] = Math.Clamp(MaxTokens / 2, 256, 2048)
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, baseUrl.TrimEnd('/') + "/chat/completions")
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
        };
        configureAuth(request);

        using HttpResponseMessage response = await SharedHttp.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            return null;

        string body = await response.Content.ReadAsStringAsync(ct);
        return NormalizeGeneratedSummary(ExtractOpenAiCompatibleResponseText(body));
    }

    private static string NormalizeGeneratedSummary(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        return StripProtocolMarkers(text)
            .Replace("\r", string.Empty, StringComparison.Ordinal)
            .Trim();
    }

    private void CaptureContextSummary(ChatMessage assistantMsg)
    {
        string content = assistantMsg.Content;
        int start = content.IndexOf(ContextSummaryStartMarker, StringComparison.Ordinal);
        if (start < 0)
            return;

        int bodyStart = start + ContextSummaryStartMarker.Length;
        int end = content.IndexOf(ContextSummaryEndMarker, bodyStart, StringComparison.Ordinal);
        if (end < 0)
            return;

        string summary = content[bodyStart..end].Trim();
        if (!string.IsNullOrWhiteSpace(summary))
            _conversationSummary = summary;

        assistantMsg.Content = (content[..start] + content[(end + ContextSummaryEndMarker.Length)..]).Trim();
    }

    private static bool TryStripDoneMarker(ChatMessage assistantMsg)
    {
        int markerIndex = assistantMsg.Content.IndexOf(AssistantDoneMarker, StringComparison.Ordinal);
        if (markerIndex < 0)
            return false;

        assistantMsg.Content = (assistantMsg.Content[..markerIndex]
            + assistantMsg.Content[(markerIndex + AssistantDoneMarker.Length)..]).Trim();
        return true;
    }

    /// <summary>
    /// Checks for and strips the <see cref="AssistantContinueMarker"/> from the assistant message.
    /// Returns <c>true</c> if the marker was present, indicating the model is explicitly requesting
    /// another turn to continue working on the current task.
    /// </summary>
    private static bool TryStripContinueMarker(ChatMessage assistantMsg)
    {
        int markerIndex = assistantMsg.Content.IndexOf(AssistantContinueMarker, StringComparison.Ordinal);
        if (markerIndex < 0)
            return false;

        assistantMsg.Content = (assistantMsg.Content[..markerIndex]
            + assistantMsg.Content[(markerIndex + AssistantContinueMarker.Length)..]).Trim();
        return true;
    }

    private static bool IndicatesRemainingWork(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return false;

        string c = content.ToLowerInvariant();

        string[] unfinishedSignals =
        [
            "next i'll",
            "next i will",
            "i'll now",
            "i will now",
            "then i'll",
            "then i will",
            "still need to",
            "remaining work",
            "not finished",
            "not done",
            "i can now finish it by",
            "i can finish it by",
            "i still need to",
            "task not executed",
            "not executed",
            "requires a fresh attempt",
            "requires fresh attempt",
            "requires retry",
            "needs retry",
            "retry the full workflow",
            "re-run the",
            "recommend retrying",
            "no scene alterations were made",
            "no changes were made",
            "unable to complete",
            "couldn't complete",
            "incomplete",
        ];

        for (int i = 0; i < unfinishedSignals.Length; i++)
        {
            if (c.Contains(unfinishedSignals[i], StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static int EstimateTokens(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return 0;

        return Math.Max(1, text.Length / 4);
    }

    private static string StripProtocolMarkers(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return string.Empty;

        string cleaned = content.Replace(AssistantDoneMarker, string.Empty, StringComparison.Ordinal);
        cleaned = cleaned.Replace(AssistantContinueMarker, string.Empty, StringComparison.Ordinal);
        cleaned = cleaned.Replace(ContextSummaryStartMarker, string.Empty, StringComparison.Ordinal);
        cleaned = cleaned.Replace(ContextSummaryEndMarker, string.Empty, StringComparison.Ordinal);
        return cleaned.Trim();
    }
}
