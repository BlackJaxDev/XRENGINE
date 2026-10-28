using System.Globalization;
using MeshletStats = XREngine.RuntimeEngine.Rendering.Stats.GpuMeshlets;
using SkinnedBoundsStats = XREngine.RuntimeEngine.Rendering.Stats.SkinnedBounds;
using TransparencyStats = XREngine.RuntimeEngine.Rendering.Stats.GpuTransparency;
using VrStats = XREngine.RuntimeEngine.Rendering.Stats.Vr;
using VulkanStats = XREngine.RuntimeEngine.Rendering.Stats.Vulkan;

namespace XREngine.Editor;

/// <summary>
/// Render-stat rows for the transparency, meshlet, OpenXR, skinned-bounds, and Vulkan command-chain
/// counters. The rows read the last completed render frame and are built only when the panel
/// refreshes its selected view.
/// </summary>
public partial class ProfilerPanel
{
    private static void CaptureTransparencyDomains(List<NativeProfilerDisplayRow> rows)
    {
        if (TransparencyStats.GpuTransparencyMaskedVisible <= 0 &&
            TransparencyStats.GpuTransparencyApproximateVisible <= 0 &&
            TransparencyStats.GpuTransparencyExactVisible <= 0)
            return;
        rows.Add(new("Transparency domains", TransparencyStats.GpuTransparencyOpaqueOrOtherVisible.ToString(CultureInfo.InvariantCulture),
            $"opaque/other | masked {TransparencyStats.GpuTransparencyMaskedVisible} | approximate {TransparencyStats.GpuTransparencyApproximateVisible} | exact {TransparencyStats.GpuTransparencyExactVisible}"));
    }

    private static void CaptureGpuMeshlets(List<NativeProfilerDisplayRow> rows)
    {
        if (MeshletStats.GpuMeshletRequestedFrames <= 0 && MeshletStats.GpuMeshletProductionFrames <= 0 &&
            MeshletStats.GpuMeshletFallbackFrames <= 0 && MeshletStats.LastVisibleMeshletCount <= 0)
            return;
        rows.Add(new("GPU meshlet frames", MeshletStats.GpuMeshletRequestedFrames.ToString(CultureInfo.InvariantCulture),
            $"requested | production {MeshletStats.GpuMeshletProductionFrames} | fallback {MeshletStats.GpuMeshletFallbackFrames}"));
        rows.Add(new("GPU meshlet task records", MeshletStats.GpuMeshletTaskRecordsEmitted.ToString(CultureInfo.InvariantCulture),
            $"emitted | culled frustum {MeshletStats.GpuMeshletTaskRecordsFrustumCulled} | cone {MeshletStats.GpuMeshletTaskRecordsConeCulled} | Hi-Z {MeshletStats.GpuMeshletTaskRecordsHiZCulled}"));
        rows.Add(new("GPU meshlets visible", MeshletStats.LastVisibleMeshletCount.ToString(CultureInfo.InvariantCulture),
            $"dispatched {MeshletStats.LastDispatchedMeshletCount} | overflow {MeshletStats.LastTaskRecordOverflowCount} | readback {MeshletStats.LastReadbackBytes} bytes"));
        rows.Add(new("GPU meshlet dispatch", MeshletStats.LastDispatchTime.TotalMilliseconds.ToString("F3", CultureInfo.InvariantCulture), "ms"));
    }

