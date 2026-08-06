namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    private VulkanRenderGraphCompiler _renderGraphCompiler => _framePlanner.Compiler;
    private VulkanFrameOperationScheduler _frameOperationScheduler => _framePlanner.FrameScheduler;
}
