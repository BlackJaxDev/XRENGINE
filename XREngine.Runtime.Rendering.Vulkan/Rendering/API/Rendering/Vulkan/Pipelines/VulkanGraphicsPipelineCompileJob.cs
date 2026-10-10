using System.Diagnostics;
using System.Threading.Tasks;

namespace XREngine.Rendering.Vulkan;

internal sealed class VulkanGraphicsPipelineCompileJob(
    VulkanGraphicsPipelineBuildRequest request,
    Task<VulkanGraphicsPipelineCompileResult> task,
    Action promoteToForeground,
    VulkanProgramInterfaceEntry interfaceEntry)
{
    public VulkanGraphicsPipelineBuildRequest Request { get; } = request;
    public Task<VulkanGraphicsPipelineCompileResult> Task { get; } = task;
    public Task PublicationTask { get; set; } = global::System.Threading.Tasks.Task.CompletedTask;
    public long QueuedTimestamp { get; } = Stopwatch.GetTimestamp();
    public int WatchdogState;
    private VulkanProgramInterfaceEntry? _interfaceEntry = interfaceEntry;

    public void PromoteToForeground()
        => promoteToForeground();

    /// <summary>Releases the interface after every terminal compile result path.</summary>
    internal void ReleaseInterface()
    {
        VulkanProgramInterfaceEntry? entry =
            global::System.Threading.Interlocked.Exchange(ref _interfaceEntry, null);
        if (entry is not null)
            entry.Context.Resources.ProgramInterfaces.Release(entry);
    }
}
