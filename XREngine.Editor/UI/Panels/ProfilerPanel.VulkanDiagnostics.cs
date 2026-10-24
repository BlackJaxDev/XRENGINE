using System.Globalization;
using XREngine.Rendering.Vulkan;
using VulkanStats = XREngine.RuntimeEngine.Rendering.Stats.Vulkan;

namespace XREngine.Editor;

public partial class ProfilerPanel
{
    private VulkanFrameTelemetryPublication _lastVulkanPublication;
    private bool _hasVulkanPublication;

    private void CaptureVulkanFrameDiagnostics(List<NativeProfilerDisplayRow> rows)
    {
        if (!RuntimeEngine.Rendering.State.IsVulkan)
        {
            _hasVulkanPublication = false;
            _lastVulkanPublication = default;
            return;
        }
        if (VulkanStats.TryGetLatestVulkanFrameTelemetry(out VulkanFrameTelemetryPublication current) &&
            current.PublicationSequence > 0)
        {
            _lastVulkanPublication = current;
            _hasVulkanPublication = true;
        }
        if (_hasVulkanPublication)
            NativeProfilerVulkanTelemetryRows.Append(rows, _lastVulkanPublication);
        else
            rows.Add(new("Vulkan correlated frame", "Waiting", "No complete Vulkan frame publication is available."));

        rows.Add(new("Vulkan last-frame counters", VulkanStats.VulkanQueueSubmitCount.ToString(CultureInfo.InvariantCulture),
            "Queue submits; these counters use the render-stat frame cadence, separate from the correlated publication above."));
        rows.Add(new("Vulkan binds", VulkanStats.VulkanPipelineBinds.ToString(CultureInfo.InvariantCulture),
            $"pipeline (skipped {VulkanStats.VulkanPipelineBindSkips}) | descriptors {VulkanStats.VulkanDescriptorBinds} (skipped {VulkanStats.VulkanDescriptorBindSkips}) | push constants {VulkanStats.VulkanPushConstantWrites} | vertex/index {VulkanStats.VulkanVertexBufferBinds}/{VulkanStats.VulkanIndexBufferBinds} (skipped {VulkanStats.VulkanVertexBufferBindSkips}/{VulkanStats.VulkanIndexBufferBindSkips})"));
        rows.Add(new("Vulkan pipeline cache", VulkanStats.VulkanPipelineCacheLookupHits.ToString(CultureInfo.InvariantCulture),
            $"hits/misses {VulkanStats.VulkanPipelineCacheLookupHits}/{VulkanStats.VulkanPipelineCacheLookupMisses} | driver persisted/runtime/miss/unknown {VulkanStats.VulkanDriverPipelineCachePersistedHits}/{VulkanStats.VulkanDriverPipelineCacheRuntimeHits}/{VulkanStats.VulkanDriverPipelineCacheMisses}/{VulkanStats.VulkanDriverPipelineCacheUnknown}"));
        rows.Add(new("Vulkan pipeline compile", VulkanStats.VulkanPipelineCompileCompletedCount.ToString(CultureInfo.InvariantCulture),
            $"completed (background {VulkanStats.VulkanPipelineBackgroundCompileCompletedCount}) | required {VulkanStats.VulkanPipelineCompileRequiredCount} | total/max {VulkanStats.VulkanPipelineCompileTotalMs:F3}/{VulkanStats.VulkanPipelineCompileMaxMs:F3} ms | render-thread {VulkanStats.VulkanRenderThreadShaderCompileCount}"));
        rows.Add(new("Vulkan pipeline queue", VulkanStats.VulkanPipelineQueueDepthHighWater.ToString(CultureInfo.InvariantCulture),
            $"high-water/capacity {VulkanStats.VulkanPipelineQueueDepthHighWater}/{VulkanStats.VulkanPipelineQueueCapacity} | queued/rejected {VulkanStats.VulkanPipelineAsyncQueuedCount}/{VulkanStats.VulkanPipelineQueueRejectedCount} | required pending {VulkanStats.VulkanRequiredPipelinePendingCount} | deferred/draw-not-ready {VulkanStats.VulkanPipelineRecordDeferredCount}/{VulkanStats.VulkanPipelineDrawNotReadyCount}"));
        if (!string.IsNullOrEmpty(VulkanStats.VulkanPipelineCacheMissSummary))
            rows.Add(new("Vulkan pipeline misses", string.Empty, VulkanStats.VulkanPipelineCacheMissSummary));

        rows.Add(new("Vulkan frame ops", VulkanStats.VulkanFrameOpTotalCount.ToString(CultureInfo.InvariantCulture),
            $"clear/mesh/indirect/mesh-task/blit/compute {VulkanStats.VulkanFrameOpClearCount}/{VulkanStats.VulkanFrameOpMeshDrawCount}/{VulkanStats.VulkanFrameOpIndirectDrawCount}/{VulkanStats.VulkanFrameOpMeshTaskDispatchCount}/{VulkanStats.VulkanFrameOpBlitCount}/{VulkanStats.VulkanFrameOpComputeCount}"));
        rows.Add(new("Vulkan targets", VulkanStats.VulkanFrameOpSwapchainWriteCount.ToString(CultureInfo.InvariantCulture),
            $"swapchain/FBO writes {VulkanStats.VulkanFrameOpSwapchainWriteCount}/{VulkanStats.VulkanFrameOpFboWriteCount} | passes/contexts/targets {VulkanStats.VulkanFrameOpUniquePassCount}/{VulkanStats.VulkanFrameOpUniqueContextCount}/{VulkanStats.VulkanFrameOpUniqueTargetCount} | scene/overlay/diagnostic writers {VulkanStats.VulkanSceneSwapchainWriters}/{VulkanStats.VulkanOverlaySwapchainWriters}/{VulkanStats.VulkanForcedDiagnosticSwapchainWriters}"));
        rows.Add(new("Vulkan dropped ops", VulkanStats.VulkanDroppedFrameOps.ToString(CultureInfo.InvariantCulture),
            $"total | draws {VulkanStats.VulkanDroppedDrawOps} | compute {VulkanStats.VulkanDroppedComputeOps} | FBO-only draws/blits {VulkanStats.VulkanFboOnlyDrawOps}/{VulkanStats.VulkanFboOnlyBlitOps} | missing scene writers {VulkanStats.VulkanMissingSceneSwapchainWriteFrames}"));
        rows.Add(new("Vulkan command buffers", VulkanStats.VulkanCommandBufferCleanReuseCount.ToString(CultureInfo.InvariantCulture),
            $"clean reuse | records {VulkanStats.VulkanCommandBufferRecordCount} | dirty forced/frame-op/planner/profiler {VulkanStats.VulkanCommandBufferForcedDirtyCount}/{VulkanStats.VulkanCommandBufferFrameOpSignatureDirtyCount}/{VulkanStats.VulkanCommandBufferPlannerDirtyCount}/{VulkanStats.VulkanCommandBufferProfilerDirtyCount}"));
        rows.Add(new("Vulkan GPU command buffer", VulkanStats.VulkanFrameGpuCommandBufferMs.ToString("F3", CultureInfo.InvariantCulture),
            $"ms | timing {VulkanStats.VulkanFrameGpuCommandBufferTimingAvailability}"));

        rows.Add(new("Vulkan descriptor fallback", VulkanStats.VulkanDescriptorBindingFailures.ToString(CultureInfo.InvariantCulture),
            $"failures | sampled/storage image {VulkanStats.VulkanDescriptorFallbackSampledImages}/{VulkanStats.VulkanDescriptorFallbackStorageImages} | uniform/storage/texel buffers {VulkanStats.VulkanDescriptorFallbackUniformBuffers}/{VulkanStats.VulkanDescriptorFallbackStorageBuffers}/{VulkanStats.VulkanDescriptorFallbackTexelBuffers} | skipped draw/dispatch {VulkanStats.VulkanDescriptorSkippedDraws}/{VulkanStats.VulkanDescriptorSkippedDispatches}"));
        if (!string.IsNullOrEmpty(VulkanStats.VulkanDescriptorFallbackSummary))
            rows.Add(new("Vulkan fallback details", string.Empty, VulkanStats.VulkanDescriptorFallbackSummary));
        if (!string.IsNullOrEmpty(VulkanStats.VulkanDescriptorFailureSummary))
            rows.Add(new("Vulkan descriptor failures", string.Empty, VulkanStats.VulkanDescriptorFailureSummary));
        rows.Add(new("Vulkan dynamic UBO", VulkanStats.VulkanDynamicUniformAllocations.ToString(CultureInfo.InvariantCulture),
            $"allocations | bytes {VulkanStats.VulkanDynamicUniformAllocatedBytes} | exhaustions {VulkanStats.VulkanDynamicUniformExhaustions}"));
        rows.Add(new("Vulkan retired plans", VulkanStats.VulkanRetiredResourcePlanReplacements.ToString(CultureInfo.InvariantCulture),
            $"replacements | images {VulkanStats.VulkanRetiredResourcePlanImages} | buffers {VulkanStats.VulkanRetiredResourcePlanBuffers}"));
        rows.Add(new("Vulkan validation", VulkanStats.VulkanValidationErrorCount.ToString(CultureInfo.InvariantCulture),
            $"errors | messages {VulkanStats.VulkanValidationMessageCount} | layers/sync {VulkanStats.VulkanValidationLayersEnabled}/{VulkanStats.VulkanSynchronizationValidationEnabled}"));
        if (!string.IsNullOrEmpty(VulkanStats.VulkanLastValidationMessage))
            rows.Add(new("Vulkan last validation", string.Empty, VulkanStats.VulkanLastValidationMessage));
        if (!string.IsNullOrEmpty(VulkanStats.VulkanFirstFailedFrameOpType))
        {
            rows.Add(new("Vulkan first failed op", VulkanStats.VulkanFirstFailedFrameOpType,
                $"pass/pipeline/viewport {VulkanStats.VulkanFirstFailedFrameOpPassIndex}/{VulkanStats.VulkanFirstFailedFrameOpPipelineIdentity}/{VulkanStats.VulkanFirstFailedFrameOpViewportIdentity} | target {VulkanStats.VulkanFirstFailedFrameOpTargetName} | material/shader {VulkanStats.VulkanFirstFailedFrameOpMaterialName}/{VulkanStats.VulkanFirstFailedFrameOpShaderName}"));
            if (!string.IsNullOrEmpty(VulkanStats.VulkanFirstFailedFrameOpMessage))
                rows.Add(new("Vulkan failed op detail", string.Empty, VulkanStats.VulkanFirstFailedFrameOpMessage));
        }
        if (!string.IsNullOrEmpty(VulkanStats.VulkanFrameDiagnosticSummary))
            rows.Add(new("Vulkan diagnostic bundle", string.Empty, VulkanStats.VulkanFrameDiagnosticSummary));
    }
}
