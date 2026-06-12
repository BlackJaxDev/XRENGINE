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
    // ── Tool Call Logging Helpers ─────────────────────────────────────────

    /// <summary>
    /// Appends a human-readable tool call entry to the log: tool name, key arguments, and a result snippet.
    /// </summary>
    private static void AppendToolCallLog(StringBuilder toolLog, string toolName, string argsJson, string result)
    {
        // Friendly tool name: replace underscores with spaces and title-case.
        string friendly = FormatToolName(toolName);

        // Extract a short description of what arguments were passed.
        string argsSummary = SummarizeToolArguments(toolName, argsJson);

        // Truncate the result for display.
        string resultSnippet = SummarizeToolResult(result);

        toolLog.Append($"\n  - {friendly}");
        if (!string.IsNullOrEmpty(argsSummary))
            toolLog.Append($" ({argsSummary})");
        if (!string.IsNullOrEmpty(resultSnippet))
            toolLog.Append($" -> {resultSnippet}");
    }

    /// <summary>
    /// Builds the final assistant content string from model text and an optional trailing status.
    /// Tool call details are displayed separately in the collapsible tool-calls section.
    /// </summary>
    private static string BuildAssistantContent(StringBuilder modelText, StringBuilder toolLog, string? statusSuffix = null)
    {
        var result = new StringBuilder();

        if (modelText.Length > 0)
            result.Append(modelText);

        if (!string.IsNullOrEmpty(statusSuffix))
        {
            if (result.Length > 0)
                result.Append("\n\n");
            result.Append($"[{statusSuffix}]");
        }

        return result.Length > 0 ? result.ToString() : string.Empty;
    }

    private static string FormatToolName(string toolName)
    {
        // "create_scene_node" → "Create scene node"
        string spaced = toolName.Replace('_', ' ');
        return spaced.Length > 0
            ? char.ToUpper(spaced[0]) + spaced[1..]
            : spaced;
    }

    private static string SummarizeToolArguments(string toolName, string argsJson)
    {
        if (string.IsNullOrWhiteSpace(argsJson) || argsJson is "{}" or "null")
            return string.Empty;

        try
        {
            using var doc = JsonDocument.Parse(argsJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return string.Empty;

            // Pick the most meaningful argument to display.
            // Priority: name > node_name > scene_name > component_type > node_id > first string property
            string[] priorityKeys = ["name", "node_name", "scene_name", "component_type", "type", "node_id", "path"];
            foreach (string key in priorityKeys)
            {
                if (root.TryGetProperty(key, out var val) && val.ValueKind == JsonValueKind.String)
                {
                    string? v = val.GetString();
                    if (!string.IsNullOrWhiteSpace(v))
                        return $"{key}: {Truncate(v, 60)}";
                }
            }

            // Fallback: show first string property.
            foreach (var prop in root.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.String)
                {
                    string? v = prop.Value.GetString();
                    if (!string.IsNullOrWhiteSpace(v))
                        return $"{prop.Name}: {Truncate(v, 60)}";
                }
            }
        }
        catch { }

        return string.Empty;
    }

    private static string SummarizeToolResult(string result)
    {
        if (string.IsNullOrWhiteSpace(result))
            return string.Empty;

        if (result.StartsWith("[MCP Error]", StringComparison.Ordinal))
            return Truncate(result, 80);

        // Try to extract a summary message from JSON results.
        ReadOnlySpan<char> trimmed = result.AsSpan().TrimStart();
        if (trimmed.Length > 0 && trimmed[0] == '{')
        {
            try
            {
                using var doc = JsonDocument.Parse(result);
                var root = doc.RootElement;
                // Many MCP results have a top-level message or text.
                foreach (string key in new[] { "message", "text", "summary", "status" })
                {
                    if (root.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String)
                    {
                        string? s = v.GetString();
                        if (!string.IsNullOrWhiteSpace(s))
                            return Truncate(s, 100);
                    }
                }
            }
            catch { }
        }

        string cleaned = StripInlineJsonSuffix(result);
        return Truncate(cleaned, 100);
    }

    /// <summary>
    /// Produces a compact summary of a tool result suitable for the conversation context block.
    /// Unlike <see cref="SummarizeToolResult"/> (which is for UI display), this method preserves
    /// IDs (node GUIDs, asset IDs, component IDs) so the model can reference them in subsequent
    /// turns without re-querying.
    /// </summary>
    private static string SummarizeToolResultForContext(string result)
    {
        if (string.IsNullOrWhiteSpace(result))
            return string.Empty;

        if (result.StartsWith("[MCP Error]", StringComparison.Ordinal))
            return Truncate(result, 120);

        // The tool result typically has two parts separated by newline:
        //   Line 1: human-readable message (e.g. "Found 1 nodes matching 'Floor'.")
        //   Line 2+: JSON data (e.g. {"nodes":[{"id":"<guid>","name":"Floor","path":"..."}]})
        // We want to keep the message AND extract compact ID mappings from the JSON data.

        string message = string.Empty;
        string? jsonPart = null;

        int firstNewline = result.IndexOf('\n');
        if (firstNewline > 0)
        {
            message = result[..firstNewline].Trim();
            string remainder = result[(firstNewline + 1)..].Trim();
            if (remainder.Length > 0 && (remainder[0] == '{' || remainder[0] == '['))
                jsonPart = remainder;
        }
        else
        {
            // Single line — might be pure JSON or pure text
            string trimmed = result.Trim();
            if (trimmed.Length > 0 && (trimmed[0] == '{' || trimmed[0] == '['))
                jsonPart = trimmed;
            else
                message = trimmed;
        }

        // If no JSON data, return the message (or truncated raw text)
        if (jsonPart is null)
            return Truncate(message, 200);

        // Extract ID mappings from JSON data
        var idParts = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(jsonPart);
            ExtractIdMappings(doc.RootElement, idParts, depth: 0);
        }
        catch { }

        if (idParts.Count == 0)
            return Truncate(message, 200);

        // Build: "Found 1 nodes matching 'Floor'. IDs: Floor=<guid>"
        string idSection = string.Join(", ", idParts);
        if (string.IsNullOrEmpty(message))
            return Truncate($"IDs: {idSection}", 500);

        return Truncate($"{message} IDs: {idSection}", 500);
    }

    /// <summary>
    /// Recursively extracts name→id mappings from JSON elements for context summaries.
    /// Handles common MCP patterns: arrays of {id, name}, objects with {id}, nested data.
    /// </summary>
    private static void ExtractIdMappings(JsonElement element, List<string> parts, int depth)
    {
        if (depth > 3 || parts.Count >= 20) // Safety limits
            return;

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
            {
                // Look for id/name pairs in this object
                string? id = null;
                string? name = null;
                foreach (string idKey in new[] { "id", "node_id", "asset_id", "component_id" })
                {
                    if (element.TryGetProperty(idKey, out var idEl) && idEl.ValueKind == JsonValueKind.String)
                    {
                        id = idEl.GetString();
                        break;
                    }
                }
                if (element.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String)
                    name = nameEl.GetString();

                if (id is not null)
                {
                    parts.Add(name is not null ? $"{name}={id}" : id);
                    return; // Don't recurse further into this object — we captured it
                }

                // No direct id — recurse into child properties that are arrays/objects
                foreach (var prop in element.EnumerateObject())
                {
                    if (prop.Value.ValueKind is JsonValueKind.Array or JsonValueKind.Object)
                        ExtractIdMappings(prop.Value, parts, depth + 1);
                }
                break;
            }
            case JsonValueKind.Array:
            {
                foreach (var item in element.EnumerateArray())
                    ExtractIdMappings(item, parts, depth + 1);
                break;
            }
        }
    }

    private static string StripInlineJsonSuffix(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        string normalized = text.Trim();

        int objectStart = normalized.IndexOf('{');
        int arrayStart = normalized.IndexOf('[');
        int jsonStart = objectStart switch
        {
            < 0 => arrayStart,
            _ when arrayStart < 0 => objectStart,
            _ => Math.Min(objectStart, arrayStart),
        };

        // If the text is pure JSON, keep original behavior for JSON parsing paths.
        if (jsonStart <= 0)
            return normalized;

        ReadOnlySpan<char> jsonTail = normalized.AsSpan(jsonStart).Trim();
        if (jsonTail.Length == 0)
            return normalized;

        try
        {
            using var _ = JsonDocument.Parse(jsonTail.ToString());
            return normalized[..jsonStart].TrimEnd();
        }
        catch
        {
            return normalized;
        }
    }

    private static string Truncate(string s, int maxLen)
    {
        if (s.Length <= maxLen)
            return s;

        if (maxLen <= 3)
            return s[..maxLen];

        return s[..(maxLen - 3)] + "...";
    }

    private string ResolveProviderModelName(ProviderType provider)
        => provider switch
        {
            ProviderType.Codex when UseRealtimeWebSocket => string.IsNullOrWhiteSpace(OpenAiRealtimeModel) ? "gpt-4o-realtime-preview" : OpenAiRealtimeModel.Trim(),
            ProviderType.Codex => string.IsNullOrWhiteSpace(OpenAiModel) ? "gpt-5-codex" : OpenAiModel.Trim(),
            ProviderType.ClaudeCode => string.IsNullOrWhiteSpace(AnthropicModel) ? "claude-sonnet-4-20250514" : AnthropicModel.Trim(),
            ProviderType.Gemini => string.IsNullOrWhiteSpace(GeminiModel) ? "gemini-2.5-pro" : GeminiModel.Trim(),
            ProviderType.GitHubModels => string.IsNullOrWhiteSpace(GitHubModelsModel) ? "openai/gpt-4.1" : GitHubModelsModel.Trim(),
            _ => "unknown"
        };

    private void LogAiTrace(string message)
    {
        if (!VerboseAiLogging)
            return;

        Debug.Log(ELogCategory.AI, message);
    }

    private void LogAiPromptAttempt(
        ProviderType provider,
        int attempt,
        int maxAutoReprompts,
        string prompt,
        bool isContinueFlow,
        bool requestSelfSummary)
    {
        try
        {
            string model = ResolveProviderModelName(provider);
            int contextWindowTokens = ResolveContextWindowTokens(provider);
            string flow = isContinueFlow ? "ContinuePromptAsync" : "SendPromptAsync";

            LogAiTrace(
                $"""
[MCP Assistant Prompt]
flow={flow}
provider={provider}
model={model}
attempt={attempt}/{maxAutoReprompts}
contextWindowTokens={contextWindowTokens}
requestSelfSummary={requestSelfSummary}
promptLength={prompt.Length}
promptStart
{prompt}
promptEnd
""");
        }
        catch (Exception ex)
        {
            LogAiTrace($"[MCP Assistant Prompt Log Error] {ex}");
        }
    }

    private void LogAiResponseAttempt(
        ProviderType provider,
        int attempt,
        int maxAutoReprompts,
        string response,
        bool isContinueFlow)
    {
        try
        {
            string flow = isContinueFlow ? "ContinuePromptAsync" : "SendPromptAsync";
            bool hasDoneMarker = response?.Contains(AssistantDoneMarker, StringComparison.Ordinal) ?? false;
            bool hasContinueMarker = response?.Contains(AssistantContinueMarker, StringComparison.Ordinal) ?? false;
            string safeResponse = response ?? string.Empty;

            LogAiTrace(
                $"""
[MCP Assistant Response]
flow={flow}
provider={provider}
attempt={attempt}/{maxAutoReprompts}
responseLength={safeResponse.Length}
hasDoneMarker={hasDoneMarker}
hasContinueMarker={hasContinueMarker}
responseStart
{safeResponse}
responseEnd
""");
        }
        catch (Exception ex)
        {
            LogAiTrace($"[MCP Assistant Response Log Error] {ex}");
        }
    }

    /// <summary>
    /// Executes a tool call against the local MCP server's <c>tools/call</c> endpoint.
    /// Returns the result text, or an error message on failure.
}
