using System;
using System.ComponentModel;
using System.Threading.Tasks;
using XREngine;
using XREngine.Data.Core;
using GpuPipelineStats = XREngine.Engine.Rendering.Stats.GpuPipelineProfiler;
using VrStats = XREngine.Engine.Rendering.Stats.Vr;
using VulkanStats = XREngine.Engine.Rendering.Stats.Vulkan;

namespace XREngine.Editor.Mcp
{
    public sealed partial class EditorMcpActions
    {
        [XRMcp(Name = "dump_cpu_frame_profile", Permission = McpPermissionLevel.ReadOnly)]
        [Description("Dump the latest CPU profiler frame snapshot to an LLM-readable log file in the current Build/Logs run directory.")]
        public static Task<McpToolResponse> DumpCpuFrameProfileAsync(McpToolContext context)
        {
            ProfilerDiagnosticDumps.DumpResult result = ProfilerDiagnosticDumps.DumpCpuFrameTimingHistory();
            return Task.FromResult(new McpToolResponse(
                result.Message,
                new
                {
                    files = result.FileNames,
                    paths = ProfilerDiagnosticDumps.BuildAbsoluteLogPaths(result.FileNames),
                    log_directory = ProfilerDiagnosticDumps.GetCurrentLogDirectory(),
                    error = result.Error
                },
                isError: !result.Success));
        }

        [XRMcp(Name = "dump_gpu_render_pipeline_profile", Permission = McpPermissionLevel.ReadOnly)]
        [Description("Dump retained GPU timing history for one render pipeline, or all captured pipelines when pipeline_name is omitted.")]
        public static Task<McpToolResponse> DumpGpuRenderPipelineProfileAsync(
            McpToolContext context,
            [McpName("pipeline_name"), Description("Render pipeline root name to dump. Omit to dump all captured render pipelines.")]
            string? pipelineName = null,
            [McpName("all_pipelines"), Description("When true, dump all captured render pipelines regardless of pipeline_name.")]
            bool allPipelines = false)
        {
            ProfilerDiagnosticDumps.DumpResult result = allPipelines
                ? ProfilerDiagnosticDumps.DumpAllGpuRenderPipelineTimingHistories()
                : ProfilerDiagnosticDumps.DumpGpuRenderPipelineTimingHistory(pipelineName);

            string[] availablePipelines = result.Success
                ? []
                : ProfilerDiagnosticDumps.GetAvailableGpuRenderPipelineNames();

            return Task.FromResult(new McpToolResponse(
                result.Message,
                new
                {
                    files = result.FileNames,
                    paths = ProfilerDiagnosticDumps.BuildAbsoluteLogPaths(result.FileNames),
                    log_directory = ProfilerDiagnosticDumps.GetCurrentLogDirectory(),
                    available_pipelines = availablePipelines,
                    error = result.Error
                },
                isError: !result.Success));
        }

