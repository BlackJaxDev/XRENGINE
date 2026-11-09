using XREngine.Rendering.VideoStreaming;
using XREngine.Rendering.VideoStreaming.Interfaces;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Exposes streaming video frame uploads on the Vulkan backend.
/// </summary>
/// <remarks>
/// The uploader resolves texture wrappers through this renderer's backend object
/// registry, so it belongs to the current logical-device generation.
/// </remarks>
public sealed partial class VulkanRenderer : IVideoFrameGpuActionsBackendCapability
{
    IVideoFrameGpuActions IVideoFrameGpuActionsBackendCapability.CreateVideoFrameGpuActions()
        => new VulkanVideoFrameGpuActions(new VulkanVideoFrameUploadContext(_resourceRuntime.BackendObjects));
}
