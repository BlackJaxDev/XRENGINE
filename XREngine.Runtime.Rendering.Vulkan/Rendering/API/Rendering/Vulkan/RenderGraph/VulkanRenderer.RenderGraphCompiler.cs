namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    private VulkanRenderGraphCompiler _renderGraphCompiler => _renderGraphRuntime.Compiler;
    private VulkanFrameOperationScheduler _frameOperationScheduler => _renderGraphRuntime.FrameScheduler;
}
