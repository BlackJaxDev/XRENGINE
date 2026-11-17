using System;
using System.Text;

namespace XREngine.Rendering;

public sealed partial class AdvancedPreparationExtractor
{
    /// <summary>
    /// True when <see cref="XREngineEnvironmentVariables.AdvancedDeformationSlotDiagnostics"/> is on.
    /// Read once, so the off state costs one static read for each deferred preparation.
    /// </summary>
    private static readonly bool s_deformationSlotDiagnostics =
        XREnvironment.IsEnabled(XREngineEnvironmentVariables.AdvancedDeformationSlotDiagnostics);

    /// <summary>Auxiliary log of the deferral lines. Release sessions also write it.</summary>
    private const string DeformationSlotDiagnosticsLogFileName = "advanced-deformation-slot.log";

    /// <summary>
    /// Writes one line when the deformation frame could not open and the shared preparation
    /// defers: the frame id and render frame, the slot, the failed check
    /// (<see cref="EAdvancedDeformationSlotDeferral"/>), the reuse status and authority, the frame
    /// id that produced the slot and the frame age, the producer fence's timeline target and the
    /// backend's completed value, the active render viewport kind (the preparation consumer that
    /// asked), the requested consumers, and the process totals of every deferral reason.
    /// </summary>
    private void LogDeformationSlotDeferral(ulong frameId, uint slot, EAdvancedPreparationConsumer consumers)
    {
        if (!s_deformationSlotDiagnostics)
            return;

        AdvancedGpuDeformationResources deformation = _gpuDeformation;
        _ = deformation.TryDescribeOutputSlot(slot, out ulong producerFrameId, out ulong fenceTarget, out ulong fenceCompleted);
        XRViewport? viewport = RuntimeEngine.Rendering.State.RenderingPipelineState?.WindowViewport as XRViewport;
        RuntimeVrState vr = RuntimeEngine.VRState;
        string viewportKind = viewport is null ? "none"
            : ReferenceEquals(viewport, vr.OpenXRApi?.StereoViewport) ? "openxr-stereo"
            : ReferenceEquals(viewport, vr.LeftEyeViewport) ? "left-eye"
            : ReferenceEquals(viewport, vr.RightEyeViewport) ? "right-eye"
            : "window";

        StringBuilder totals = new();
        for (EAdvancedDeformationSlotDeferral reason = EAdvancedDeformationSlotDeferral.SlotPoisoned;
            reason < EAdvancedDeformationSlotDeferral.Count;
            ++reason)
        {
            long count = deformation.GetBeginFrameDeferralCount(reason);
            if (count != 0)
                totals.Append(' ').Append(reason).Append('=').Append(count);
        }

        Debug.WriteAuxiliaryLog(
            DeformationSlotDiagnosticsLogFileName,
            FormattableString.Invariant(
                $"frame={frameId} renderFrame={RuntimeEngine.Rendering.State.RenderFrameId} slot={slot} reason={deformation.LastBeginFrameDeferral} status={deformation.LastOutputReuseStatus} authority={deformation.LastOutputReuseAuthority} producerFrame={producerFrameId} frameAge={(producerFrameId == 0UL ? 0UL : frameId - producerFrameId)} fenceTarget={fenceTarget} fenceCompleted={fenceCompleted} viewport={viewportKind} consumers={consumers} totals:{totals}"));
    }
}
