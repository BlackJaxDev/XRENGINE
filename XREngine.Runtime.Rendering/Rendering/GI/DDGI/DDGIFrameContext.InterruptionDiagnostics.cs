namespace XREngine.Rendering.GI.DDGI;

#if !XRE_PUBLISHED
internal sealed partial class DDGIFrameContext
{
    private DDGIInterruptionDiagnosticState? _interruptionDiagnostic;
    private ulong _nextInterruptionDiagnosticToken;

    internal bool TryArmInterruptionDiagnostic(
        XRRenderPipelineInstance pipeline,
        int skipCount,
        out DDGIInterruptionDiagnosticSnapshot? snapshot,
        out string? failure)
    {
        snapshot = null;
        if (skipCount is < 1 or > 120)
        {
            failure = "skip_count must be between 1 and 120.";
            return false;
        }
        if (!RuntimeEngine.IsRenderThread || !ReferenceEquals(RuntimeEngine.Rendering.State.CurrentRenderingPipeline, pipeline))
        {
            failure = "DDGI interruption diagnostics must be armed on the selected pipeline's render thread and scope.";
            return false;
        }

        _interruptionDiagnostic = new DDGIInterruptionDiagnosticState(
            ++_nextInterruptionDiagnosticToken,
            pipeline,
            pipeline.InstanceId,
            pipeline.ResourceGeneration,
            skipCount);
        snapshot = _interruptionDiagnostic.Capture();
        failure = null;
        return true;
    }

    internal bool TryGetInterruptionDiagnosticSnapshot(XRRenderPipelineInstance pipeline, out DDGIInterruptionDiagnosticSnapshot? snapshot)
    {
        if (_interruptionDiagnostic is null || !_interruptionDiagnostic.Matches(pipeline))
        {
            snapshot = null;
            return false;
        }
        snapshot = _interruptionDiagnostic.Capture();
        return true;
    }

    private bool ShouldInterruptAfterVisibility()
    {
        DDGIInterruptionDiagnosticState? diagnostic = _interruptionDiagnostic;
        XRRenderPipelineInstance? pipeline = RuntimeEngine.Rendering.State.CurrentRenderingPipeline;
        if (diagnostic is null || pipeline is null)
            return false;
        if (!diagnostic.Matches(pipeline))
        {
            _interruptionDiagnostic = null;
            return false;
        }
        return diagnostic.ShouldSkip(RuntimeEngine.Rendering.State.RenderFrameId, State.FrameIndex);
    }

    private void RecordInterruptionAbort()
        => _interruptionDiagnostic?.RecordAbort();

    private void RecordAcceptedInterruptionReceipt()
        => _interruptionDiagnostic?.RecordAcceptedReceipt();

    private void RecordInterruptionReceiptFailure()
        => _interruptionDiagnostic?.RecordReceiptFailure();

    private void RecordInterruptionComplete()
        => _interruptionDiagnostic?.RecordComplete();

    private void RecordInterruptionPublication()
        => _interruptionDiagnostic?.RecordPublication(RuntimeEngine.Rendering.State.RenderFrameId, State.FrameIndex);

    private void ClearInterruptionDiagnostic()
        => _interruptionDiagnostic = null;
}
#endif
