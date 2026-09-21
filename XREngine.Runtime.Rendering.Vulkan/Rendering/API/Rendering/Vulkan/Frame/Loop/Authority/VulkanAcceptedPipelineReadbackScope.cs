using System.Threading;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Retains an accepted planner-readback authority while an external consumer
/// inspects resources from the corresponding submitted viewport generation.
/// </summary>
internal sealed class VulkanAcceptedPipelineReadbackScope(
    VulkanFrameLoop owner,
    IDisposable innerScope) : IDisposable
{
    private VulkanFrameLoop? _owner = owner;
    private IDisposable? _innerScope = innerScope;

    public void Dispose()
    {
        VulkanFrameLoop? owner = Interlocked.Exchange(ref _owner, null);
        IDisposable? innerScope = Interlocked.Exchange(ref _innerScope, null);
        try
        {
            innerScope?.Dispose();
        }
        finally
        {
            owner?.ExitAcceptedPipelineReadbackScope();
        }
    }
}
