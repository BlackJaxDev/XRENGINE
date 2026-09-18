namespace XREngine.Rendering.Vulkan;

internal enum VulkanProgramLinkReadiness : byte
{
    Pending,
    Ready,
    Failed,
}