    private static void CaptureOpenXr(List<NativeProfilerDisplayRow> rows)
    {
        if (!VrStats.LastFrameHadVrWork)
            return;
        rows.Add(new("VR visible", VrStats.VrLeftEyeVisible.ToString(CultureInfo.InvariantCulture),
            $"left | right {VrStats.VrRightEyeVisible} | draws left/right {VrStats.VrLeftEyeDraws}/{VrStats.VrRightEyeDraws}"));
        rows.Add(new("VR build", VrStats.VrLeftWorkerBuildTimeMs.ToString("F3", CultureInfo.InvariantCulture),
            $"ms left | right {VrStats.VrRightWorkerBuildTimeMs:F3} ms"));
        rows.Add(new("VR submit", VrStats.VrRenderSubmitTimeMs.ToString("F3", CultureInfo.InvariantCulture),
            $"ms render | xrEndFrame {VrStats.VrXrEndFrameSubmitTimeMs:F3} ms | xrWaitFrame block {VrStats.VrXrWaitFrameBlockTimeMs:F3} ms"));
        string lead = double.IsFinite(VrStats.VrXrPredictedDisplayLeadTimeMs)
            ? VrStats.VrXrPredictedDisplayLeadTimeMs.ToString("F3", CultureInfo.InvariantCulture)
            : "unavailable";
        rows.Add(new("VR predicted display lead", lead,
            $"ms | predicted to late pose {VrStats.VrXrPredictedToLatePoseDeltaMillimeters:F2} mm / {VrStats.VrXrPredictedToLatePoseDeltaDegrees:F2} deg"));
        rows.Add(new("VR missed deadlines", VrStats.VrXrMissedDeadlineFrames.ToString(CultureInfo.InvariantCulture),
            $"frames | tracking loss {VrStats.VrXrTrackingLossFrames} frames"));
        rows.Add(new("VR collect relocate", VrStats.VrXrRelocatePredictedTimeMs.ToString("F3", CultureInfo.InvariantCulture),
            $"ms | frustum pad {VrStats.VrXrCollectFrustumExpansionDegrees:F2} deg"));
        rows.Add(new("VR pacing idle", VrStats.VrXrPacingThreadIdleTimeMs.ToString("F3", CultureInfo.InvariantCulture),
            $"ms | handoff stalls {VrStats.VrXrPacingHandoffStalls}"));
    }

    private static void CaptureSkinnedBounds(List<NativeProfilerDisplayRow> rows)
    {
        if (!SkinnedBoundsStats.SkinnedBoundsStatsReady)
        {
            rows.Add(new("Skinned bounds refresh", "Waiting", "No skinned-bounds statistics are available."));
            return;
        }
        int deferredFinished = SkinnedBoundsStats.SkinnedBoundsDeferredCompletedCount + SkinnedBoundsStats.SkinnedBoundsDeferredFailedCount;
        int gpuCompleted = SkinnedBoundsStats.SkinnedBoundsGpuCompletedCount;
        rows.Add(new("Skinned bounds deferred", SkinnedBoundsStats.SkinnedBoundsDeferredScheduledCount.ToString(CultureInfo.InvariantCulture),
            $"scheduled | completed {SkinnedBoundsStats.SkinnedBoundsDeferredCompletedCount} | failed {SkinnedBoundsStats.SkinnedBoundsDeferredFailedCount} | in flight {SkinnedBoundsStats.SkinnedBoundsDeferredInFlightCount} (max {SkinnedBoundsStats.SkinnedBoundsDeferredMaxInFlightCount})"));
        rows.Add(new("Skinned bounds queue wait", SkinnedBoundsStats.SkinnedBoundsDeferredQueueWaitMs.ToString("F3", CultureInfo.InvariantCulture),
            $"ms total | avg {Average(SkinnedBoundsStats.SkinnedBoundsDeferredQueueWaitMs, deferredFinished):F3} | max {SkinnedBoundsStats.SkinnedBoundsDeferredMaxQueueWaitMs:F3}"));
        rows.Add(new("Skinned bounds CPU job", SkinnedBoundsStats.SkinnedBoundsDeferredCpuJobMs.ToString("F3", CultureInfo.InvariantCulture),
            $"ms total | avg {Average(SkinnedBoundsStats.SkinnedBoundsDeferredCpuJobMs, deferredFinished):F3} | max {SkinnedBoundsStats.SkinnedBoundsDeferredMaxCpuJobMs:F3}"));
        rows.Add(new("Skinned bounds apply", SkinnedBoundsStats.SkinnedBoundsDeferredApplyMs.ToString("F3", CultureInfo.InvariantCulture),
            $"ms total | avg {Average(SkinnedBoundsStats.SkinnedBoundsDeferredApplyMs, deferredFinished):F3} | max {SkinnedBoundsStats.SkinnedBoundsDeferredMaxApplyMs:F3}"));
        rows.Add(new("Skinned bounds GPU", gpuCompleted.ToString(CultureInfo.InvariantCulture),
            $"completed | compute avg/max {Average(SkinnedBoundsStats.SkinnedBoundsGpuComputeMs, gpuCompleted):F3}/{SkinnedBoundsStats.SkinnedBoundsGpuMaxComputeMs:F3} ms | apply avg/max {Average(SkinnedBoundsStats.SkinnedBoundsGpuApplyMs, gpuCompleted):F3}/{SkinnedBoundsStats.SkinnedBoundsGpuMaxApplyMs:F3} ms"));
    }

