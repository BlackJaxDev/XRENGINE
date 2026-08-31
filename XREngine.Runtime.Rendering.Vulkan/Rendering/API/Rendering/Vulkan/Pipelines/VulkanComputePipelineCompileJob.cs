using System.Threading.Tasks;

namespace XREngine.Rendering.Vulkan;

internal sealed class VulkanComputePipelineCompileJob(
    VulkanComputePipelineBuildRequest request,
    Task<VulkanComputePipelineCompileResult> task,
    Action promoteToForeground)
{
    public VulkanComputePipelineBuildRequest Request { get; } = request;
    public Task<VulkanComputePipelineCompileResult> Task { get; } = task;
    public void PromoteToForeground() => promoteToForeground();
}
