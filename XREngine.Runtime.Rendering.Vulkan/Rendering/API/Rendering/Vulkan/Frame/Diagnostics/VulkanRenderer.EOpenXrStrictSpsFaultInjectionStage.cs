namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    internal enum EOpenXrStrictSpsFaultInjectionStage : byte
    {
        None = 0,
        Recording,
        LifetimeValidation,
        Submit,
    }
}
