using XREngine.Rendering.DLSS;

namespace XREngine.Rendering.Vulkan;

internal sealed class VulkanFrameGenerationVendorUpscaleSession(
    NvidiaDlssManager.Native.NativeFrameGenerationSession native) :
    IRuntimeVendorUpscaleSession
{
    public NvidiaDlssManager.Native.NativeFrameGenerationSession Native { get; } = native;

    public void ResetResources()
    {
    }

    public void Dispose() => Native.Dispose();
}
