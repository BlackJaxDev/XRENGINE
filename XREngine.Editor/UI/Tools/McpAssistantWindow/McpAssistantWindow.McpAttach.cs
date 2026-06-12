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
    private void EnsureMcpServerAutoEnabledForAssistantMessage()
    {
        EditorPreferences? prefs = Engine.EditorPreferences;
        if (prefs is null)
            return;

        RefreshMcpEndpointFromPreferences();

        if (!prefs.McpServerEnabled)
            prefs.McpServerEnabled = true;

        try
        {
            int port = Math.Clamp(prefs.McpServerPort, 1, 65535);
            if (!McpServerHost.Instance.IsRunning)
                McpServerHost.Instance.Start(port);
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"Failed auto-starting MCP server for assistant chat: {ex.Message}");
        }
    }

    private async Task<(bool ok, string reason)> EnsureMcpServerAttachReadyAsync(CancellationToken ct)
    {
        if (!AttachMcpServer)
            return (true, "MCP attachment disabled.");

        string url = _mcpServerUrl.Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out _))
            return (false, "Invalid MCP URL.");

        if (!McpServerHost.Instance.IsRunning)
        {
            EnsureMcpServerAutoEnabledForAssistantMessage();
            if (!McpServerHost.Instance.IsRunning)
                return (false, "MCP server is not running.");
        }

        var payload = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = "mcp-preflight-tools-list",
            ["method"] = "tools/list"
        };

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
            };

            if (!string.IsNullOrWhiteSpace(_mcpAuthToken))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _mcpAuthToken.Trim());

            using HttpResponseMessage response = await SharedHttp.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            string body = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
                return (false, $"HTTP {(int)response.StatusCode}");

            using JsonDocument doc = JsonDocument.Parse(body);
            JsonElement root = doc.RootElement;

            if (root.TryGetProperty("error", out JsonElement err) && err.ValueKind == JsonValueKind.Object)
            {
                string msg = TryExtractEventErrorMessage(root) ?? "tools/list failed";
                return (false, msg);
            }

            if (!root.TryGetProperty("result", out _))
                return (false, "MCP server returned no result.");

            return (true, "ok");
        }
        catch (OperationCanceledException)
        {
            return (false, "MCP preflight timed out.");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private void SetStatus(string text, Vector4 color)
    {
        _status = text;
        _statusColor = color;
    }
}
