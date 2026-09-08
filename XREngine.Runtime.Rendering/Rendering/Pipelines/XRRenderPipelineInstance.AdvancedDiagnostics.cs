namespace XREngine.Rendering;

using XREngine.Rendering.Commands;

public partial class XRRenderPipelineInstance
{
    private const int AdvancedDiagnosticPhaseCount = 3;
    private readonly object _advancedProfileDiagnosticLock = new();
    private readonly AdvancedProfileStageDiagnostic[] _advancedProfileStageDiagnostics =
        new AdvancedProfileStageDiagnostic[
            Enum.GetValues<EAdvancedRenderStage>().Length * AdvancedDiagnosticPhaseCount];
    private readonly AdvancedProfileEyeDiagnostic[] _advancedProfileEyes = new AdvancedProfileEyeDiagnostic[RenderFrameViewSet.MaxViewCount];
    private int _advancedProfileEyeCount;
    private ulong _advancedProfileViewFrameId;

    internal void RecordAdvancedProfileStageDiagnostic(
        EAdvancedRenderStage stage,
        EAdvancedVisibilityStageBackendPhase phase,
        EAdvancedProfileStageDiagnosticState state,
        string? reason)
    {
        int index = checked((int)stage * AdvancedDiagnosticPhaseCount + (int)phase);
        if ((uint)index >= (uint)_advancedProfileStageDiagnostics.Length)
            return;

        AdvancedRenderPipelineOutputBinding binding = AdvancedOutputBinding;
        AdvancedProfileStageDiagnostic observation = new(
            true,
            RuntimeEngine.Rendering.State.RenderFrameId,
            stage,
            phase,
            state,
            ResourceGeneration,
            binding.Request.OutputId,
            reason);
        lock (_advancedProfileDiagnosticLock)
        {
            // A prerequisite rejection uses Complete even for a split stage.
            // Retire prior-frame phase observations together so a recovered
            // LateCompute/LateRaster pair cannot retain its warm-up rejection.
            int firstPhase = (int)stage * AdvancedDiagnosticPhaseCount;
            for (int phaseIndex = firstPhase; phaseIndex < firstPhase + AdvancedDiagnosticPhaseCount; phaseIndex++)
                if (_advancedProfileStageDiagnostics[phaseIndex].FrameId != observation.FrameId)
                    _advancedProfileStageDiagnostics[phaseIndex] = default;
            _advancedProfileStageDiagnostics[index] = observation;
            if (stage == EAdvancedRenderStage.VisibilityPreparation)
            {
                _advancedProfileEyeCount = 0;
                _advancedProfileViewFrameId = observation.FrameId;
                if (state == EAdvancedProfileStageDiagnosticState.BackendEnqueueAccepted &&
                    RenderState.FrameViewSet is RenderFrameViewSet views)
                {
                    // Collection may have published a mono package before XR
                    // locate. Report the exact frozen views admitted for authoring.
                    for (int viewIndex = 0; viewIndex < views.ViewCount; viewIndex++)
                    {
                        BackendReadyCanonicalViewRecord view = BackendReadyFramePackage.CreateCanonicalViewRecord(
                            views.GetView(viewIndex), observation.FrameId);
                        _advancedProfileEyes[viewIndex] = new AdvancedProfileEyeDiagnostic(
                            observation.FrameId, observation.ResourceGeneration, observation.OutputId,
                            view.ViewId, view.OutputLayer, view.HistoryKey, view.ViewMaskLo, view.ViewMaskHi,
                            view.Flags, view.ViewportWidth, view.ViewportHeight);
                    }
                    _advancedProfileEyeCount = views.ViewCount;
                }
            }
        }
    }

    /// <summary>
    /// Copies the bounded latest stage observations for editor diagnostics.
    /// This diagnostic-only allocation never runs from frame authoring.
    /// </summary>
    public AdvancedProfileStageDiagnostic[] CaptureAdvancedProfileStageDiagnostics()
    {
        lock (_advancedProfileDiagnosticLock)
            return [.. _advancedProfileStageDiagnostics];
    }

    /// <summary>
    /// Captures stage observations and the frozen views last admitted by native
    /// preparation. Collection-time views cannot describe a later XR locate.
    /// The diagnostic copy performs no live camera or view-set query.
    /// </summary>
    public AdvancedProfileDiagnosticsSnapshot CaptureAdvancedProfileDiagnostics()
    {
        lock (_advancedProfileDiagnosticLock)
        {
            AdvancedRenderPipelineOutputBinding binding = AdvancedOutputBinding;
            return new AdvancedProfileDiagnosticsSnapshot(
                _advancedProfileViewFrameId,
                ResourceGeneration,
                binding.Request.OutputId,
                [.. _advancedProfileStageDiagnostics],
                _advancedProfileEyes.AsSpan(0, _advancedProfileEyeCount).ToArray());
        }
    }
}
