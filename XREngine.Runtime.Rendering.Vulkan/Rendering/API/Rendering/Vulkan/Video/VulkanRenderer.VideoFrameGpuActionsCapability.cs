using XREngine.Rendering.VideoStreaming;
using XREngine.Rendering.VideoStreaming.Interfaces;

namespace XREngine.Rendering.Vulkan;

public partial class VulkanRenderer : IVideoFrameGpuActionsBackendCapability
{
    IVideoFrameGpuActions IVideoFrameGpuActionsBackendCapability.CreateVideoFrameGpuActions()
        => new VulkanVideoFrameGpuActions(new VulkanVideoFrameUploadContext(this));
}
