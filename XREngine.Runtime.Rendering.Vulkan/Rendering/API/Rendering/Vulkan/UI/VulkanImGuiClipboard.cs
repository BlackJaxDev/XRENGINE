using ImGuiNET;
using XREngine.Rendering;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Installs process-stable clipboard callbacks for Vulkan ImGui contexts.
/// </summary>
internal static class VulkanImGuiClipboard
{
    internal static unsafe void InstallCallbacks()
    {
        if (!OperatingSystem.IsWindows())
            return;

        ImGuiPlatformIOPtr platformIO = ImGuiNative.igGetPlatformIO();
        platformIO.Platform_GetClipboardTextFn = RendererNativeCallbackBridge.GetClipboardTextCallbackPointer;
        platformIO.Platform_SetClipboardTextFn = RendererNativeCallbackBridge.SetClipboardTextCallbackPointer;
    }
}
