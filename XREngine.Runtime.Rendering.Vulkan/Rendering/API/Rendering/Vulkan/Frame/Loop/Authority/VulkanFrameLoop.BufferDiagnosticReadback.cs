using System.Threading;

namespace XREngine.Rendering.Vulkan;

/// <summary>Owns accepted-frame authority for synchronous, externally requested buffer diagnostics.</summary>
internal sealed partial class VulkanFrameLoop
{
    private int _acceptedPipelineReadbackScopeDepth;

    private IDisposable TrackAcceptedPipelineReadbackScope(IDisposable scope)
    {
        Interlocked.Increment(ref _acceptedPipelineReadbackScopeDepth);
        return new VulkanAcceptedPipelineReadbackScope(this, scope);
    }

    internal void ExitAcceptedPipelineReadbackScope()
        => Interlocked.Decrement(ref _acceptedPipelineReadbackScopeDepth);

    /// <summary>
    /// Reads a buffer only while the caller has installed an accepted viewport planner scope.
    /// The synchronous copy is a cold diagnostic path; normal rendering never uses it.
    /// </summary>
    internal bool TryReadAcceptedPipelineBufferBytesForDiagnostics(
        XRDataBuffer sourceBuffer,
        uint sourceByteOffset,
        Span<byte> destination,
        out string reason)
    {
        if (!RuntimeEngine.IsRenderThread ||
            Volatile.Read(ref _acceptedPipelineReadbackScopeDepth) <= 0 ||
            !_resourcePlannerSessions.TryGetScopedFrameOpContext(out FrameOpContext context) ||
            context.PipelineInstance is null ||
            context.ResourceRegistry is null)
        {
            reason = "<no-active-accepted-pipeline-readback-scope>";
            return false;
        }

        VulkanBackendObjectContext? backendContext = _resourceRuntime.BackendObjectContext;
        if (backendContext is null)
        {
            reason = "<vulkan-backend-object-context-unavailable>";
            return false;
        }

        return TryReadBufferBytesForDiagnostics(
            backendContext,
            sourceBuffer,
            sourceByteOffset,
            destination,
            out reason);
    }
}
