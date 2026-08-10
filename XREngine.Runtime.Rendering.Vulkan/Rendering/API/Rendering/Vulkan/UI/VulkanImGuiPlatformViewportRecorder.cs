using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Renderer-free command adapter for detached ImGui platform windows. The
/// platform window supplies only frozen native handles for its current image.
/// </summary>
internal sealed class VulkanImGuiPlatformViewportRecorder
{
    private readonly VulkanImGuiOverlayCommandRecorder _overlayRecorder = new();

    internal bool TryRecord(
        VulkanTrackedCommandEncoder encoder,
        VulkanFrameTelemetry telemetry,
        VulkanImGuiDrawBufferResources drawBuffers,
        in VulkanImGuiOverlayRecordingInput input,
        out CommandBuffer commandBuffer)
        => _overlayRecorder.TryRecord(
            encoder,
            telemetry,
            drawBuffers,
            in input,
            out commandBuffer);
}
