using ImGuiNET;
using XREngine.Rendering;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Installs process-stable clipboard callbacks for Vulkan ImGui contexts.
/// </summary>
internal static class VulkanImGuiClipboard
{
    internal static void InstallCallbacks(ImGuiIOPtr io)
    {
        if (!OperatingSystem.IsWindows())
            return;

        io.GetClipboardTextFn = RendererNativeCallbackBridge.GetClipboardTextCallbackPointer;
        io.SetClipboardTextFn = RendererNativeCallbackBridge.SetClipboardTextCallbackPointer;
    }
}
