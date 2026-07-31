using XREngine.Scene;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    private readonly object _forwardLightingBindingSnapshotCacheSync = new();
    private readonly Dictionary<ForwardLightingBindingSnapshotCacheKey, ComputeDispatchSnapshot>
        _forwardLightingBindingSnapshots = [];
    private ulong _forwardLightingBindingSnapshotFrame;

    /// <summary>
    /// Captures the expensive world/view lighting bindings once per exact pass
    /// scope, then replays the immutable values into each material capture.
    /// GPU buffer uploads were already frame-gated; this also removes redundant
    /// light traversal, shadow-atlas resolution, and uniform-array construction.
    /// </summary>
    private void SetForwardLightingUniformsCached(
        Lights3DCollection lights,
        XRRenderProgram programData,
        VkRenderProgram backendProgram)
    {
        ulong frameId = RuntimeRenderingHostServices.FrameTiming.CurrentRenderFrameId;
        if (frameId == 0)
        {
            lights.SetForwardLightingUniforms(programData);
            return;
        }

        XRRenderPipelineInstance.RenderingState? renderingState =
            RuntimeEngine.Rendering.State.RenderingPipelineState;
        var renderArea = RuntimeEngine.Rendering.State.RenderArea;
        ForwardLightingBindingSnapshotCacheKey key = new(
            lights,
            RuntimeEngine.Rendering.State.CurrentRenderingPipeline,
            RuntimeEngine.Rendering.State.RenderingCamera,
            RuntimeEngine.Rendering.State.RenderingStereoRightEyeCamera,
            RuntimeEngine.Rendering.State.RenderingWorld,
            ResolveCurrentFrameOpDrawTarget(),
            RuntimeEngine.Rendering.State.CurrentRenderGraphPassIndex,
            renderArea.X,
            renderArea.Y,
            renderArea.Width,
            renderArea.Height,
            RuntimeEngine.Rendering.State.IsStereoPass,
            renderingState?.UseUnjitteredProjection ?? false);

        if (!TryGetForwardLightingBindingSnapshot(frameId, key, out ComputeDispatchSnapshot? snapshot))
        {
            using (VkRenderProgram.BindingUpdateScope lightingCapture = backendProgram.BeginBindingUpdate())
            {
                backendProgram.ClearBindings();
                lights.SetForwardLightingUniforms(programData);
                snapshot = backendProgram.CaptureComputeSnapshot();
            }

            snapshot = PublishForwardLightingBindingSnapshot(frameId, key, snapshot);
        }

        backendProgram.MergeBindingSnapshot(snapshot!);
    }

    private bool TryGetForwardLightingBindingSnapshot(
        ulong frameId,
        in ForwardLightingBindingSnapshotCacheKey key,
        out ComputeDispatchSnapshot? snapshot)
    {
        lock (_forwardLightingBindingSnapshotCacheSync)
        {
            PrepareForwardLightingBindingSnapshotCacheNoLock(frameId);
            return _forwardLightingBindingSnapshots.TryGetValue(key, out snapshot);
        }
    }

    private ComputeDispatchSnapshot PublishForwardLightingBindingSnapshot(
        ulong frameId,
        in ForwardLightingBindingSnapshotCacheKey key,
        ComputeDispatchSnapshot snapshot)
    {
        lock (_forwardLightingBindingSnapshotCacheSync)
        {
            PrepareForwardLightingBindingSnapshotCacheNoLock(frameId);
            if (_forwardLightingBindingSnapshots.TryGetValue(
                    key,
                    out ComputeDispatchSnapshot? existing))
            {
                return existing;
            }

            _forwardLightingBindingSnapshots.Add(key, snapshot);
            return snapshot;
        }
    }

    private void PrepareForwardLightingBindingSnapshotCacheNoLock(ulong frameId)
    {
        if (_forwardLightingBindingSnapshotFrame == frameId)
            return;

        _forwardLightingBindingSnapshotFrame = frameId;
        _forwardLightingBindingSnapshots.Clear();
    }
}
