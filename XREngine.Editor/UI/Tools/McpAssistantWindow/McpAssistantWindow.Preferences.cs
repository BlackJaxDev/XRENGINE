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
    // ── Preferences Sync ─────────────────────────────────────────────────

    private void RefreshMcpEndpointFromPreferences()
    {
        int port = Math.Clamp(Engine.EditorPreferences?.McpServerPort ?? 5467, 1, 65535);
        _mcpServerUrl = $"http://localhost:{port}/mcp/";
        _mcpAuthToken = Engine.EditorPreferences?.McpServerAuthToken ?? string.Empty;
    }
}