        [XRMcp(Name = "get_render_profiler_stats", Permission = McpPermissionLevel.ReadOnly)]
        [Description("Return the latest render-profiler counters, including Vulkan frame lifecycle timings and command-buffer cache state.")]
        public static Task<McpToolResponse> GetRenderProfilerStatsAsync(McpToolContext context)
        {
            return Task.FromResult(new McpToolResponse(
                "Retrieved render profiler stats.",
                new
                {
                    gpu_pipeline = new
                    {
                        enabled = GpuPipelineStats.GpuRenderPipelineProfilingEnabled,
                        supported = GpuPipelineStats.GpuRenderPipelineProfilingSupported,
                        timings_ready = GpuPipelineStats.GpuRenderPipelineTimingsReady,
                        backend = GpuPipelineStats.GpuRenderPipelineBackend,
                        status = GpuPipelineStats.GpuRenderPipelineStatusMessage,
                        frame_ms = GpuPipelineStats.GpuRenderPipelineFrameMs,
                    },
                    vr = new
                    {
                        left_eye_draws = VrStats.VrLeftEyeDraws,
                        right_eye_draws = VrStats.VrRightEyeDraws,
                        left_eye_visible = VrStats.VrLeftEyeVisible,
                        right_eye_visible = VrStats.VrRightEyeVisible,
                        left_worker_build_ms = JsonFinite(VrStats.VrLeftWorkerBuildTimeMs),
                        right_worker_build_ms = JsonFinite(VrStats.VrRightWorkerBuildTimeMs),
                        render_submit_ms = JsonFinite(VrStats.VrRenderSubmitTimeMs),
                        xr_wait_frame_block_ms = JsonFinite(VrStats.VrXrWaitFrameBlockTimeMs),
                        xr_end_frame_submit_ms = JsonFinite(VrStats.VrXrEndFrameSubmitTimeMs),
                        predicted_to_late_pose_delta_mm = JsonFinite(VrStats.VrXrPredictedToLatePoseDeltaMillimeters),
                        predicted_to_late_pose_delta_degrees = JsonFinite(VrStats.VrXrPredictedToLatePoseDeltaDegrees),
                        predicted_display_lead_time_ms = JsonFinite(VrStats.VrXrPredictedDisplayLeadTimeMs),
                        missed_deadline_frames = VrStats.VrXrMissedDeadlineFrames,
                        tracking_loss_frames = VrStats.VrXrTrackingLossFrames,
                        relocate_predicted_time_ms = JsonFinite(VrStats.VrXrRelocatePredictedTimeMs),
                        collect_frustum_expansion_degrees = JsonFinite(VrStats.VrXrCollectFrustumExpansionDegrees),
                        pacing_thread_idle_ms = JsonFinite(VrStats.VrXrPacingThreadIdleTimeMs),
                        pacing_handoff_stalls = VrStats.VrXrPacingHandoffStalls,
                    },
                    vulkan = new
                    {
                        frame_lifecycle = new
                        {
                            total_ms = VulkanStats.VulkanFrameTotalMs,
                            gpu_command_buffer_ms = VulkanStats.VulkanFrameGpuCommandBufferMs,
                            wait_fence_ms = VulkanStats.VulkanFrameWaitFenceMs,
                            sample_timing_queries_ms = VulkanStats.VulkanFrameSampleTimingQueriesMs,
                            drain_retired_resources_ms = VulkanStats.VulkanFrameDrainRetiredResourcesMs,
                            acquire_image_ms = VulkanStats.VulkanFrameAcquireImageMs,
                            acquire_bridge_submit_ms = VulkanStats.VulkanFrameAcquireBridgeSubmitMs,
                            wait_swapchain_image_ms = VulkanStats.VulkanFrameWaitSwapchainImageMs,
                            reset_dynamic_uniform_ring_ms = VulkanStats.VulkanFrameResetDynamicUniformRingMs,
                            record_command_buffer_ms = VulkanStats.VulkanFrameRecordCommandBufferMs,
                            submit_ms = VulkanStats.VulkanFrameSubmitMs,
                            trim_ms = VulkanStats.VulkanFrameTrimMs,
                            present_ms = VulkanStats.VulkanFramePresentMs,
                        },
                        command_buffer_cache = new
                        {
                            clean_reuse_count = VulkanStats.VulkanCommandBufferCleanReuseCount,
                            record_count = VulkanStats.VulkanCommandBufferRecordCount,
                            forced_dirty_count = VulkanStats.VulkanCommandBufferForcedDirtyCount,
                            frame_op_signature_dirty_count = VulkanStats.VulkanCommandBufferFrameOpSignatureDirtyCount,
                            planner_dirty_count = VulkanStats.VulkanCommandBufferPlannerDirtyCount,
                            profiler_dirty_count = VulkanStats.VulkanCommandBufferProfilerDirtyCount,
                            dirty_summary = VulkanStats.VulkanCommandBufferDirtySummary,
                            record_allocated_bytes = VulkanStats.VulkanRecordCommandBufferAllocatedBytes,
                        },
                        command_chains = new
                        {
                            chains_scheduled = VulkanStats.VulkanCommandChainsScheduled,
                            chains_recorded = VulkanStats.VulkanCommandChainsRecorded,
                            chains_reused = VulkanStats.VulkanCommandChainsReused,
                            chains_frame_data_refreshed = VulkanStats.VulkanCommandChainsFrameDataRefreshed,
                            volatile_chains_recorded = VulkanStats.VulkanVolatileCommandChainsRecorded,
                            primary_command_buffers_reused = VulkanStats.VulkanPrimaryCommandBuffersReused,
                            primary_command_buffers_recorded = VulkanStats.VulkanPrimaryCommandBuffersRecorded,
                            visibility_packet_count = VulkanStats.VulkanVisibilityPacketCount,
                            render_packet_count = VulkanStats.VulkanRenderPacketCount,
                            secondary_command_buffer_count = VulkanStats.VulkanSecondaryCommandBufferCount,
                            chain_worker_record_ms = VulkanStats.VulkanCommandChainWorkerRecordMs,
                            render_thread_wait_for_workers_ms = VulkanStats.VulkanRenderThreadWaitForChainWorkersMs,
                            first_structural_dirty_reason = VulkanStats.VulkanFirstCommandChainStructuralDirtyReason,
                            first_descriptor_generation_mismatch = VulkanStats.VulkanFirstCommandChainDescriptorGenerationMismatch,
                            first_resource_plan_revision_mismatch = VulkanStats.VulkanFirstCommandChainResourcePlanRevisionMismatch,
                        },
                        frame_ops = new
                        {
                            total_count = VulkanStats.VulkanFrameOpTotalCount,
                            clear_count = VulkanStats.VulkanFrameOpClearCount,
                            mesh_draw_count = VulkanStats.VulkanFrameOpMeshDrawCount,
                            indirect_draw_count = VulkanStats.VulkanFrameOpIndirectDrawCount,
                            mesh_task_dispatch_count = VulkanStats.VulkanFrameOpMeshTaskDispatchCount,
                            blit_count = VulkanStats.VulkanFrameOpBlitCount,
                            compute_count = VulkanStats.VulkanFrameOpComputeCount,
                            swapchain_write_count = VulkanStats.VulkanFrameOpSwapchainWriteCount,
                            fbo_write_count = VulkanStats.VulkanFrameOpFboWriteCount,
                            unique_pass_count = VulkanStats.VulkanFrameOpUniquePassCount,
                            unique_context_count = VulkanStats.VulkanFrameOpUniqueContextCount,
                            unique_target_count = VulkanStats.VulkanFrameOpUniqueTargetCount,
                        },
                        descriptors = new
                        {
                            pool_create_count = VulkanStats.VulkanDescriptorPoolCreateCount,
                            pool_destroy_count = VulkanStats.VulkanDescriptorPoolDestroyCount,
                            pool_reset_count = VulkanStats.VulkanDescriptorPoolResetCount,
                            fallback_sampled_images = VulkanStats.VulkanDescriptorFallbackSampledImages,
                            fallback_storage_images = VulkanStats.VulkanDescriptorFallbackStorageImages,
                            fallback_uniform_buffers = VulkanStats.VulkanDescriptorFallbackUniformBuffers,
                            fallback_storage_buffers = VulkanStats.VulkanDescriptorFallbackStorageBuffers,
                            fallback_texel_buffers = VulkanStats.VulkanDescriptorFallbackTexelBuffers,
                            binding_failures = VulkanStats.VulkanDescriptorBindingFailures,
                            skipped_draws = VulkanStats.VulkanDescriptorSkippedDraws,
                            skipped_dispatches = VulkanStats.VulkanDescriptorSkippedDispatches,
                            fallback_summary = VulkanStats.VulkanDescriptorFallbackSummary,
                            failure_summary = VulkanStats.VulkanDescriptorFailureSummary,
                            dynamic_uniform_allocations = VulkanStats.VulkanDynamicUniformAllocations,
                            dynamic_uniform_allocated_bytes = VulkanStats.VulkanDynamicUniformAllocatedBytes,
                            dynamic_uniform_exhaustions = VulkanStats.VulkanDynamicUniformExhaustions,
                        },
                        retired_resources = new
                        {
                            plan_replacements = VulkanStats.VulkanRetiredResourcePlanReplacements,
                            plan_images = VulkanStats.VulkanRetiredResourcePlanImages,
                            plan_buffers = VulkanStats.VulkanRetiredResourcePlanBuffers,
                            descriptor_pool_count = VulkanStats.VulkanRetiredDescriptorPoolCount,
                            pipeline_count = VulkanStats.VulkanRetiredPipelineCount,
                            framebuffer_count = VulkanStats.VulkanRetiredFramebufferCount,
                            buffer_count = VulkanStats.VulkanRetiredBufferCount,
                            buffer_memory_count = VulkanStats.VulkanRetiredBufferMemoryCount,
                            image_count = VulkanStats.VulkanRetiredImageCount,
                            image_view_count = VulkanStats.VulkanRetiredImageViewCount,
                            sampler_count = VulkanStats.VulkanRetiredSamplerCount,
                            image_memory_count = VulkanStats.VulkanRetiredImageMemoryCount,
                            image_bytes = VulkanStats.VulkanRetiredImageBytes,
                        },
                        validation = new
                        {
                            message_count = VulkanStats.VulkanValidationMessageCount,
                            error_count = VulkanStats.VulkanValidationErrorCount,
                            last_message = VulkanStats.VulkanLastValidationMessage,
                        },
                        diagnostics = new
                        {
                            dropped_frame_ops = VulkanStats.VulkanDroppedFrameOps,
                            dropped_draw_ops = VulkanStats.VulkanDroppedDrawOps,
                            dropped_compute_ops = VulkanStats.VulkanDroppedComputeOps,
                            scene_swapchain_writers = VulkanStats.VulkanSceneSwapchainWriters,
                            overlay_swapchain_writers = VulkanStats.VulkanOverlaySwapchainWriters,
                            missing_scene_swapchain_write_frames = VulkanStats.VulkanMissingSceneSwapchainWriteFrames,
                            frame_diagnostic_summary = VulkanStats.VulkanFrameDiagnosticSummary,
                        },
                    },
                }));
        }

        private static double? JsonFinite(double value)
            => double.IsFinite(value) ? value : null;
    }
}