    private static double Average(double total, int count)
        => count <= 0 ? 0.0 : total / count;

    private static void CaptureVulkanCommandChains(List<NativeProfilerDisplayRow> rows)
    {
        rows.Add(new("Vulkan command chains", VulkanStats.VulkanCommandChainsScheduled.ToString(CultureInfo.InvariantCulture),
            $"scheduled | recorded {VulkanStats.VulkanCommandChainsRecorded} | reused {VulkanStats.VulkanCommandChainsReused} | refreshed {VulkanStats.VulkanCommandChainsFrameDataRefreshed} | volatile {VulkanStats.VulkanVolatileCommandChainsRecorded} | secondary {VulkanStats.VulkanSecondaryCommandBufferCount} | packets V/R {VulkanStats.VulkanVisibilityPacketCount}/{VulkanStats.VulkanRenderPacketCount}"));
        rows.Add(new("Vulkan chain workers", VulkanStats.VulkanCommandChainWorkerQueuedChains.ToString(CultureInfo.InvariantCulture),
            $"queued | started/completed {VulkanStats.VulkanCommandChainWorkersStarted}/{VulkanStats.VulkanCommandChainWorkersCompleted} | peak {VulkanStats.VulkanCommandChainPeakConcurrentWorkers} | serial/reused/conflicts {VulkanStats.VulkanCommandChainSeriallyRecorded}/{VulkanStats.VulkanCommandChainWorkerReused}/{VulkanStats.VulkanCommandChainWorkerConflicts} | failures/timeouts {VulkanStats.VulkanCommandChainWorkerFailures}/{VulkanStats.VulkanCommandChainWorkerWaitTimeouts}"));
        rows.Add(new("Vulkan chain timing", VulkanStats.VulkanCommandChainWorkerRecordMs.ToString("F3", CultureInfo.InvariantCulture),
            $"ms record | queue {VulkanStats.VulkanCommandChainWorkerQueueDelayMs:F3} | active {VulkanStats.VulkanCommandChainWorkerActiveSpanMs:F3} | overlap {VulkanStats.VulkanCommandChainWorkerOverlapMs:F3} | merge {VulkanStats.VulkanCommandChainWorkerMergeMs:F3} | render-thread wait {VulkanStats.VulkanRenderThreadWaitForChainWorkersMs:F3} ms | primary reused/recorded {VulkanStats.VulkanPrimaryCommandBuffersReused}/{VulkanStats.VulkanPrimaryCommandBuffersRecorded}"));
        rows.Add(new("Vulkan retired drains", VulkanStats.VulkanRetiredDescriptorPoolCount.ToString(CultureInfo.InvariantCulture),
            $"descriptor pools | pipelines {VulkanStats.VulkanRetiredPipelineCount} | framebuffers {VulkanStats.VulkanRetiredFramebufferCount} | buffers {VulkanStats.VulkanRetiredBufferCount}/{VulkanStats.VulkanRetiredBufferMemoryCount} mem | images {VulkanStats.VulkanRetiredImageCount}/{VulkanStats.VulkanRetiredImageMemoryCount} mem | views {VulkanStats.VulkanRetiredImageViewCount} | samplers {VulkanStats.VulkanRetiredSamplerCount} | image {VulkanStats.VulkanRetiredImageBytes / (1024.0 * 1024.0):F2} MiB"));
        if (!string.IsNullOrEmpty(VulkanStats.VulkanCommandBufferDirtySummary))
            rows.Add(new("Vulkan dirty reasons", string.Empty, VulkanStats.VulkanCommandBufferDirtySummary));
        if (!string.IsNullOrEmpty(VulkanStats.VulkanFirstCommandChainStructuralDirtyReason))
            rows.Add(new("Vulkan chain structural", string.Empty, VulkanStats.VulkanFirstCommandChainStructuralDirtyReason));
        if (!string.IsNullOrEmpty(VulkanStats.VulkanFirstCommandChainDescriptorGenerationMismatch))
            rows.Add(new("Vulkan chain descriptor", string.Empty, VulkanStats.VulkanFirstCommandChainDescriptorGenerationMismatch));
        if (!string.IsNullOrEmpty(VulkanStats.VulkanFirstCommandChainResourcePlanRevisionMismatch))
            rows.Add(new("Vulkan chain resource plan", string.Empty, VulkanStats.VulkanFirstCommandChainResourcePlanRevisionMismatch));
    }
}
