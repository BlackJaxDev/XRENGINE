using System;

namespace XREngine.Rendering.Vulkan;

/// <summary>Allocation-free mutable lifecycle record for one frame owned by a telemetry authority.</summary>
internal ref struct VulkanFrameTrace
{
    private readonly VulkanFrameTelemetry _authority;

    public VulkanFrameRootIdentity Identity;
    public VulkanFrameStageTiming FramePacing;
    public VulkanFrameStageTiming SnapshotHandoff;
    public VulkanFrameStageTiming CompletionMaintenance;
    public VulkanFrameStageTiming OutputAcquire;
    public VulkanFrameStageTiming PlanBuild;
    public VulkanFrameStageTiming ResourcePrepare;
    public VulkanFrameStageTiming WorkSchedule;
    public VulkanFrameStageTiming CommandRecord;
    public VulkanFrameStageTiming SubmitPrepare;
    public VulkanFrameStageTiming QueueSubmit;
    public VulkanFrameStageTiming OutputComplete;
    public VulkanFrameStageTiming FrameSettlement;

    public TimeSpan WaitFrameSlot;
    public TimeSpan WaitCurrentFrameSlot;
    public TimeSpan WaitNextFrameSlotBeforeCollect;
    public TimeSpan AcquireImage;
    public TimeSpan RecordCommandBuffer;
    public TimeSpan SnapshotImGuiOverlay;
    public TimeSpan RecordSceneCommandBuffer;
    public TimeSpan RecordImGuiOverlay;
    public TimeSpan RecordDynamicUiTextOverlay;
    public TimeSpan SubmitQueue;
    public TimeSpan TrimStaging;
    public TimeSpan PresentQueue;
    public TimeSpan SampleTimingQueries;
    public TimeSpan DrainRetiredResources;
    public TimeSpan AcquireBridgeSubmit;
    public TimeSpan WaitSwapchainImage;
    public TimeSpan ResetDynamicUniformRing;
    public VulkanPresentationProfileSnapshot PresentationProfile;
    public TimeSpan ActualPresentInterval;
    public TimeSpan LimiterSleep;
    public TimeSpan LimiterSpin;
    public TimeSpan QueueSubmitAdmission;
    public TimeSpan NativeQueueSubmit;
    public TimeSpan QueuePresentAdmission;
    public TimeSpan NativeQueuePresent;
    public int FramesAhead;
    public uint AcquireUnavailableCount;
    public bool PresentDispatched;
    public bool PresentationAccepted;
    public VulkanDeviceDiagnosticTelemetry DeviceDiagnostics;
    public TimeSpan WorkerOverlap;
    public VulkanFrameCausalWaitSet CausalWaits;

    private static readonly TimeSpan CausalWaitThreshold =
        TimeSpan.FromMilliseconds(0.1);

    internal VulkanFrameTrace(VulkanFrameTelemetry authority, in DesktopFrameIdentity identity)
    {
        this = default;
        _authority = authority;
        Identity = new VulkanFrameRootIdentity(
            identity.FrameNumber,
            identity.FrameNumber,
            identity.FrameSlot,
            identity.StartTimestamp,
            new VulkanFrameOutputIdentity(0, 0));
    }

    internal VulkanFrameTrace(VulkanFrameTelemetry authority, VulkanFrameRootIdentity identity)
    {
        this = default;
        _authority = authority;
        Identity = identity;
    }

    public void SetOutputIdentity(int outputIndex, ulong outputGeneration)
        => Identity = Identity with
        {
            Output = new VulkanFrameOutputIdentity(outputIndex, outputGeneration),
        };

    /// <summary>
    /// Correlates the output-attempt root with the immutable render frame that
    /// supplied its command work. Reused output work can therefore retain a
    /// different output-attempt and render-frame identity without ambiguity.
    /// </summary>
    public void SetRenderFrameIdentity(ulong renderFrameNumber)
        => Identity = Identity with { RenderFrameNumber = renderFrameNumber };

    public void RecordStage(EVulkanFrameStage stage, TimeSpan elapsed, EVulkanFrameIntervalClass intervalClass,
        EVulkanFrameOutcome outcome, EVulkanFrameWaitReason waitReason = EVulkanFrameWaitReason.None)
    {
        if ((intervalClass is EVulkanFrameIntervalClass.Wait or EVulkanFrameIntervalClass.Driver or EVulkanFrameIntervalClass.External) &&
            waitReason == EVulkanFrameWaitReason.None)
            throw new ArgumentOutOfRangeException(nameof(waitReason));

        switch (stage)
        {
            case EVulkanFrameStage.FramePacing: FramePacing.Add(elapsed, intervalClass, outcome, waitReason); break;
            case EVulkanFrameStage.SnapshotHandoff: SnapshotHandoff.Add(elapsed, intervalClass, outcome, waitReason); break;
            case EVulkanFrameStage.CompletionMaintenance: CompletionMaintenance.Add(elapsed, intervalClass, outcome, waitReason); break;
            case EVulkanFrameStage.OutputAcquire: OutputAcquire.Add(elapsed, intervalClass, outcome, waitReason); break;
            case EVulkanFrameStage.PlanBuild: PlanBuild.Add(elapsed, intervalClass, outcome, waitReason); break;
            case EVulkanFrameStage.ResourcePrepare: ResourcePrepare.Add(elapsed, intervalClass, outcome, waitReason); break;
            case EVulkanFrameStage.WorkSchedule: WorkSchedule.Add(elapsed, intervalClass, outcome, waitReason); break;
            case EVulkanFrameStage.CommandRecord: CommandRecord.Add(elapsed, intervalClass, outcome, waitReason); break;
            case EVulkanFrameStage.SubmitPrepare: SubmitPrepare.Add(elapsed, intervalClass, outcome, waitReason); break;
            case EVulkanFrameStage.QueueSubmit: QueueSubmit.Add(elapsed, intervalClass, outcome, waitReason); break;
            case EVulkanFrameStage.OutputComplete: OutputComplete.Add(elapsed, intervalClass, outcome, waitReason); break;
            case EVulkanFrameStage.FrameSettlement: FrameSettlement.Add(elapsed, intervalClass, outcome, waitReason); break;
            default: throw new ArgumentOutOfRangeException(nameof(stage));
        }
    }

    public void PublishAfterFrame(TimeSpan totalFrameTime, EVulkanFrameOutcome outcome)
    {
        VulkanFrameWaitInstrumentation.CompleteFrame(_authority, ref this);
        PopulateMeasuredLegacyStages();
        _authority.PublishAfterFrame(this, totalFrameTime, outcome);
    }

    /// <summary>
    /// Retains a bounded causal payload only for waits that exceed the Phase 1
    /// detailed-capture threshold. Successful short waits remain allocation-free.
    /// </summary>
    public void RecordCausalWait(in VulkanFrameCausalWait wait)
    {
        if (wait.Elapsed >= CausalWaitThreshold)
            CausalWaits.Add(in wait);
    }

    /// <summary>
    /// Adds only the portion of a coarse phase interval that is not already
    /// represented by explicitly classified child intervals.
    /// </summary>
    public void RecordStageRemainder(
        EVulkanFrameStage stage,
        TimeSpan inclusiveElapsed,
        EVulkanFrameOutcome outcome)
    {
        TimeSpan represented = GetStage(stage).Elapsed;
        TimeSpan remainder = inclusiveElapsed > represented
            ? inclusiveElapsed - represented
            : TimeSpan.Zero;
        if (remainder > TimeSpan.Zero)
        {
            RecordStage(
                stage,
                remainder,
                EVulkanFrameIntervalClass.Work,
                outcome);
        }
    }

    /// <summary>
    /// Reclassifies a child wait/driver/external interval that was included in
    /// the stage's coarse work measurement.
    /// </summary>
    internal void ReclassifyStageWork(
        EVulkanFrameStage stage,
        TimeSpan elapsed,
        EVulkanFrameIntervalClass intervalClass,
        EVulkanFrameWaitReason waitReason)
    {
        switch (stage)
        {
            case EVulkanFrameStage.FramePacing:
                FramePacing.ReclassifyWork(elapsed, intervalClass, waitReason);
                break;
            case EVulkanFrameStage.SnapshotHandoff:
                SnapshotHandoff.ReclassifyWork(elapsed, intervalClass, waitReason);
                break;
            case EVulkanFrameStage.CompletionMaintenance:
                CompletionMaintenance.ReclassifyWork(elapsed, intervalClass, waitReason);
                break;
            case EVulkanFrameStage.OutputAcquire:
                OutputAcquire.ReclassifyWork(elapsed, intervalClass, waitReason);
                break;
            case EVulkanFrameStage.PlanBuild:
                PlanBuild.ReclassifyWork(elapsed, intervalClass, waitReason);
                break;
            case EVulkanFrameStage.ResourcePrepare:
                ResourcePrepare.ReclassifyWork(elapsed, intervalClass, waitReason);
                break;
            case EVulkanFrameStage.WorkSchedule:
                WorkSchedule.ReclassifyWork(elapsed, intervalClass, waitReason);
                break;
            case EVulkanFrameStage.CommandRecord:
                CommandRecord.ReclassifyWork(elapsed, intervalClass, waitReason);
                break;
            case EVulkanFrameStage.SubmitPrepare:
                SubmitPrepare.ReclassifyWork(elapsed, intervalClass, waitReason);
                break;
            case EVulkanFrameStage.QueueSubmit:
                QueueSubmit.ReclassifyWork(elapsed, intervalClass, waitReason);
                break;
            case EVulkanFrameStage.OutputComplete:
                OutputComplete.ReclassifyWork(elapsed, intervalClass, waitReason);
                break;
            case EVulkanFrameStage.FrameSettlement:
                FrameSettlement.ReclassifyWork(elapsed, intervalClass, waitReason);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(stage));
        }
    }

    internal readonly VulkanFrameTelemetryPublication CreatePublication(
        long authorityId,
        long sequence,
        TimeSpan totalFrameTime,
        EVulkanFrameOutcome outcome)
        => new(
            authorityId,
            sequence,
            Identity,
            totalFrameTime,
            outcome,
            new VulkanFrameDetailTelemetry(
                WaitFrameSlot,
                AcquireImage,
                RecordCommandBuffer,
                SnapshotImGuiOverlay,
                RecordSceneCommandBuffer,
                RecordImGuiOverlay,
                RecordDynamicUiTextOverlay,
                SubmitQueue,
                TrimStaging,
                PresentQueue,
                SampleTimingQueries,
                DrainRetiredResources,
                AcquireBridgeSubmit,
                WaitSwapchainImage,
                ResetDynamicUniformRing),
            PresentationProfile,
            new VulkanFramePresentationTelemetry(
                ActualPresentInterval,
                LimiterSleep,
                LimiterSpin,
                QueueSubmitAdmission,
                NativeQueueSubmit,
                QueuePresentAdmission,
                NativeQueuePresent,
                FramesAhead,
                AcquireUnavailableCount,
                PresentDispatched,
                PresentationAccepted),
            DeviceDiagnostics,
            CreateFrameTree(totalFrameTime),
            CausalWaits,
            CreateAttribution(totalFrameTime),
            FramePacing,
            SnapshotHandoff,
            CompletionMaintenance,
            OutputAcquire,
            PlanBuild,
            ResourcePrepare,
            WorkSchedule,
            CommandRecord,
            SubmitPrepare,
            QueueSubmit,
            OutputComplete,
            FrameSettlement);

    private readonly VulkanFrameTreeTelemetry CreateFrameTree(
        TimeSpan totalFrameTime)
    {
        TimeSpan work = TimeSpan.Zero;
        TimeSpan wait = TimeSpan.Zero;
        TimeSpan driver = TimeSpan.Zero;
        TimeSpan external = TimeSpan.Zero;
        TimeSpan diagnostic = TimeSpan.Zero;
        AccumulateStage(in FramePacing, ref work, ref wait, ref driver, ref external, ref diagnostic);
        AccumulateStage(in SnapshotHandoff, ref work, ref wait, ref driver, ref external, ref diagnostic);
        AccumulateStage(in CompletionMaintenance, ref work, ref wait, ref driver, ref external, ref diagnostic);
        AccumulateStage(in OutputAcquire, ref work, ref wait, ref driver, ref external, ref diagnostic);
        AccumulateStage(in PlanBuild, ref work, ref wait, ref driver, ref external, ref diagnostic);
        AccumulateStage(in ResourcePrepare, ref work, ref wait, ref driver, ref external, ref diagnostic);
        AccumulateStage(in WorkSchedule, ref work, ref wait, ref driver, ref external, ref diagnostic);
        AccumulateStage(in CommandRecord, ref work, ref wait, ref driver, ref external, ref diagnostic);
        AccumulateStage(in SubmitPrepare, ref work, ref wait, ref driver, ref external, ref diagnostic);
        AccumulateStage(in QueueSubmit, ref work, ref wait, ref driver, ref external, ref diagnostic);
        AccumulateStage(in OutputComplete, ref work, ref wait, ref driver, ref external, ref diagnostic);
        AccumulateStage(in FrameSettlement, ref work, ref wait, ref driver, ref external, ref diagnostic);

        TimeSpan stageExclusive = work + wait + driver + external + diagnostic;
        TimeSpan rootExclusive = totalFrameTime > stageExclusive
            ? totalFrameTime - stageExclusive
            : TimeSpan.Zero;
        return new VulkanFrameTreeTelemetry(
            totalFrameTime,
            stageExclusive,
            rootExclusive,
            work,
            wait,
            driver,
            external,
            diagnostic,
            WorkerOverlap,
            totalFrameTime);
    }

    private static void AccumulateStage(
        in VulkanFrameStageTiming stage,
        ref TimeSpan work,
        ref TimeSpan wait,
        ref TimeSpan driver,
        ref TimeSpan external,
        ref TimeSpan diagnostic)
    {
        work += stage.WorkElapsed;
        wait += stage.WaitElapsed;
        driver += stage.DriverElapsed;
        external += stage.ExternalElapsed;
        diagnostic += stage.DiagnosticElapsed;
    }

    private readonly VulkanFrameAttributionTelemetry CreateAttribution(
        TimeSpan totalFrameTime)
    {
        TimeSpan attributed =
            FramePacing.Elapsed +
            SnapshotHandoff.Elapsed +
            CompletionMaintenance.Elapsed +
            OutputAcquire.Elapsed +
            PlanBuild.Elapsed +
            ResourcePrepare.Elapsed +
            WorkSchedule.Elapsed +
            CommandRecord.Elapsed +
            SubmitPrepare.Elapsed +
            QueueSubmit.Elapsed +
            OutputComplete.Elapsed +
            FrameSettlement.Elapsed;
        TimeSpan unattributed = totalFrameTime > attributed
            ? totalFrameTime - attributed
            : TimeSpan.Zero;
        double ratio = totalFrameTime > TimeSpan.Zero
            ? Math.Clamp(attributed.TotalSeconds / totalFrameTime.TotalSeconds, 0.0, 1.0)
            : 1.0;
        return new VulkanFrameAttributionTelemetry(
            attributed,
            unattributed,
            ratio,
            unattributed >= TimeSpan.FromTicks(500));
    }

    private void PopulateMeasuredLegacyStages()
    {
        TimeSpan elapsed = WaitCurrentFrameSlot;
        EnsureStageInclusive(
            EVulkanFrameStage.CompletionMaintenance,
            elapsed,
            EVulkanFrameOutcome.Completed);
        ReclassifyStageWork(
            EVulkanFrameStage.CompletionMaintenance,
            elapsed,
            EVulkanFrameIntervalClass.Wait,
            EVulkanFrameWaitReason.FrameSlot);

        elapsed = LimiterSleep;
        ReclassifyStageWork(
            EVulkanFrameStage.FramePacing,
            elapsed,
            EVulkanFrameIntervalClass.Wait,
            EVulkanFrameWaitReason.FrameLimiterSleep);
        elapsed = LimiterSpin;
        ReclassifyStageWork(
            EVulkanFrameStage.FramePacing,
            elapsed,
            EVulkanFrameIntervalClass.Wait,
            EVulkanFrameWaitReason.FrameLimiterSpin);

        elapsed = DrainRetiredResources;
        EnsureStageInclusive(
            EVulkanFrameStage.CompletionMaintenance,
            WaitCurrentFrameSlot + elapsed,
            EVulkanFrameOutcome.Completed);
        ReclassifyStageWork(
            EVulkanFrameStage.CompletionMaintenance,
            elapsed,
            EVulkanFrameIntervalClass.Driver,
            EVulkanFrameWaitReason.Completion);

        elapsed = AcquireImage + WaitSwapchainImage +
            SampleTimingQueries + ResetDynamicUniformRing;
        EnsureStageInclusive(
            EVulkanFrameStage.OutputAcquire,
            elapsed,
            EVulkanFrameOutcome.Completed);
        ReclassifyStageWork(
            EVulkanFrameStage.OutputAcquire,
            WaitSwapchainImage,
            EVulkanFrameIntervalClass.Wait,
            EVulkanFrameWaitReason.OutputImage);
        ReclassifyStageWork(
            EVulkanFrameStage.OutputAcquire,
            AcquireImage,
            EVulkanFrameIntervalClass.External,
            EVulkanFrameWaitReason.SwapchainAcquire);
        ReclassifyStageWork(
            EVulkanFrameStage.OutputAcquire,
            SampleTimingQueries,
            EVulkanFrameIntervalClass.Driver,
            EVulkanFrameWaitReason.Completion);

        elapsed = RecordCommandBuffer + SnapshotImGuiOverlay;
        EnsureStageInclusive(
            EVulkanFrameStage.CommandRecord,
            elapsed,
            EVulkanFrameOutcome.Completed);

        elapsed = SubmitQueue + AcquireBridgeSubmit + TrimStaging;
        EnsureStageInclusive(
            EVulkanFrameStage.QueueSubmit,
            elapsed,
            EVulkanFrameOutcome.Completed);
        ReclassifyStageWork(
            EVulkanFrameStage.QueueSubmit,
            WaitNextFrameSlotBeforeCollect,
            EVulkanFrameIntervalClass.Wait,
            EVulkanFrameWaitReason.FrameSlot);
        ReclassifyStageWork(
            EVulkanFrameStage.QueueSubmit,
            QueueSubmitAdmission,
            EVulkanFrameIntervalClass.Wait,
            EVulkanFrameWaitReason.QueueSubmitAdmission);
        ReclassifyStageWork(
            EVulkanFrameStage.QueueSubmit,
            NativeQueueSubmit,
            EVulkanFrameIntervalClass.Driver,
            EVulkanFrameWaitReason.NativeQueueSubmit);

        EnsureStageInclusive(
            EVulkanFrameStage.OutputComplete,
            PresentQueue,
            EVulkanFrameOutcome.Completed);
        ReclassifyStageWork(
            EVulkanFrameStage.OutputComplete,
            QueuePresentAdmission,
            EVulkanFrameIntervalClass.Wait,
            EVulkanFrameWaitReason.QueuePresentAdmission);
        ReclassifyStageWork(
            EVulkanFrameStage.OutputComplete,
            NativeQueuePresent,
            EVulkanFrameIntervalClass.External,
            EVulkanFrameWaitReason.NativeQueuePresent);

    }

    private void EnsureStageInclusive(
        EVulkanFrameStage stage,
        TimeSpan inclusiveElapsed,
        EVulkanFrameOutcome outcome)
        => RecordStageRemainder(stage, inclusiveElapsed, outcome);

    private readonly VulkanFrameStageTiming GetStage(EVulkanFrameStage stage)
        => stage switch
        {
            EVulkanFrameStage.FramePacing => FramePacing,
            EVulkanFrameStage.SnapshotHandoff => SnapshotHandoff,
            EVulkanFrameStage.CompletionMaintenance => CompletionMaintenance,
            EVulkanFrameStage.OutputAcquire => OutputAcquire,
            EVulkanFrameStage.PlanBuild => PlanBuild,
            EVulkanFrameStage.ResourcePrepare => ResourcePrepare,
            EVulkanFrameStage.WorkSchedule => WorkSchedule,
            EVulkanFrameStage.CommandRecord => CommandRecord,
            EVulkanFrameStage.SubmitPrepare => SubmitPrepare,
            EVulkanFrameStage.QueueSubmit => QueueSubmit,
            EVulkanFrameStage.OutputComplete => OutputComplete,
            EVulkanFrameStage.FrameSettlement => FrameSettlement,
            _ => throw new ArgumentOutOfRangeException(nameof(stage)),
        };
}
