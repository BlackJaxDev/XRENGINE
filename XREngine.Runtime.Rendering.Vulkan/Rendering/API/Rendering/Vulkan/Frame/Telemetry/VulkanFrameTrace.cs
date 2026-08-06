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
        PopulateMeasuredLegacyStages();
        _authority.PublishAfterFrame(this, totalFrameTime, outcome);
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

    private void PopulateMeasuredLegacyStages()
    {
        TimeSpan elapsed = WaitFrameSlot;
        if (FramePacing.IntervalCount == 0 && elapsed > TimeSpan.Zero)
            RecordStage(EVulkanFrameStage.FramePacing, elapsed, EVulkanFrameIntervalClass.Wait, EVulkanFrameOutcome.Completed, EVulkanFrameWaitReason.FrameSlot);

        elapsed = DrainRetiredResources + SampleTimingQueries;
        if (CompletionMaintenance.IntervalCount == 0 && elapsed > TimeSpan.Zero)
            RecordStage(EVulkanFrameStage.CompletionMaintenance, elapsed, EVulkanFrameIntervalClass.Driver, EVulkanFrameOutcome.Completed, EVulkanFrameWaitReason.Completion);

        elapsed = AcquireImage + WaitSwapchainImage;
        if (OutputAcquire.IntervalCount == 0 && elapsed > TimeSpan.Zero)
            RecordStage(EVulkanFrameStage.OutputAcquire, elapsed, EVulkanFrameIntervalClass.External, EVulkanFrameOutcome.Completed, EVulkanFrameWaitReason.OutputImage);

        elapsed = RecordCommandBuffer + SnapshotImGuiOverlay;
        if (CommandRecord.IntervalCount == 0 && elapsed > TimeSpan.Zero)
            RecordStage(EVulkanFrameStage.CommandRecord, elapsed, EVulkanFrameIntervalClass.Work, EVulkanFrameOutcome.Completed);

        elapsed = SubmitQueue + AcquireBridgeSubmit;
        if (QueueSubmit.IntervalCount == 0 && elapsed > TimeSpan.Zero)
            RecordStage(EVulkanFrameStage.QueueSubmit, elapsed, EVulkanFrameIntervalClass.Driver, EVulkanFrameOutcome.Completed, EVulkanFrameWaitReason.QueueGateway);

        if (OutputComplete.IntervalCount == 0 && PresentQueue > TimeSpan.Zero)
            RecordStage(EVulkanFrameStage.OutputComplete, PresentQueue, EVulkanFrameIntervalClass.External, EVulkanFrameOutcome.Completed, EVulkanFrameWaitReason.ExternalRuntime);

        elapsed = TrimStaging + ResetDynamicUniformRing;
        if (FrameSettlement.IntervalCount == 0 && elapsed > TimeSpan.Zero)
            RecordStage(EVulkanFrameStage.FrameSettlement, elapsed, EVulkanFrameIntervalClass.Work, EVulkanFrameOutcome.Completed);
    }
}
