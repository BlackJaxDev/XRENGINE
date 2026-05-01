using XREngine.Rendering;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    internal void RecordTextureUploadProgress(in TextureUploadTelemetry telemetry)
    {
        // Hook reserved for the Vulkan image uploader. OpenGL owns the first runtime
        // implementation, but the shared telemetry contract above the backend is now
        // renderer-neutral.
    }

    internal bool TryScheduleTextureResidencyTransition(
        in TextureResidencyTelemetry residency,
        in TextureUploadTelemetry upload)
    {
        // TODO(Vulkan texture streaming): route staged image uploads, sparse image
        // residency, and generation-gated cancellation through the shared scheduler.
        return false;
    }
}
