using XREngine.Rendering.Vulkan;

namespace XREngine.Runtime.Bootstrap;

public class UnitTestingVulkanRenderSettings
{
    public EVulkanRenderTargetMode RenderTargetMode { get; set; } = EVulkanRenderTargetMode.Auto;
    public EVulkanPresentationProfile PresentationProfile { get; set; } = EVulkanPresentationProfile.Uncapped;
    public float TargetRefreshHz { get; set; } = 0.0f;
}
