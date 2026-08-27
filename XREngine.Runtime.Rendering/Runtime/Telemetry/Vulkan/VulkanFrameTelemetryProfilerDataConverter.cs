using XREngine.Data.Profiling;

namespace XREngine.Rendering.Vulkan;

/// <summary>Converts the shared renderer publication into diagnostic transport data.</summary>
public static class VulkanFrameTelemetryProfilerDataConverter
{
    /// <summary>
    /// Materializes the bounded stage and causal-wait arrays only when a
    /// profiler consumer requests a packet. Renderer publication itself stays
    /// allocation-free.
    /// </summary>
    public static VulkanCorrelatedFrameTreeData CreateProfilerFrameTree(
        in VulkanFrameTelemetryPublication publication)
    {
        if (publication.PublicationSequence <= 0)
            return new VulkanCorrelatedFrameTreeData();

        VulkanFrameStageTelemetryData[] stages =
            new VulkanFrameStageTelemetryData[(int)EVulkanFrameStage.Count];
        for (int stageIndex = 0; stageIndex < stages.Length; stageIndex++)
        {
            EVulkanFrameStage stage = (EVulkanFrameStage)stageIndex;
            VulkanFrameStageTiming timing = publication.GetStage(stage);
            stages[stageIndex] = new VulkanFrameStageTelemetryData
            {
                Name = stage.ToString(),
                ElapsedMs = timing.Elapsed.TotalMilliseconds,
                WorkMs = timing.WorkElapsed.TotalMilliseconds,
                WaitMs = timing.WaitElapsed.TotalMilliseconds,
                NativeDriverMs = timing.DriverElapsed.TotalMilliseconds,
                ExternalRuntimeMs = timing.ExternalElapsed.TotalMilliseconds,
                DiagnosticMs = timing.DiagnosticElapsed.TotalMilliseconds,
                IntervalCount = timing.IntervalCount,
                LastIntervalClass = timing.IntervalClass.ToString(),
                Outcome = timing.Outcome.ToString(),
                WaitReason = timing.WaitReason.ToString(),
            };
        }

        VulkanFrameCausalWaitTelemetryData[] causalWaits =
            new VulkanFrameCausalWaitTelemetryData[publication.CausalWaits.Count];
        for (int waitIndex = 0; waitIndex < causalWaits.Length; waitIndex++)
        {
            VulkanFrameCausalWait wait = publication.CausalWaits.Get(waitIndex);
            causalWaits[waitIndex] = new VulkanFrameCausalWaitTelemetryData
            {
                Stage = wait.Stage.ToString(),
                Reason = wait.Reason.ToString(),
                ElapsedMs = wait.Elapsed.TotalMilliseconds,
                FrameId = wait.FrameId,
                FrameSlot = wait.FrameSlot,
                ImageIndex = wait.ImageIndex,
                SemaphoreTargetValue = wait.SemaphoreTargetValue,
                SemaphoreCompletedValue = wait.SemaphoreCompletedValue,
                QueueFamily = wait.QueueFamily,
                PendingCommandCount = wait.PendingCommandCount,
                ConcurrentWorkerActivity = wait.ConcurrentWorkerActivity,
            };
        }

        return new VulkanCorrelatedFrameTreeData
        {
            AuthorityId = publication.AuthorityId,
            PublicationSequence = publication.PublicationSequence,
            EngineFrameNumber = publication.Identity.EngineFrameNumber,
            RenderFrameNumber = publication.Identity.RenderFrameNumber,
            FrameSlot = publication.Identity.FrameSlot,
            OutputIndex = publication.Identity.Output.OutputIndex,
            OutputGeneration = publication.Identity.Output.OutputGeneration,
            Outcome = publication.Outcome.ToString(),
            PresentationProfile = publication.PresentationProfile.ResolvedProfile.ToString(),
            PresentMode = publication.PresentationProfile.PresentMode.ToString(),
            ActualPresentIntervalMs = publication.Presentation.ActualPresentInterval.TotalMilliseconds,
            FramesAhead = publication.Presentation.FramesAhead,
            InclusiveMs = publication.Tree.InclusiveElapsed.TotalMilliseconds,
            StageExclusiveMs = publication.Tree.StageExclusiveElapsed.TotalMilliseconds,
            RootExclusiveMs = publication.Tree.RootExclusiveElapsed.TotalMilliseconds,
            WorkMs = publication.Tree.WorkElapsed.TotalMilliseconds,
            WaitMs = publication.Tree.WaitElapsed.TotalMilliseconds,
            NativeDriverMs = publication.Tree.NativeDriverElapsed.TotalMilliseconds,
            ExternalRuntimeMs = publication.Tree.ExternalRuntimeElapsed.TotalMilliseconds,
            DiagnosticMs = publication.Tree.DiagnosticElapsed.TotalMilliseconds,
            WorkerOverlapMs = publication.Tree.WorkerOverlapElapsed.TotalMilliseconds,
            RequiredOutputCriticalPathMs = publication.Tree.RequiredOutputCriticalPathElapsed.TotalMilliseconds,
            AttributedRatio = publication.Attribution.AttributedRatio,
            HasReportableGap = publication.Attribution.HasReportableGap,
            DeviceOperational = publication.DeviceDiagnostics.DeviceOperational,
            DeviceLost = publication.DeviceDiagnostics.DeviceLost,
            LastSuccessfulSubmissionSerial = publication.DeviceDiagnostics.LastSuccessfulSubmissionSerial,
            LastSuccessfulSignalTimelineValue = publication.DeviceDiagnostics.LastSuccessfulSignalTimelineValue,
            DroppedCausalWaitCount = publication.CausalWaits.DroppedCount,
            Stages = stages,
            CausalWaits = causalWaits,
        };
    }
}
