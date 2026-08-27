using System.Diagnostics;
using System.Threading.Tasks;

namespace XREngine.Rendering.Vulkan;

internal sealed class VulkanGraphicsPipelineCompileJob(
    VulkanGraphicsPipelineBuildRequest request,
    Task<VulkanGraphicsPipelineCompileResult> task,
    Action promoteToForeground)
{
    public VulkanGraphicsPipelineBuildRequest Request { get; } = request;
    public Task<VulkanGraphicsPipelineCompileResult> Task { get; } = task;
    public Task PublicationTask { get; set; } = global::System.Threading.Tasks.Task.CompletedTask;
    public long QueuedTimestamp { get; } = Stopwatch.GetTimestamp();
    public int WatchdogState;

    public void PromoteToForeground()
        => promoteToForeground();
}
