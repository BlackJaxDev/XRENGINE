namespace XREngine.Rendering.Vulkan;

internal sealed class VulkanPrePushDataCallback
{
    public bool ShouldPush { get; set; } = true;
    public bool AllowPostPushCallback { get; set; } = true;
}
