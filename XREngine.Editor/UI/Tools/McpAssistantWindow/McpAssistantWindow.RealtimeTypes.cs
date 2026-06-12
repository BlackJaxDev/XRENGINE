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
    private sealed class PendingFunctionCall
    {
        public string CallId { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public StringBuilder Arguments { get; } = new();
    }

    private sealed class RealtimeFunctionResult
    {
        public bool IsError { get; init; }
        public string FunctionOutput { get; init; } = string.Empty;
        public string ImageDataUrl { get; init; } = string.Empty;
        public string ContextText { get; init; } = string.Empty;
    }

    private sealed class RealtimeScreenshotCapture
    {
        public string Path { get; init; } = string.Empty;
        public string? CameraNodeId { get; init; }
        public string? CameraName { get; init; }
        public int WindowIndex { get; init; }
        public int ViewportIndex { get; init; }
    }
}
