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
    // ── MCP Local Proxy Helpers ──────────────────────────────────────────

    /// <summary>
    /// Calls the local MCP server's <c>tools/list</c> method and returns the tools array.
    /// Returns null on failure.
    /// </summary>
    private async Task<JsonArray?> FetchMcpToolListAsync(CancellationToken ct)
    {
        string url = _mcpServerUrl.Trim();
        var payload = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = "fetch-tools-list",
            ["method"] = "tools/list"
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
        };
        if (!string.IsNullOrWhiteSpace(_mcpAuthToken))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _mcpAuthToken.Trim());

        using HttpResponseMessage response = await SharedHttp.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
            return null;

        string body = await response.Content.ReadAsStringAsync(ct);
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;

        if (!root.TryGetProperty("result", out JsonElement result))
            return null;

        if (result.TryGetProperty("tools", out JsonElement toolsEl) && toolsEl.ValueKind == JsonValueKind.Array)
            return JsonSerializer.Deserialize<JsonArray>(toolsEl.GetRawText());

        return null;
    }

    /// <summary>
    /// Converts MCP tool definitions to OpenAI function tool format.
    /// </summary>
    private static JsonArray ConvertMcpToolsToOpenAiFunctions(JsonArray mcpTools)
    {
        var result = new JsonArray();
        foreach (JsonNode? tool in mcpTools)
        {
            if (tool is not JsonObject toolObj)
                continue;

            string? name = toolObj["name"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(name))
                continue;

            var fn = new JsonObject
            {
                ["type"] = "function",
                ["name"] = name
            };

            if (toolObj["description"] is JsonNode descNode)
                fn["description"] = descNode.GetValue<string>();

            // MCP uses "inputSchema", OpenAI uses "parameters"
            if (toolObj["inputSchema"] is JsonObject inputSchema)
                fn["parameters"] = JsonNode.Parse(inputSchema.ToJsonString());
            else
                fn["parameters"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() };

            result.Add(fn);
        }
        return result;
    }

    /// <summary>
    /// Converts MCP tool definitions to Anthropic tool format.
    /// </summary>
    private static JsonArray ConvertMcpToolsToAnthropicTools(JsonArray mcpTools)
    {
        var result = new JsonArray();
        foreach (JsonNode? tool in mcpTools)
        {
            if (tool is not JsonObject toolObj)
                continue;

            string? name = toolObj["name"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(name))
                continue;

            var anthropicTool = new JsonObject
            {
                ["name"] = name
            };

            if (toolObj["description"] is JsonNode descNode)
                anthropicTool["description"] = descNode.GetValue<string>();

            // MCP uses "inputSchema", Anthropic uses "input_schema"
            if (toolObj["inputSchema"] is JsonObject inputSchema)
                anthropicTool["input_schema"] = JsonNode.Parse(inputSchema.ToJsonString());
            else
                anthropicTool["input_schema"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() };

            result.Add(anthropicTool);
        }
        return result;
    }

    private static JsonArray ConvertOpenAiFunctionsToAnthropicTools(JsonArray openAiTools)
    {
        var result = new JsonArray();
        foreach (JsonNode? tool in openAiTools)
        {
            if (tool is not JsonObject toolObj)
                continue;

            string? type = toolObj["type"]?.GetValue<string>();
            if (!string.Equals(type, "function", StringComparison.OrdinalIgnoreCase))
                continue;

            string? name = toolObj["name"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(name))
                continue;

            var anthropicTool = new JsonObject
            {
                ["name"] = name
            };

            if (toolObj["description"] is JsonNode descNode)
                anthropicTool["description"] = descNode.GetValue<string>();

            if (toolObj["parameters"] is JsonObject schema)
                anthropicTool["input_schema"] = JsonNode.Parse(schema.ToJsonString());
            else
                anthropicTool["input_schema"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() };

            result.Add(anthropicTool);
        }

        return result;
    }
}
