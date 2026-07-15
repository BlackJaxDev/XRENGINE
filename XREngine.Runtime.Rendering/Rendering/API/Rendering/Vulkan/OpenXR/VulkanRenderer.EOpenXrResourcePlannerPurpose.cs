namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    internal enum EOpenXrResourcePlannerPurpose : byte
    {
        Eye,
        Mirror,
        Publish,
        EyePrewarm,
        MirrorPrewarm,
    }
}
