using XREngine.Rendering.DLSS;

namespace XREngine.Rendering.Vulkan;

internal sealed class VulkanDlssVendorUpscaleSession(
    NvidiaDlssManager.Native.NativeVulkanSession native) :
    IRuntimeVendorUpscaleSession
{
    public NvidiaDlssManager.Native.NativeVulkanSession Native { get; } = native;
    public void ResetResources() => Native.ResetResources();
    public void Dispose() => Native.Dispose();
}
