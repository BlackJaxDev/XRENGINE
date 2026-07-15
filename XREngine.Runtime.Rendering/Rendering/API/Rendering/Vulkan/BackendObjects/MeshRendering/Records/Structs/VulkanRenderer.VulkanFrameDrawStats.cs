namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    internal readonly record struct VulkanFrameDrawStats(int DrawCalls, int MultiDrawCalls, int TrianglesRendered);
}