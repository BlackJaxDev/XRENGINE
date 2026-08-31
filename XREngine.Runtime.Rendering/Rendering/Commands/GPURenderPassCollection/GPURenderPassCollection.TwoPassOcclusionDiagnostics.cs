namespace XREngine.Rendering.Commands;

public sealed partial class GPURenderPassCollection
{
    private GpuHiZTwoPassDiagnosticDescriptor _completedTwoPassDiagnostic;
    private bool _completedTwoPassDiagnosticValid;

    /// <summary>
    /// Gets the GPU-owned keep-set references only when this pass completed a
    /// current-depth two-pass Hi-Z execution for <paramref name="logicalEngineFrameId"/>.
    /// This method neither maps nor reads GPU memory.
    /// </summary>
    public bool TryGetCompletedTwoPassDiagnostic(
        ulong logicalEngineFrameId,
        out GpuHiZTwoPassDiagnosticDescriptor diagnostic)
    {
        diagnostic = default;
        if (!_completedTwoPassDiagnosticValid ||
            !_completedTwoPassDiagnostic.TwoPassExecuted ||
            _completedTwoPassDiagnostic.LogicalEngineFrameId != logicalEngineFrameId)
        {
            return false;
        }

        diagnostic = _completedTwoPassDiagnostic;
        return true;
    }

    /// <summary>
    /// Gets the current logical frame's submitted GPU visibility stream. A
    /// first-frame or camera-cut bypass reports <c>TwoPassExecuted=false</c>
    /// and leaves early/candidate-only streams absent instead of reusing a
    /// previous frame's two-pass result.
    /// </summary>
    public bool TryGetVisibilityDiagnostic(
        ulong logicalEngineFrameId,
        out GpuHiZTwoPassDiagnosticDescriptor diagnostic)
    {
        diagnostic = default;
        if (!_completedTwoPassDiagnosticValid ||
            _completedTwoPassDiagnostic.LogicalEngineFrameId != logicalEngineFrameId)
        {
            return false;
        }

        diagnostic = _completedTwoPassDiagnostic;
        return true;
    }

    private void StampCompletedGpuHiZTwoPassDiagnostic(GPUScene scene, uint candidateUpperBound,
        in GpuHiZTemporalInvalidation invalidation, TimeSpan cpuElapsed)
    {
        if (_twoPassPhaseOneCommandBuffer is null ||
            _twoPassPhaseOneCountBuffer is null ||
            _culledSceneToRenderBuffer is null ||
            _culledCountBuffer is null ||
            _twoPassCandidateCountBuffer is null ||
            _twoPassVisibilityBuffer is null)
        {
            _completedTwoPassDiagnosticValid = false;
            return;
        }

        _completedTwoPassDiagnostic = new GpuHiZTwoPassDiagnosticDescriptor(
            RuntimeEngine.Rendering.State.RenderFrameId,
            MeshSubmissionStrategy,
            TwoPassExecuted: true,
            candidateUpperBound,
            _twoPassPhaseOneCommandBuffer,
            _twoPassPhaseOneCountBuffer,
            _culledSceneToRenderBuffer,
            _culledCountBuffer,
            _twoPassCandidateCountBuffer,
            scene.CullControlBuffer,
            _twoPassVisibilityBuffer)
        {
            TemporalInvalidated = invalidation.Invalidated,
            CameraCut = invalidation.CameraCut,
            ProjectionDiscontinuity = invalidation.ProjectionDiscontinuity,
            UnsafeSceneRevision = invalidation.UnsafeSceneRevision,
            OcclusionCpuMilliseconds = cpuElapsed.TotalMilliseconds,
        };
        _completedTwoPassDiagnosticValid = true;
    }

    private void StampCompletedSinglePassVisibilityDiagnostic(GPUScene scene)
    {
        if (_culledSceneToRenderBuffer is null || _culledCountBuffer is null)
        {
            _completedTwoPassDiagnosticValid = false;
            return;
        }

        _completedTwoPassDiagnostic = new GpuHiZTwoPassDiagnosticDescriptor(
            RuntimeEngine.Rendering.State.RenderFrameId,
            MeshSubmissionStrategy,
            TwoPassExecuted: false,
            CandidateUpperBound: 0u,
            PhaseOneDrawIds: null,
            PhaseOneCount: null,
            _culledSceneToRenderBuffer,
            _culledCountBuffer,
            CandidateCount: null,
            scene.CullControlBuffer,
            VisibilityHistory: _twoPassVisibilityBuffer);
        _completedTwoPassDiagnosticValid = true;
    }
}
