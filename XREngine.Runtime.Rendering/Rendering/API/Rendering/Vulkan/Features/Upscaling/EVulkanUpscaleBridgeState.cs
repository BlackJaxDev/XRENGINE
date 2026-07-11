namespace XREngine.Rendering.Vulkan;

public enum EVulkanUpscaleBridgeState
{
    Unsupported,
    Disabled,
    Initializing,
    Ready,
    NeedsRecreate,
    Faulted,
}
