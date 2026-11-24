using XREngine.Rendering.VideoStreaming;
using XREngine.Rendering.VideoStreaming.Interfaces;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Exposes streaming video frame uploads on the Vulkan backend.
/// </summary>
/// <remarks>
/// The uploader resolves texture wrappers and the synchronous upload policy through this
/// renderer's resource runtime, so it belongs to the current logical-device generation.
/// </remarks>
public sealed partial class VulkanRenderer : IVideoFrameGpuActionsBackendCapability
{
    IVideoFrameGpuActions IVideoFrameGpuActionsBackendCapability.CreateVideoFrameGpuActions()
        => new VulkanVideoFrameGpuActions(new VulkanVideoFrameUploadContext(_resourceRuntime));
}
