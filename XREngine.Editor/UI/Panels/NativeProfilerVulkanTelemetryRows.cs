using System.Globalization;
using XREngine.Rendering.Vulkan;

namespace XREngine.Editor;

/// <summary>Formats one complete, allocation-free Vulkan frame publication into bounded virtual rows.</summary>
internal static class NativeProfilerVulkanTelemetryRows
{
    public static void Append(List<NativeProfilerDisplayRow> rows,
        in VulkanFrameTelemetryPublication frame)
    {
        if (frame.PublicationSequence <= 0)
            return;

        VulkanFrameTreeTelemetry tree = frame.Tree;
        VulkanFrameRootIdentity identity = frame.Identity;
        VulkanFramePresentationTelemetry presentation = frame.Presentation;
        VulkanDeviceDiagnosticTelemetry device = frame.DeviceDiagnostics;
        VulkanPresentationProfileSnapshot profile = frame.PresentationProfile;
        VulkanFrameDetailTelemetry detail = frame.Detail;
        rows.Add(new("Vulkan frame publication", frame.PublicationSequence.ToString(CultureInfo.InvariantCulture),
            $"authority {frame.AuthorityId} | engine/render {identity.EngineFrameNumber}/{identity.RenderFrameNumber} | slot {identity.FrameSlot} | output {identity.Output.OutputIndex}/{identity.Output.OutputGeneration} | {frame.Outcome}"));
        rows.Add(new("Vulkan critical path", tree.RequiredOutputCriticalPathElapsed.TotalMilliseconds.ToString("F3", CultureInfo.InvariantCulture),
            $"ms required output | inclusive {tree.InclusiveElapsed.TotalMilliseconds:F3} | stage exclusive {tree.StageExclusiveElapsed.TotalMilliseconds:F3} | root unattributed {tree.RootExclusiveElapsed.TotalMilliseconds:F3}"));
        rows.Add(new("Vulkan frame classes", tree.WorkElapsed.TotalMilliseconds.ToString("F3", CultureInfo.InvariantCulture),
            $"work | wait {tree.WaitElapsed.TotalMilliseconds:F3} | driver {tree.NativeDriverElapsed.TotalMilliseconds:F3} | external {tree.ExternalRuntimeElapsed.TotalMilliseconds:F3} | diagnostics {tree.DiagnosticElapsed.TotalMilliseconds:F3} | worker overlap {tree.WorkerOverlapElapsed.TotalMilliseconds:F3} ms"));
        rows.Add(new("Vulkan attribution", (frame.Attribution.AttributedRatio * 100.0).ToString("F2", CultureInfo.InvariantCulture),
            $"% | attributed {frame.Attribution.Attributed.TotalMilliseconds:F3} ms | unattributed {frame.Attribution.Unattributed.TotalMilliseconds:F3} ms | reportable gap {frame.Attribution.HasReportableGap}"));
        rows.Add(new("Vulkan presentation", profile.ResolvedProfile.ToString(),
            $"requested {profile.RequestedProfile} | mode {profile.PresentMode} | actual {presentation.ActualPresentInterval.TotalMilliseconds:F3} ms | target {profile.TargetRefreshHz:F1} Hz | ahead {presentation.FramesAhead}/{profile.MaximumFramesAhead} | outcome {frame.Outcome}"));
        rows.Add(new("Vulkan present calls", presentation.NativeQueuePresent.TotalMilliseconds.ToString("F3", CultureInfo.InvariantCulture),
            $"native ms | queue admission {presentation.QueuePresentAdmission.TotalMilliseconds:F3} | submit admission/native {presentation.QueueSubmitAdmission.TotalMilliseconds:F3}/{presentation.NativeQueueSubmit.TotalMilliseconds:F3} | limiter sleep/spin {presentation.LimiterSleep.TotalMilliseconds:F3}/{presentation.LimiterSpin.TotalMilliseconds:F3} | dispatched/accepted {presentation.PresentDispatched}/{presentation.PresentationAccepted} | acquire unavailable {presentation.AcquireUnavailableCount}"));
        rows.Add(new("Vulkan profile", profile.RenderTargetMode.ToString(),
            $"images/slots {profile.SwapchainImageCount}/{profile.FrameSlotCount} | limiter/frame generation {profile.LimiterEnabled}/{profile.FrameGenerationEnabled} | validation {profile.ValidationEnabled} | present ID available/enabled {profile.PresentIdAvailable}/{profile.PresentIdEnabled} | wait {profile.PresentWaitAvailable}/{profile.PresentWaitEnabled} | display timing {profile.DisplayTimingAvailable}/{profile.DisplayTimingEnabled}"));
        rows.Add(new("Vulkan device", device.DeviceOperational ? "Operational" : device.DeviceLost ? "Lost" : "Unavailable",
            $"fault supported/capture {device.DeviceFaultSupported}/{device.DeviceFaultCaptureActive} | memory budget supported/valid {device.MemoryBudgetSupported}/{device.MemoryBudgetSampleValid} | usage/budget {device.DeviceLocalUsageBytes}/{device.DeviceLocalBudgetBytes} bytes | largest heap {device.LargestDeviceLocalHeapBytes} | allocations {device.ActiveAllocationCount}"));
        rows.Add(new("Vulkan last success", device.LastSuccessfulSubmissionSerial.ToString(CultureInfo.InvariantCulture),
            $"frame/slot/image {device.LastSuccessfulSubmissionFrameId}/{device.LastSuccessfulSubmissionFrameSlot}/{device.LastSuccessfulSubmissionImageIndex} | timeline wait/signal {device.LastSuccessfulWaitTimelineValue}/{device.LastSuccessfulSignalTimelineValue} | marker serial/gen {device.LastCommandMarkerSerial}/{device.LastCommandMarkerGeneration} | descriptor gen {device.DescriptorTableGeneration} | longest driver {device.LongestNativeDriverCall.TotalMilliseconds:F3} ms | >1s {device.NativeDriverCallExceededOneSecond}"));
        rows.Add(new("Vulkan CPU detail", frame.TotalElapsed.TotalMilliseconds.ToString("F3", CultureInfo.InvariantCulture),
            $"total ms | slot wait {detail.WaitFrameSlot.TotalMilliseconds:F3} (current {detail.WaitCurrentFrameSlot.TotalMilliseconds:F3}, next collect {detail.WaitNextFrameSlotBeforeCollect.TotalMilliseconds:F3}) | acquire {detail.AcquireImage.TotalMilliseconds:F3} | record {detail.RecordCommandBuffer.TotalMilliseconds:F3} | submit {detail.SubmitQueue.TotalMilliseconds:F3} | trim {detail.TrimStaging.TotalMilliseconds:F3} | present {detail.PresentQueue.TotalMilliseconds:F3}"));
        rows.Add(new("Vulkan record detail", detail.RecordSceneCommandBuffer.TotalMilliseconds.ToString("F3", CultureInfo.InvariantCulture),
            $"scene ms | ImGui snapshot/record {detail.SnapshotImGuiOverlay.TotalMilliseconds:F3}/{detail.RecordImGuiOverlay.TotalMilliseconds:F3} | dynamic text {detail.RecordDynamicUiTextOverlay.TotalMilliseconds:F3} | query {detail.SampleTimingQueries.TotalMilliseconds:F3} | retire {detail.DrainRetiredResources.TotalMilliseconds:F3} | bridge {detail.AcquireBridgeSubmit.TotalMilliseconds:F3} | image wait {detail.WaitSwapchainImage.TotalMilliseconds:F3} | UBO reset {detail.ResetDynamicUniformRing.TotalMilliseconds:F3}"));

        for (int index = 0; index < (int)EVulkanFrameStage.Count; index++)
        {
            EVulkanFrameStage stage = (EVulkanFrameStage)index;
            VulkanFrameStageTiming timing = frame.GetStage(stage);
            rows.Add(new($"Vulkan stage: {stage}", timing.Elapsed.TotalMilliseconds.ToString("F3", CultureInfo.InvariantCulture),
                $"ms | work {timing.WorkElapsed.TotalMilliseconds:F3} | wait {timing.WaitElapsed.TotalMilliseconds:F3} | driver {timing.DriverElapsed.TotalMilliseconds:F3} | external {timing.ExternalElapsed.TotalMilliseconds:F3} | diagnostic {timing.DiagnosticElapsed.TotalMilliseconds:F3} | intervals {timing.IntervalCount} | class {timing.IntervalClass} | outcome {timing.Outcome} | reason {timing.WaitReason}"));
        }

        rows.Add(new("Vulkan causal waits", frame.CausalWaits.Count.ToString(CultureInfo.InvariantCulture),
            $"captured | dropped {frame.CausalWaits.DroppedCount}"));
        for (int index = 0; index < frame.CausalWaits.Count; index++)
        {
            VulkanFrameCausalWait wait = frame.CausalWaits.Get(index);
            rows.Add(new($"  {wait.Stage}/{wait.Reason}", wait.Elapsed.TotalMilliseconds.ToString("F3", CultureInfo.InvariantCulture),
                $"ms | frame/slot/image {wait.FrameId}/{wait.FrameSlot}/{wait.ImageIndex} | timeline {wait.SemaphoreCompletedValue}->{wait.SemaphoreTargetValue} | queue {wait.QueueFamily} | pending/workers {wait.PendingCommandCount}/{wait.ConcurrentWorkerActivity}"));
        }
    }
}
