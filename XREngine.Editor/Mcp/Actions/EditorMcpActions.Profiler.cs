using System;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using XREngine;
using XREngine.Data.Core;
using XREngine.Rendering.Occlusion;
using GpuPipelineStats = XREngine.Engine.Rendering.Stats.GpuPipelineProfiler;
using OcclusionTelemetry = XREngine.Rendering.Occlusion.OcclusionTelemetry;
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
                    frame_lifecycle = new
                    {
                        collect_visible_late_policy = Engine.Rendering.Stats.FrameLifecycle.CollectVisibleLatePolicy,
                        requested_collect_generation = Engine.Rendering.Stats.FrameLifecycle.RequestedCollectGeneration,
                        completed_collect_generation = Engine.Rendering.Stats.FrameLifecycle.CompletedCollectGeneration,
                        published_collect_generation = Engine.Rendering.Stats.FrameLifecycle.PublishedCollectGeneration,
                        consumed_collect_generation = Engine.Rendering.Stats.FrameLifecycle.ConsumedCollectGeneration,
                        required_collect_generation = Engine.Rendering.Stats.FrameLifecycle.RequiredCollectGeneration,
                        collect_wait_for_render_ms = Engine.Rendering.Stats.FrameLifecycle.CollectWaitForRenderMs,
                        render_wait_for_collect_ms = Engine.Rendering.Stats.FrameLifecycle.RenderWaitForCollectMs,
                        render_wait_reason = Engine.Rendering.Stats.FrameLifecycle.RenderWaitReason,
                        stale_collect_reuse_frames = Engine.Rendering.Stats.FrameLifecycle.StaleCollectReuseFrames,
                    },
                    gpu_pipeline = new
                    {
                        enabled = GpuPipelineStats.GpuRenderPipelineProfilingEnabled,
                        supported = GpuPipelineStats.GpuRenderPipelineProfilingSupported,
                        timings_ready = GpuPipelineStats.GpuRenderPipelineTimingsReady,
                        backend = GpuPipelineStats.GpuRenderPipelineBackend,
                        status = GpuPipelineStats.GpuRenderPipelineStatusMessage,
                        frame_ms = GpuPipelineStats.GpuRenderPipelineFrameMs,
                    },
                    frame_outputs = BuildFrameOutputManifest(Engine.Rendering.Stats.FrameOutputs.LastManifest),
                    occlusion = new
                    {
                        effective_mode = OcclusionTelemetry.LastEffectiveMode.ToString(),
                        submission_strategy = OcclusionTelemetry.LastSubmissionStrategy.ToString(),
                        cpu_passes_active = OcclusionTelemetry.CpuPassesActive,
                        cpu_passes_skipped_no_camera = OcclusionTelemetry.CpuPassesSkippedNoCamera,
                        cpu_passes_skipped_shadow = OcclusionTelemetry.CpuPassesSkippedShadow,
                        cpu_passes_skipped_depth_normal_prepass = OcclusionTelemetry.CpuPassesSkippedDepthNormalPrePass,
                        cpu_passes_skipped_mode_off = OcclusionTelemetry.CpuPassesSkippedModeOff,
                        cpu_tested = OcclusionTelemetry.CpuTested,
                        cpu_culled = OcclusionTelemetry.CpuCulled,
                        cpu_rendered = OcclusionTelemetry.CpuRendered,
                        cpu_decision_seed = OcclusionTelemetry.CpuDecisionSeed,
                        cpu_decision_cached = OcclusionTelemetry.CpuDecisionCached,
                        cpu_decision_visible_query = OcclusionTelemetry.CpuDecisionVisibleQuery,
                        cpu_decision_visible_hysteresis = OcclusionTelemetry.CpuDecisionVisibleHysteresis,
                        cpu_decision_probe = OcclusionTelemetry.CpuDecisionProbe,
                        cpu_decision_skip = OcclusionTelemetry.CpuDecisionSkip,
                        cpu_decision_forced_visible = OcclusionTelemetry.CpuDecisionForcedVisible,
                        cpu_motion_tier = OcclusionTelemetry.CpuMotionTier.ToString(),
                        cpu_active_view_scope = OcclusionTelemetry.CpuActiveViewScope.ToString(),
                        cpu_global_conservative_frames = OcclusionTelemetry.CpuGlobalConservativeFrames,
                        cpu_pending_queries = OcclusionTelemetry.CpuPendingQueries,
                        cpu_query_submitted_total = OcclusionTelemetry.CpuQuerySubmittedTotal,
                        cpu_query_resolved_total = OcclusionTelemetry.CpuQueryResolvedTotal,
                        cpu_query_latency_samples = OcclusionTelemetry.CpuQueryLatencySamples,
                        cpu_query_latency_average_frames = OcclusionTelemetry.CpuQueryLatencyAverageFrames,
                        cpu_query_latency_max_frames = OcclusionTelemetry.CpuQueryLatencyMaxFrames,
                        cpu_budget_skipped_total = OcclusionTelemetry.CpuBudgetSkippedTotal,
                        cpu_forced_visible_total = OcclusionTelemetry.CpuForcedVisibleTotal,
                        cpu_forced_visible_reasons = Enum.GetValues<ECpuOcclusionForceVisibleReason>()
                            .Select(reason => new
                            {
                                reason = reason.ToString(),
                                count = OcclusionTelemetry.GetCpuForcedVisibleCount(reason),
                            })
                            .Where(static entry => entry.count > 0)
                            .ToArray(),
                        cpu_query_submitted_reasons = Enum.GetValues<ECpuOcclusionQueryReason>()
                            .Select(reason => new
                            {
                                reason = reason.ToString(),
                                count = OcclusionTelemetry.GetCpuQuerySubmittedCount(reason),
                            })
                            .Where(static entry => entry.count > 0)
                            .ToArray(),
                        cpu_query_resolved_reasons = Enum.GetValues<ECpuOcclusionQueryReason>()
                            .Select(reason => new
                            {
                                reason = reason.ToString(),
                                count = OcclusionTelemetry.GetCpuQueryResolvedCount(reason),
                            })
                            .Where(static entry => entry.count > 0)
                            .ToArray(),
                        cpu_unsupported_stereo_query_mode = OcclusionTelemetry.CpuUnsupportedStereoQueryMode,
                        cpu_query_async_submitted = OcclusionTelemetry.CpuQueryAsyncSubmitted,
                        cpu_query_async_resolved = OcclusionTelemetry.CpuQueryAsyncResolved,
                        cpu_query_async_occluded = OcclusionTelemetry.CpuQueryAsyncOccluded,
                        cpu_soc_tested = OcclusionTelemetry.CpuSocTested,
                        cpu_soc_culled = OcclusionTelemetry.CpuSocCulled,
                        cpu_view_snapshots = OcclusionTelemetry.GetCpuViewSnapshots(),
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
                            snapshot_imgui_overlay_ms = VulkanStats.VulkanFrameSnapshotImGuiOverlayMs,
                            record_scene_command_buffer_ms = VulkanStats.VulkanFrameRecordSceneCommandBufferMs,
                            record_imgui_overlay_ms = VulkanStats.VulkanFrameRecordImGuiOverlayMs,
                            record_dynamic_ui_text_overlay_ms = VulkanStats.VulkanFrameRecordDynamicUiTextOverlayMs,
                            submit_ms = VulkanStats.VulkanFrameSubmitMs,
                            trim_ms = VulkanStats.VulkanFrameTrimMs,
                            present_ms = VulkanStats.VulkanFramePresentMs,
                        },
                        cpu_stages = new
                        {
                            frame_op_preparation = VulkanCpuStage(EVulkanCpuStage.FrameOpPreparation),
                            resource_planning = VulkanCpuStage(EVulkanCpuStage.ResourcePlanning),
                            frame_data_refresh = VulkanCpuStage(EVulkanCpuStage.FrameDataRefresh),
                            packet_construction = VulkanCpuStage(EVulkanCpuStage.PacketConstruction),
                            primary_recording = VulkanCpuStage(EVulkanCpuStage.PrimaryRecording),
                            secondary_recording = VulkanCpuStage(EVulkanCpuStage.SecondaryRecording),
                            descriptor_publication = VulkanCpuStage(EVulkanCpuStage.DescriptorPublication),
                            submission = VulkanCpuStage(EVulkanCpuStage.Submission),
                        },
                        command_buffer_cache = new
                        {
                            clean_reuse_count = VulkanStats.VulkanCommandBufferCleanReuseCount,
                            record_count = VulkanStats.VulkanCommandBufferRecordCount,
                            forced_dirty_count = VulkanStats.VulkanCommandBufferForcedDirtyCount,
                            frame_op_signature_dirty_count = VulkanStats.VulkanCommandBufferFrameOpSignatureDirtyCount,
                            planner_dirty_count = VulkanStats.VulkanCommandBufferPlannerDirtyCount,
                            profiler_dirty_count = VulkanStats.VulkanCommandBufferProfilerDirtyCount,
                            decision_reason_mask = (int)VulkanStats.VulkanCommandBufferDecisionReasonMask,
                            decision_reasons = VulkanStats.VulkanCommandBufferDecisionReasonMask.ToString(),
                            decision_visibility_generation = VulkanStats.VulkanCommandBufferDecisionVisibilityGeneration,
                            decision_structural_signature = VulkanStats.VulkanCommandBufferDecisionStructuralSignature,
                            decision_descriptor_generation = VulkanStats.VulkanCommandBufferDecisionDescriptorGeneration,
                            decision_swapchain_slot = VulkanStats.VulkanCommandBufferDecisionSwapchainSlot,
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
                            mesh_frame_data_arena_chunks = VulkanStats.VulkanMeshFrameDataArenaChunkCount,
                            mesh_frame_data_mapped_bytes = VulkanStats.VulkanMeshFrameDataMappedBytes,
                            mesh_frame_data_reserved_bytes = VulkanStats.VulkanMeshFrameDataReservedBytes,
                            mesh_frame_data_reservations = VulkanStats.VulkanMeshFrameDataReservationCount,
                            mesh_frame_data_generation = VulkanStats.VulkanMeshFrameDataGeneration,
                            mesh_frame_data_recording_leases = VulkanStats.VulkanMeshFrameDataRecordingLeases,
                            mesh_frame_data_cached_leases = VulkanStats.VulkanMeshFrameDataCachedLeases,
                            mesh_frame_data_submitted_leases = VulkanStats.VulkanMeshFrameDataSubmittedLeases,
                            mesh_frame_data_active_generations = VulkanStats.VulkanMeshFrameDataActiveGenerationCount,
                            mesh_frame_data_lease_retained_generations = VulkanStats.VulkanMeshFrameDataLeaseRetainedGenerationCount,
                            mesh_descriptor_allocation_variants = VulkanStats.VulkanMeshDescriptorAllocationVariants,
                            mesh_descriptor_pools = VulkanStats.VulkanMeshDescriptorPools,
                            mesh_descriptor_allocated_sets = VulkanStats.VulkanMeshDescriptorAllocatedSets,
                            mesh_descriptor_reserved_sets = VulkanStats.VulkanMeshDescriptorReservedSets,
                            mesh_frame_data_arena_chunk_high_water = VulkanStats.VulkanMeshFrameDataArenaChunkHighWater,
                            mesh_frame_data_mapped_bytes_high_water = VulkanStats.VulkanMeshFrameDataMappedBytesHighWater,
                            mesh_frame_data_reserved_bytes_high_water = VulkanStats.VulkanMeshFrameDataReservedBytesHighWater,
                            mesh_frame_data_reservation_high_water = VulkanStats.VulkanMeshFrameDataReservationHighWater,
                            mesh_frame_data_lease_high_water = VulkanStats.VulkanMeshFrameDataLeaseHighWater,
                            mesh_descriptor_allocation_variant_high_water = VulkanStats.VulkanMeshDescriptorAllocationVariantHighWater,
                            mesh_descriptor_pool_high_water = VulkanStats.VulkanMeshDescriptorPoolHighWater,
                            mesh_descriptor_set_high_water = VulkanStats.VulkanMeshDescriptorSetHighWater,
                        },
                        retired_resources = new
                        {
                            pending_count = VulkanStats.VulkanLifetimePendingRetirementCount,
                            oldest_pending_age_ms = VulkanStats.VulkanLifetimeOldestPendingRetirementAgeMilliseconds,
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

        private static object BuildFrameOutputManifest(Engine.Rendering.Stats.FrameOutputManifestSnapshot snapshot)
        {
            Engine.Rendering.Stats.FrameOutputEntrySnapshot[] outputs = snapshot.Outputs ?? [];
            object[] outputData = new object[outputs.Length];
            for (int i = 0; i < outputs.Length; i++)
            {
                Engine.Rendering.Stats.FrameOutputEntrySnapshot output = outputs[i];
                outputData[i] = new
                {
                    frame_id = output.FrameId,
                    output_kind = output.OutputKindName,
                    view_kind = output.ViewKindName,
                    output_id = output.Request.OutputId,
                    view_family_id = output.Request.ViewFamilyId,
                    output_class = output.Request.OutputClass.ToString(),
                    priority = output.Request.Schedule.Priority.ToString(),
                    target_class = output.Request.Target.TargetClass.ToString(),
                    stable_target_id = output.Request.Target.StableTargetId,
                    target_generation = output.Request.Target.TargetGeneration,
                    display_width = output.Request.Target.DisplayWidth,
                    display_height = output.Request.Target.DisplayHeight,
                    internal_width = output.Request.Target.InternalWidth,
                    internal_height = output.Request.Target.InternalHeight,
                    target_compatibility_key = output.Request.Target.CompatibilityKey,
                    sample_count = output.Request.Target.SampleCount,
                    view_mask = output.Request.Target.ViewMask,
                    external_image_slot = output.Request.Target.ExternalImageSlot,
                    desired_rate_hz = output.Request.Schedule.DesiredRateHz,
                    deadline_ms = JsonFinite(output.Request.Schedule.DeadlineMs),
                    max_cpu_budget_ms = JsonFinite(output.Request.Schedule.MaxCpuBudgetMs),
                    max_gpu_budget_ms = JsonFinite(output.Request.Schedule.MaxGpuBudgetMs),
                    max_content_age_frames = output.Request.Schedule.MaxContentAgeFrames,
                    hard_deadline = output.Request.Schedule.HardDeadline,
                    quality_requirements = output.Request.QualityRequirements.ToString(),
                    fallback_policy = output.Request.FallbackPolicy.ToString(),
                    completion_requirement = output.Request.CompletionRequirement.ToString(),
                    producer_dependency_set_id = output.Request.ProducerDependencySetId,
                    consumer_dependency_set_id = output.Request.ConsumerDependencySetId,
                    work_disposition = output.WorkDisposition.ToString(),
                    content_age_frames = output.ContentAgeFrames,
                    deadline_missed = output.DeadlineMissed,
                    policy_authorized = output.PolicyAuthorized,
                    policy_reason = output.PolicyReason.ToString(),
                    name = output.Name,
                    pipeline = output.PipelineName,
                    active = output.Active,
                    rendered = output.Rendered,
                    scene_rendered = output.SceneRendered,
                    render_phase_scene_rendered = output.RenderPhaseSceneRendered,
                    mirror = output.Mirror,
                    separate_scene_render = output.SeparateSceneRender,
                    shared_visibility = output.SharedVisibility,
                    due = output.Due,
                    skipped = output.Skipped,
                    cadence_skipped = output.CadenceSkipped,
                    auto_skipped = output.AutoSkipped,
                    skip_reason = output.SkipReasonName,
                    configured_target_rate_hz = output.ConfiguredTargetRateHz,
                    source_rate_hz = output.SourceRateHz,
                    achieved_rate_hz = JsonFinite(output.AchievedRateHz),
                    total_render_count = output.TotalRenderCount,
                    total_skip_count = output.TotalSkipCount,
                    command_count = output.CommandCount,
                    collect_cpu_ms = JsonFinite(output.CollectCpuMs),
                    swap_cpu_ms = JsonFinite(output.SwapCpuMs),
                    render_cpu_ms = JsonFinite(output.RenderCpuMs),
                    submit_cpu_ms = JsonFinite(output.SubmitCpuMs),
                    overlay_cpu_ms = JsonFinite(output.OverlayCpuMs),
                    present_cpu_ms = JsonFinite(output.PresentCpuMs),
                    gpu_ms = JsonFinite(output.GpuMs),
                    summary = output.Summary,
                };
            }

            return new
            {
                frame_id = snapshot.FrameId,
                vr_active = snapshot.VrActive,
                mirror_mode = snapshot.MirrorMode.ToString(),
                visibility_policy = snapshot.VisibilityPolicy.ToString(),
                budget_band = snapshot.BudgetBand,
                budget_ms = JsonFinite(snapshot.BudgetMs),
                whole_frame_ms = JsonFinite(snapshot.WholeFrameMs),
                whole_frame_p50_ms = JsonFinite(snapshot.WholeFrameP50Ms),
                whole_frame_p90_ms = JsonFinite(snapshot.WholeFrameP90Ms),
                whole_frame_p95_ms = JsonFinite(snapshot.WholeFrameP95Ms),
                whole_frame_p99_ms = JsonFinite(snapshot.WholeFrameP99Ms),
                whole_frame_worst_ms = JsonFinite(snapshot.WholeFrameWorstMs),
                workload_identity_hash = snapshot.WorkloadIdentityHash,
                output_request_count = snapshot.Work.OutputRequestCount,
                output_event_count = snapshot.Work.OutputEventCount,
                collect_event_count = snapshot.Work.CollectEventCount,
                swap_event_count = snapshot.Work.SwapEventCount,
                render_event_count = snapshot.Work.RenderEventCount,
                submit_event_count = snapshot.Work.SubmitEventCount,
                overlay_event_count = snapshot.Work.OverlayEventCount,
                present_event_count = snapshot.Work.PresentEventCount,
                unique_view_family_count = snapshot.Work.UniqueViewFamilyCount,
                target_variant_count = snapshot.Work.TargetVariantCount,
                scene_snapshot_count = snapshot.Work.SceneSnapshotCount,
                visibility_build_count = snapshot.Work.VisibilityBuildCount,
                compiled_plan_cache_hits = snapshot.Work.CompiledPlanCacheHits,
                compiled_plan_cache_misses = snapshot.Work.CompiledPlanCacheMisses,
                shared_pass_reuse_count = snapshot.Work.SharedPassReuseCount,
                recorded_work_item_count = snapshot.Work.RecordedWorkItemCount,
                reused_work_item_count = snapshot.Work.ReusedWorkItemCount,
                duplicated_work_item_count = snapshot.Work.DuplicatedWorkItemCount,
                cpu_budget_deferral_count = snapshot.Work.CpuBudgetDeferralCount,
                gpu_budget_deferral_count = snapshot.Work.GpuBudgetDeferralCount,
                stale_result_reuse_count = snapshot.Work.StaleResultReuseCount,
                missed_deadline_count = snapshot.Work.MissedDeadlineCount,
                unapproved_policy_event_count = snapshot.Work.UnapprovedPolicyEventCount,
                submission_rejection_count = snapshot.Work.SubmissionRejectionCount,
                planner_prune_count = snapshot.Work.PlannerPruneCount,
                global_in_flight_wait_count = snapshot.Work.GlobalInFlightWaitCount,
                force_flush_count = snapshot.Work.ForceFlushCount,
                outputs = outputData,
            };
        }

        private static object VulkanCpuStage(EVulkanCpuStage stage)
            => new
            {
                elapsed_ms = VulkanStats.VulkanCpuStageMs(stage),
                allocated_bytes = VulkanStats.VulkanCpuStageAllocatedBytes(stage),
                allocation_high_water_bytes = VulkanStats.VulkanCpuStageAllocationHighWaterBytes(stage),
            };

        private static double? JsonFinite(double value)
            => double.IsFinite(value) ? value : null;
    }
}
