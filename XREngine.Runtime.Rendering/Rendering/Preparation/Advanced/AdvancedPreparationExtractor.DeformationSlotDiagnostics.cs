using System;
using System.Diagnostics;
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

    /// <summary>True when the deformation slot diagnostics are on. Read by the slot poison log.</summary>
    internal static bool DeformationSlotDiagnosticsEnabled => s_deformationSlotDiagnostics;

    /// <summary>Auxiliary log of the deferral, poison and summary lines. Release sessions also write it.</summary>
    internal const string DeformationSlotDiagnosticsLogFileName = "advanced-deformation-slot.log";

    /// <summary>Seconds between two summary lines.</summary>
    private const double PreparationSummaryIntervalSeconds = 5.0;

    // Process totals of every preparation deferral reason. Written under the shared preparation
    // service lock; read by the summary line.
    private readonly long[] _preparationDeferralCounts = new long[(int)EAdvancedPreparationDeferral.Count];

    /// <summary>Process total of preparation deferrals for <paramref name="reason"/>.</summary>
    internal long GetPreparationDeferralCount(EAdvancedPreparationDeferral reason)
        => (uint)reason < (uint)_preparationDeferralCounts.Length ? _preparationDeferralCounts[(int)reason] : 0L;

    /// <summary>The reason of the last preparation deferral, or None.</summary>
    internal EAdvancedPreparationDeferral LastPreparationDeferral { get; private set; }

    /// <summary>
    /// Counts one preparation deferral (or one reuse of a deferred world frame) and, while the
    /// deformation slot diagnostics are on, writes one PREP line: the reason, the frame id, the
    /// render frame, the active viewport, the consumers and the deferral text. Counting does
    /// not allocate; only the line does.
    /// </summary>
    internal void NotePreparationDeferral(
        EAdvancedPreparationDeferral reason,
        ulong frameId,
        EAdvancedPreparationConsumer consumers,
        string text)
    {
        if ((uint)reason < (uint)_preparationDeferralCounts.Length)
            _preparationDeferralCounts[(int)reason]++;
        if (reason != EAdvancedPreparationDeferral.DeferredWorldFrameReused)
            LastPreparationDeferral = reason;
        if (!s_deformationSlotDiagnostics)
            return;
        Debug.WriteAuxiliaryLog(
            DeformationSlotDiagnosticsLogFileName,
            FormattableString.Invariant(
                $"PREP reason={reason} frame={frameId} renderFrame={RuntimeEngine.Rendering.State.RenderFrameId} viewport={ResolveDiagnosticViewportKind()} consumers={consumers} text=\"{text}\""));
    }

    /// <summary>Names the viewport of the active render for diagnostics: OpenXR stereo, an eye, a window or none.</summary>
    private static string ResolveDiagnosticViewportKind()
    {
        XRViewport? viewport = RuntimeEngine.Rendering.State.RenderingPipelineState?.WindowViewport as XRViewport;
        RuntimeVrState vr = RuntimeEngine.VRState;
        return viewport is null ? "none"
            : ReferenceEquals(viewport, vr.OpenXRApi?.StereoViewport) ? "openxr-stereo"
            : ReferenceEquals(viewport, vr.LeftEyeViewport) ? "left-eye"
            : ReferenceEquals(viewport, vr.RightEyeViewport) ? "right-eye"
            : "window";
    }

    private long _summaryStartTimestamp;
    private long _summaryPublishedCount;
    private long _summaryDeferredCount;

    /// <summary>
    /// Counts one preparation build and, every few seconds while the diagnostics are on, writes
    /// one SUMMARY line that shows which session state the preparation is in: the published and
    /// deferred builds since the last summary, the last deferral reason, the poisoned slot mask, and
    /// the process totals of every deferral reason and poison cause. A session whose builds all
    /// defer with every slot poisoned shows a frozen image; some poisoned slots give intermittent
    /// deferrals (stale shadows); no poisoned slot and few deferrals is the healthy state.
    /// </summary>
    internal void NotePreparationOutcome(bool published)
    {
        if (!s_deformationSlotDiagnostics)
            return;

        if (published)
            _summaryPublishedCount++;
        else
            _summaryDeferredCount++;

        long now = Stopwatch.GetTimestamp();
        if (_summaryStartTimestamp == 0L)
        {
            _summaryStartTimestamp = now;
            return;
        }
        if (Stopwatch.GetElapsedTime(_summaryStartTimestamp, now).TotalSeconds < PreparationSummaryIntervalSeconds)
            return;

        AdvancedGpuDeformationResources deformation = _gpuDeformation;
        StringBuilder totals = new();
        for (EAdvancedDeformationSlotDeferral reason = EAdvancedDeformationSlotDeferral.SlotPoisoned;
            reason < EAdvancedDeformationSlotDeferral.Count;
            ++reason)
        {
            long count = deformation.GetBeginFrameDeferralCount(reason);
            if (count != 0)
                totals.Append(' ').Append(reason).Append('=').Append(count);
        }
        for (EAdvancedDeformationSlotPoisonCause cause = 0; cause < EAdvancedDeformationSlotPoisonCause.Count; ++cause)
        {
            long count = deformation.GetSlotPoisonCount(cause);
            if (count != 0)
                totals.Append(" poison.").Append(cause).Append('=').Append(count);
        }
        for (EAdvancedPreparationDeferral reason = EAdvancedPreparationDeferral.PackageWithoutCanonicalPublication;
            reason < EAdvancedPreparationDeferral.Count;
            ++reason)
        {
            long count = GetPreparationDeferralCount(reason);
            if (count != 0)
                totals.Append(" prep.").Append(reason).Append('=').Append(count);
        }
        XREngine.Rendering.Commands.AdvancedTextureSourceMismatchDiagnostics.AppendTotals(totals);

        Debug.WriteAuxiliaryLog(
            DeformationSlotDiagnosticsLogFileName,
            FormattableString.Invariant(
                $"SUMMARY renderFrame={RuntimeEngine.Rendering.State.RenderFrameId} windowSeconds={Stopwatch.GetElapsedTime(_summaryStartTimestamp, now).TotalSeconds:F1} published={_summaryPublishedCount} deferred={_summaryDeferredCount} lastDeferral=\"{LastDeferralReason}\" lastPrepDeferral={LastPreparationDeferral} lastBeginFrameDeferral={deformation.LastBeginFrameDeferral} poisonedMask=0x{deformation.PoisonedSlotMask:X} slots={deformation.OutputSlotCount} totals:{totals}"));
        _summaryStartTimestamp = now;
        _summaryPublishedCount = 0L;
        _summaryDeferredCount = 0L;
    }

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
        string viewportKind = ResolveDiagnosticViewportKind();

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
                $"frame={frameId} renderFrame={RuntimeEngine.Rendering.State.RenderFrameId} slot={slot} reason={deformation.LastBeginFrameDeferral} status={deformation.LastOutputReuseStatus} authority={deformation.LastOutputReuseAuthority} producerFrame={producerFrameId} frameAge={(producerFrameId == 0UL ? 0UL : frameId - producerFrameId)} fenceTarget={fenceTarget} fenceCompleted={fenceCompleted} viewport={viewportKind} consumers={consumers} native=[{(deformation.LastBeginFrameDeferral == EAdvancedDeformationSlotDeferral.NativeConsumers ? deformation.LastNativeReuseDetail ?? "none" : "n/a")}] totals:{totals}"));
    }
}
