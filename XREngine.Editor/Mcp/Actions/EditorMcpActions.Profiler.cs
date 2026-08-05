using System;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using XREngine;
using XREngine.Data.Core;
using XREngine.Rendering;
using XREngine.Rendering.Occlusion;
using GpuDrivenStats = XREngine.RuntimeEngine.Rendering.Stats.GpuDriven;
using GpuPipelineStats = XREngine.RuntimeEngine.Rendering.Stats.GpuPipelineProfiler;
using OcclusionTelemetry = XREngine.Rendering.Occlusion.OcclusionTelemetry;
using VrStats = XREngine.RuntimeEngine.Rendering.Stats.Vr;
using VulkanStats = XREngine.RuntimeEngine.Rendering.Stats.Vulkan;

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
                        collect_visible_late_policy = RuntimeEngine.Rendering.Stats.FrameLifecycle.CollectVisibleLatePolicy,
                        requested_collect_generation = RuntimeEngine.Rendering.Stats.FrameLifecycle.RequestedCollectGeneration,
                        completed_collect_generation = RuntimeEngine.Rendering.Stats.FrameLifecycle.CompletedCollectGeneration,
                        published_collect_generation = RuntimeEngine.Rendering.Stats.FrameLifecycle.PublishedCollectGeneration,
                        consumed_collect_generation = RuntimeEngine.Rendering.Stats.FrameLifecycle.ConsumedCollectGeneration,
                        required_collect_generation = RuntimeEngine.Rendering.Stats.FrameLifecycle.RequiredCollectGeneration,
                        collect_wait_for_render_ms = RuntimeEngine.Rendering.Stats.FrameLifecycle.CollectWaitForRenderMs,
                        render_wait_for_collect_ms = RuntimeEngine.Rendering.Stats.FrameLifecycle.RenderWaitForCollectMs,
                        render_wait_reason = RuntimeEngine.Rendering.Stats.FrameLifecycle.RenderWaitReason,
                        stale_collect_reuse_frames = RuntimeEngine.Rendering.Stats.FrameLifecycle.StaleCollectReuseFrames,
                        frame_package_production_ms = RuntimeEngine.Rendering.Stats.FrameLifecycle.FramePackageProductionMs,
                        frame_package_publication_ms = RuntimeEngine.Rendering.Stats.FrameLifecycle.FramePackagePublicationMs,
                        frame_package_validation_ms = RuntimeEngine.Rendering.Stats.FrameLifecycle.FramePackageValidationMs,
                        frame_package_consumption_ms = RuntimeEngine.Rendering.Stats.FrameLifecycle.FramePackageConsumptionMs,
                        frame_packages_prepared = RuntimeEngine.Rendering.Stats.FrameLifecycle.FramePackagesPrepared,
                        frame_packages_published = RuntimeEngine.Rendering.Stats.FrameLifecycle.FramePackagesPublished,
                        frame_packages_consumed = RuntimeEngine.Rendering.Stats.FrameLifecycle.FramePackagesConsumed,
                        frame_packages_prepared_late = RuntimeEngine.Rendering.Stats.FrameLifecycle.FramePackagesPreparedLate,
                        frame_packages_rejected = RuntimeEngine.Rendering.Stats.FrameLifecycle.FramePackagesRejected,
                        frame_package_generation_age = RuntimeEngine.Rendering.Stats.FrameLifecycle.FramePackageGenerationAge,
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
                    scene = new
                    {
                        tracked_renderables = context.WorldInstance.VisualScene.Renderables.Count,
                        gpu_commands = new
                        {
                            total_count = context.WorldInstance.VisualScene.GPUCommands.TotalCommandCount,
                            allocated_capacity = context.WorldInstance.VisualScene.GPUCommands.AllocatedMaxCommandCount,
                            skinned_count = context.WorldInstance.VisualScene.GPUCommands.SkinnedCommandCount,
                        },
                    },
                    gpu_driven = new
                    {
                        culled_command_count = GpuDrivenStats.CulledCommandCount,
                        active_bucket_count = GpuDrivenStats.ActiveBucketCount,
                        empty_bucket_skips = GpuDrivenStats.EmptyBucketSkips,
                        full_bucket_scans = GpuDrivenStats.FullBucketScans,
                        material_scatter_dispatches = GpuDrivenStats.MaterialScatterDispatches,
                        configured_material_slots = GpuDrivenStats.ConfiguredMaterialSlots,
                        material_pass_groups = GpuDrivenStats.MaterialPassGroups,
                        unsupported_compact_passes = GpuDrivenStats.UnsupportedCompactPasses,
                        command_capacity = GpuDrivenStats.CommandCapacity,
                        active_command_count = GpuDrivenStats.ActiveCommandCount,
                        material_lookup_capacity = GpuDrivenStats.MaterialLookupCapacity,
                        active_material_slots = GpuDrivenStats.ActiveMaterialSlots,
                        required_material_rows = GpuDrivenStats.RequiredMaterialRows,
                        ready_material_rows = GpuDrivenStats.ReadyMaterialRows,
                        non_ready_material_texture_references = GpuDrivenStats.NonReadyMaterialTextureReferences,
                        invalid_material_ids = GpuDrivenStats.InvalidMaterialIds,
                        fallback_submitted_material_rows = GpuDrivenStats.FallbackSubmittedMaterialRows,
                        material_table_publication_generation = GpuDrivenStats.MaterialTablePublicationGeneration,
                        material_descriptor_publication_generation = GpuDrivenStats.MaterialDescriptorPublicationGeneration,
                        submission_managed_allocated_bytes = GpuDrivenStats.SubmissionManagedAllocatedBytes,
                        submission_backend_managed_allocated_bytes = GpuDrivenStats.SubmissionBackendManagedAllocatedBytes,
                        submission_owned_managed_allocated_bytes = GpuDrivenStats.SubmissionOwnedManagedAllocatedBytes,
                        delayed_diagnostic_readback_bytes = GpuDrivenStats.DelayedDiagnosticReadbackBytes,
                        delayed_diagnostic_readback_count = GpuDrivenStats.DelayedDiagnosticReadbackCount,
                        gpu_compaction_overflow = GpuDrivenStats.GpuCompactionOverflow,
                        active_list_overflow = GpuDrivenStats.ActiveListOverflow,
                        bucket_overflow = GpuDrivenStats.BucketOverflow,
                        meshlet_overflow = GpuDrivenStats.MeshletOverflow,
                        hiz_mode = GpuDrivenStats.HiZMode,
                        material_binding_rung = GpuDrivenStats.MaterialBindingRung,
                        material_binding_rung_reason = GpuDrivenStats.MaterialBindingRungReason,
                        gpu_compaction_rung = GpuDrivenStats.GpuCompactionRung,
                        gpu_compaction_rung_reason = GpuDrivenStats.GpuCompactionRungReason,
                    },
                    frame_outputs = BuildFrameOutputManifest(RuntimeEngine.Rendering.Stats.FrameOutputs.LastManifest),
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
                        openxr_eye_primary_record_span_ms = JsonFinite(VrStats.VrOpenXrEyePrimaryRecordSpanMs),
                        openxr_eye_primary_record_overlap_ms = JsonFinite(VrStats.VrOpenXrEyePrimaryRecordOverlapMs),
                        openxr_eye_primary_record_overlap_ratio = JsonFinite(VrStats.VrOpenXrEyePrimaryRecordOverlapRatio),
                        process_openxr_eye_primary_record_samples = VrStats.VrProcessOpenXrEyePrimaryRecordSamples,
                        process_openxr_eye_primary_record_span_ms = JsonFinite(VrStats.VrProcessOpenXrEyePrimaryRecordSpanMs),
                        process_openxr_eye_primary_record_overlap_ms = JsonFinite(VrStats.VrProcessOpenXrEyePrimaryRecordOverlapMs),
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
                            frame_data_manifest = VulkanCpuStage(EVulkanCpuStage.FrameDataManifest),
                            dependency_snapshot = VulkanCpuStage(EVulkanCpuStage.DependencySnapshot),
                            image_layout_snapshot = VulkanCpuStage(EVulkanCpuStage.ImageLayoutSnapshot),
                            command_buffer_reuse = VulkanCpuStage(EVulkanCpuStage.CommandBufferReuse),
                            submission_preparation = VulkanCpuStage(EVulkanCpuStage.SubmissionPreparation),
                            submission_diagnostics = VulkanCpuStage(EVulkanCpuStage.SubmissionDiagnostics),
                            submission_image_state_validation = VulkanCpuStage(EVulkanCpuStage.SubmissionImageStateValidation),
                            submission_resource_lifetime_validation = VulkanCpuStage(EVulkanCpuStage.SubmissionResourceLifetimeValidation),
                            queue_submit = VulkanCpuStage(EVulkanCpuStage.QueueSubmit),
                            submission_publication = VulkanCpuStage(EVulkanCpuStage.SubmissionPublication),
                            command_chain_fast_signature = VulkanCpuStage(EVulkanCpuStage.CommandChainFastSignature),
                            command_chain_packet_lowering = VulkanCpuStage(EVulkanCpuStage.CommandChainPacketLowering),
                            command_chain_schedule_evaluation = VulkanCpuStage(EVulkanCpuStage.CommandChainScheduleEvaluation),
                            primary_frame_data_manifest = VulkanCpuStage(EVulkanCpuStage.PrimaryFrameDataManifest),
                            primary_prewarm = VulkanCpuStage(EVulkanCpuStage.PrimaryPrewarm),
                            primary_command_encoding = VulkanCpuStage(EVulkanCpuStage.PrimaryCommandEncoding),
                            context_pass_transitions = VulkanCpuStage(EVulkanCpuStage.ContextPassTransitions),
                            barrier_planning_emission = VulkanCpuStage(EVulkanCpuStage.BarrierPlanningEmission),
                            op_dispatch = VulkanCpuStage(EVulkanCpuStage.OpDispatch),
                            mesh_draw_preparation = VulkanCpuStage(EVulkanCpuStage.MeshDrawPreparation),
                            mesh_draw_resource_preparation = VulkanCpuStage(EVulkanCpuStage.MeshDrawResourcePreparation),
                            mesh_draw_binding_preparation = VulkanCpuStage(EVulkanCpuStage.MeshDrawBindingPreparation),
                            mesh_draw_material_bindings = VulkanCpuStage(EVulkanCpuStage.MeshDrawMaterialBindings),
                            mesh_draw_binding_snapshot_copy = VulkanCpuStage(EVulkanCpuStage.MeshDrawBindingSnapshotCopy),
                            mesh_draw_enqueue = VulkanCpuStage(EVulkanCpuStage.MeshDrawEnqueue),
                            frame_data_descriptor_validation = VulkanCpuStage(EVulkanCpuStage.FrameDataDescriptorValidation),
                            frame_data_engine_uniform_upload = VulkanCpuStage(EVulkanCpuStage.FrameDataEngineUniformUpload),
                            frame_data_auto_uniform_upload = VulkanCpuStage(EVulkanCpuStage.FrameDataAutoUniformUpload),
                            prepared_draw_construction = VulkanCpuStage(EVulkanCpuStage.PreparedDrawConstruction),
                            secondary_merge = VulkanCpuStage(EVulkanCpuStage.SecondaryMerge),
                            command_dependency_comparison = VulkanCpuStage(EVulkanCpuStage.CommandDependencyComparison),
                            command_dirty_propagation = VulkanCpuStage(EVulkanCpuStage.CommandDirtyPropagation),
                            command_cache_scanning = VulkanCpuStage(EVulkanCpuStage.CommandCacheScanning),
                            frame_op_drain = VulkanCpuStage(EVulkanCpuStage.FrameOpDrain),
                            frame_op_scheduling = VulkanCpuStage(EVulkanCpuStage.FrameOpScheduling),
                            frame_op_sort = VulkanCpuStage(EVulkanCpuStage.FrameOpSort),
                            frame_op_cohort = VulkanCpuStage(EVulkanCpuStage.FrameOpCohort),
                            frame_op_split = VulkanCpuStage(EVulkanCpuStage.FrameOpSplit),
                            frame_op_signature = VulkanCpuStage(EVulkanCpuStage.FrameOpSignature),
                            frame_op_plan = VulkanCpuStage(EVulkanCpuStage.FrameOpPlan),
                            mesh_draw_publisher_state = VulkanCpuStage(EVulkanCpuStage.MeshDrawPublisherState),
                            mesh_draw_artifact_eligibility = VulkanCpuStage(EVulkanCpuStage.MeshDrawArtifactEligibility),
                            mesh_draw_artifact_lookup = VulkanCpuStage(EVulkanCpuStage.MeshDrawArtifactLookup),
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
                            reset_command_buffer_calls = VulkanStats.VulkanResetCommandBufferCalls,
                            reset_command_pool_calls = VulkanStats.VulkanResetCommandPoolCalls,
                            allocate_command_buffer_calls = VulkanStats.VulkanAllocateCommandBufferCalls,
                            command_buffers_allocated = VulkanStats.VulkanCommandBuffersAllocated,
                            execute_secondary_command_buffer_calls = VulkanStats.VulkanExecuteSecondaryCommandBufferCalls,
                            secondary_command_buffers_invoked = VulkanStats.VulkanSecondaryCommandBuffersInvoked,
                            process_reset_command_buffer_calls = VulkanStats.VulkanProcessResetCommandBufferCalls,
                            process_reset_command_pool_calls = VulkanStats.VulkanProcessResetCommandPoolCalls,
                            process_allocate_command_buffer_calls = VulkanStats.VulkanProcessAllocateCommandBufferCalls,
                            process_command_buffers_allocated = VulkanStats.VulkanProcessCommandBuffersAllocated,
                            process_execute_secondary_command_buffer_calls = VulkanStats.VulkanProcessExecuteSecondaryCommandBufferCalls,
                            process_secondary_command_buffers_invoked = VulkanStats.VulkanProcessSecondaryCommandBuffersInvoked,
                            process_worker_secondary_command_buffer_reset_calls = VulkanStats.VulkanProcessWorkerSecondaryCommandBufferResetCalls,
                            process_worker_secondary_command_buffer_allocations = VulkanStats.VulkanProcessWorkerSecondaryCommandBufferAllocations,
                            process_worker_secondary_replacement_allocations = VulkanStats.VulkanProcessWorkerSecondaryReplacementAllocations,
                            visible_mesh_draws = VulkanStats.VulkanVisibleMeshDraws,
                            unique_visible_materials = VulkanStats.VulkanUniqueVisibleMaterials,
                            prepared_mesh_draws = VulkanStats.VulkanPreparedMeshDraws,
                            recorded_command_artifact_retirements = VulkanStats.VulkanRecordedCommandArtifactRetirements,
                        },
                        binding_data = new
                        {
                            material_payload_cache_hits = VulkanStats.VulkanMaterialPayloadCacheHits,
                            material_payload_cache_misses = VulkanStats.VulkanMaterialPayloadCacheMisses,
                            material_payloads_packed = VulkanStats.VulkanMaterialPayloadsPacked,
                            material_uniforms_packed = VulkanStats.VulkanMaterialUniformsPacked,
                            material_parameter_emissions = VulkanStats.VulkanMaterialParameterEmissions,
                            material_dictionary_writes = VulkanStats.VulkanMaterialDictionaryWrites,
                            frame_material_snapshot_cache_hits = VulkanStats.VulkanFrameMaterialSnapshotCacheHits,
                            frame_material_snapshot_cache_misses = VulkanStats.VulkanFrameMaterialSnapshotCacheMisses,
                            program_binding_artifact_builds = VulkanStats.VulkanProgramBindingArtifactBuilds,
                            program_binding_artifact_reuses = VulkanStats.VulkanProgramBindingArtifactReuses,
                            program_binding_artifact_fallbacks = VulkanStats.VulkanProgramBindingArtifactFallbacks,
                            program_binding_allocation_breakdown_bytes = new
                            {
                                setup = VulkanStats.GetVulkanProgramBindingAllocationBytes(EVulkanProgramBindingAllocationSegment.Setup),
                                publisher_scope = VulkanStats.GetVulkanProgramBindingAllocationBytes(EVulkanProgramBindingAllocationSegment.PublisherScope),
                                eligibility_gap = VulkanStats.GetVulkanProgramBindingAllocationBytes(EVulkanProgramBindingAllocationSegment.EligibilityGap),
                                eligibility_scope = VulkanStats.GetVulkanProgramBindingAllocationBytes(EVulkanProgramBindingAllocationSegment.EligibilityScope),
                                artifact_key_and_generation = VulkanStats.GetVulkanProgramBindingAllocationBytes(EVulkanProgramBindingAllocationSegment.ArtifactKeyAndGeneration),
                                lookup_scope = VulkanStats.GetVulkanProgramBindingAllocationBytes(EVulkanProgramBindingAllocationSegment.LookupScope),
                                reuse_publication = VulkanStats.GetVulkanProgramBindingAllocationBytes(EVulkanProgramBindingAllocationSegment.ReusePublication),
                            },
                            program_binding_artifact_fallback_reasons = new
                            {
                                shadow_pass = VulkanStats.GetVulkanProgramBindingArtifactFallbackReasonCount(EVulkanProgramBindingArtifactFallbackReason.ShadowPass),
                                renderer_callback = VulkanStats.GetVulkanProgramBindingArtifactFallbackReasonCount(EVulkanProgramBindingArtifactFallbackReason.RendererCallback),
                                material_callback = VulkanStats.GetVulkanProgramBindingArtifactFallbackReasonCount(EVulkanProgramBindingArtifactFallbackReason.MaterialCallback),
                                active_scoped_bindings = VulkanStats.GetVulkanProgramBindingArtifactFallbackReasonCount(EVulkanProgramBindingArtifactFallbackReason.ActiveScopedBindings),
                                pipeline_variables = VulkanStats.GetVulkanProgramBindingArtifactFallbackReasonCount(EVulkanProgramBindingArtifactFallbackReason.PipelineVariables),
                                unsupported_engine_requirements = VulkanStats.GetVulkanProgramBindingArtifactFallbackReasonCount(EVulkanProgramBindingArtifactFallbackReason.UnsupportedEngineRequirements),
                                missing_lighting_owner = VulkanStats.GetVulkanProgramBindingArtifactFallbackReasonCount(EVulkanProgramBindingArtifactFallbackReason.MissingLightingOwner),
                                lighting_publication_unavailable = VulkanStats.GetVulkanProgramBindingArtifactFallbackReasonCount(EVulkanProgramBindingArtifactFallbackReason.LightingPublicationUnavailable),
                                ambient_occlusion_only = VulkanStats.GetVulkanProgramBindingArtifactFallbackReasonCount(EVulkanProgramBindingArtifactFallbackReason.AmbientOcclusionOnly),
                                mutable_legacy_uniform = VulkanStats.GetVulkanProgramBindingArtifactFallbackReasonCount(EVulkanProgramBindingArtifactFallbackReason.MutableLegacyUniform),
                                unowned_descriptor_resource = VulkanStats.GetVulkanProgramBindingArtifactFallbackReasonCount(EVulkanProgramBindingArtifactFallbackReason.UnownedDescriptorResource),
                                unowned_uniform = VulkanStats.GetVulkanProgramBindingArtifactFallbackReasonCount(EVulkanProgramBindingArtifactFallbackReason.UnownedUniform),
                                incomplete_runtime_uniform_publication = VulkanStats.GetVulkanProgramBindingArtifactFallbackReasonCount(EVulkanProgramBindingArtifactFallbackReason.IncompleteRuntimeUniformPublication),
                                artifact_content_unsupported = VulkanStats.GetVulkanProgramBindingArtifactFallbackReasonCount(EVulkanProgramBindingArtifactFallbackReason.ArtifactContentUnsupported),
                                invalid_publisher_state = VulkanStats.GetVulkanProgramBindingArtifactFallbackReasonCount(EVulkanProgramBindingArtifactFallbackReason.InvalidPublisherState),
                                publisher_changed_during_publication = VulkanStats.GetVulkanProgramBindingArtifactFallbackReasonCount(EVulkanProgramBindingArtifactFallbackReason.PublisherChangedDuringPublication),
                            },
                            program_binding_artifact_fallback_samples =
                                GetVulkanProgramBindingArtifactFallbackSamples(),
                            binding_snapshots_captured = VulkanStats.VulkanBindingSnapshotsCaptured,
                            binding_snapshot_entries = VulkanStats.VulkanBindingSnapshotEntries,
                            fast_path_binding_snapshots = VulkanStats.VulkanFastPathBindingSnapshots,
                            legacy_binding_snapshots = VulkanStats.VulkanLegacyBindingSnapshots,
                            auto_uniform_plan_cache_hits = VulkanStats.VulkanAutoUniformPlanCacheHits,
                            auto_uniform_plan_cache_misses = VulkanStats.VulkanAutoUniformPlanCacheMisses,
                            auto_uniform_static_bytes_copied = VulkanStats.VulkanAutoUniformStaticBytesCopied,
                            auto_uniform_dynamic_bytes_cleared = VulkanStats.VulkanAutoUniformDynamicBytesCleared,
                            auto_uniform_dynamic_members_patched = VulkanStats.VulkanAutoUniformDynamicMembersPatched,
                            auto_uniform_reflected_members_scanned = VulkanStats.VulkanAutoUniformReflectedMembersScanned,
                            auto_uniform_legacy_full_block_bytes = VulkanStats.VulkanAutoUniformLegacyFullBlockBytes,
                            auto_uniform_fast_path_draws = VulkanStats.VulkanAutoUniformFastPathDraws,
                            auto_uniform_legacy_fallback_draws = VulkanStats.VulkanAutoUniformLegacyFallbackDraws,
                            auto_uniform_schema_mismatch_sites = new
                            {
                                block_identity_or_size = VulkanStats.GetVulkanAutoUniformSchemaMismatchSiteCount(EVulkanAutoUniformSchemaMismatchSite.BlockIdentityOrSize),
                                frequency = VulkanStats.GetVulkanAutoUniformSchemaMismatchSiteCount(EVulkanAutoUniformSchemaMismatchSite.Frequency),
                                parity = VulkanStats.GetVulkanAutoUniformSchemaMismatchSiteCount(EVulkanAutoUniformSchemaMismatchSite.Parity),
                            },
                            auto_uniform_schema_mismatch_samples =
                                GetVulkanAutoUniformSchemaMismatchSamples(),
                            frame_data_draws_visited = VulkanStats.VulkanFrameDataDrawsVisited,
                            prepared_primary_frame_data_draws_visited =
                                VulkanStats.VulkanPreparedPrimaryFrameDataDrawsVisited,
                            prepared_dynamic_ui_frame_data_draws_visited =
                                VulkanStats.VulkanPreparedDynamicUiFrameDataDrawsVisited,
                            descriptor_records_validated = VulkanStats.VulkanDescriptorRecordsValidated,
                            descriptor_records_written = VulkanStats.VulkanDescriptorRecordsWritten,
                            descriptor_owner_lookup_misses = VulkanStats.VulkanDescriptorOwnerLookupMisses,
                            descriptor_owner_generation_misses = VulkanStats.VulkanDescriptorOwnerGenerationMisses,
                            descriptor_frame_source_generation_misses = VulkanStats.VulkanDescriptorFrameSourceGenerationMisses,
                            binding_schemas_compiled = VulkanStats.VulkanBindingSchemasCompiled,
                            binding_schema_value_operations = VulkanStats.VulkanBindingSchemaValueOperations,
                            binding_schema_descriptor_entries = VulkanStats.VulkanBindingSchemaDescriptorEntries,
                            binding_schema_fallback_operations = VulkanStats.VulkanBindingSchemaFallbackOperations,
                            auto_uniform_typed_operations_executed = VulkanStats.VulkanAutoUniformTypedOperationsExecuted,
                            auto_uniform_reflected_name_lookups = VulkanStats.VulkanAutoUniformReflectedNameLookups,
                            auto_uniform_generic_conversions = VulkanStats.VulkanAutoUniformGenericConversions,
                            auto_uniform_frequency_publication = new
                            {
                                frame = VulkanFrequencyPublication(VulkanStats.VulkanBindingFrequencyFrameIndex),
                                view = VulkanFrequencyPublication(VulkanStats.VulkanBindingFrequencyViewIndex),
                                pass = VulkanFrequencyPublication(VulkanStats.VulkanBindingFrequencyPassIndex),
                                material = VulkanFrequencyPublication(VulkanStats.VulkanBindingFrequencyMaterialIndex),
                                @object = VulkanFrequencyPublication(VulkanStats.VulkanBindingFrequencyObjectIndex),
                                instance = VulkanFrequencyPublication(VulkanStats.VulkanBindingFrequencyInstanceIndex),
                                runtime_callback = VulkanFrequencyPublication(VulkanStats.VulkanBindingFrequencyRuntimeCallbackIndex),
                            },
                            auto_uniform_fallback_reasons = new
                            {
                                binding_snapshot_ineligible = VulkanStats.GetVulkanAutoUniformFallbackReasonCount(EVulkanAutoUniformFallbackReason.BindingSnapshotIneligible),
                                program_unavailable = VulkanStats.GetVulkanAutoUniformFallbackReasonCount(EVulkanAutoUniformFallbackReason.ProgramUnavailable),
                                invalid_buffer_size = VulkanStats.GetVulkanAutoUniformFallbackReasonCount(EVulkanAutoUniformFallbackReason.InvalidBufferSize),
                                binding_schema_unavailable = VulkanStats.GetVulkanAutoUniformFallbackReasonCount(EVulkanAutoUniformFallbackReason.BindingSchemaUnavailable),
                                binding_schema_mismatch = VulkanStats.GetVulkanAutoUniformFallbackReasonCount(EVulkanAutoUniformFallbackReason.BindingSchemaMismatch),
                                invalid_member_name = VulkanStats.GetVulkanAutoUniformFallbackReasonCount(EVulkanAutoUniformFallbackReason.InvalidMemberName),
                                unsupported_shader_type = VulkanStats.GetVulkanAutoUniformFallbackReasonCount(EVulkanAutoUniformFallbackReason.UnsupportedShaderType),
                                invalid_destination_range = VulkanStats.GetVulkanAutoUniformFallbackReasonCount(EVulkanAutoUniformFallbackReason.InvalidDestinationRange),
                                invalid_array_layout = VulkanStats.GetVulkanAutoUniformFallbackReasonCount(EVulkanAutoUniformFallbackReason.InvalidArrayLayout),
                                struct_snapshot_required = VulkanStats.GetVulkanAutoUniformFallbackReasonCount(EVulkanAutoUniformFallbackReason.StructSnapshotRequired),
                                engine_source_type_mismatch = VulkanStats.GetVulkanAutoUniformFallbackReasonCount(EVulkanAutoUniformFallbackReason.EngineSourceTypeMismatch),
                                mesh_state_source_type_mismatch = VulkanStats.GetVulkanAutoUniformFallbackReasonCount(EVulkanAutoUniformFallbackReason.MeshStateSourceTypeMismatch),
                                typed_engine_source_unavailable = VulkanStats.GetVulkanAutoUniformFallbackReasonCount(EVulkanAutoUniformFallbackReason.TypedEngineSourceUnavailable),
                                typed_engine_write_failed = VulkanStats.GetVulkanAutoUniformFallbackReasonCount(EVulkanAutoUniformFallbackReason.TypedEngineWriteFailed),
                                typed_temporal_write_failed = VulkanStats.GetVulkanAutoUniformFallbackReasonCount(EVulkanAutoUniformFallbackReason.TypedTemporalWriteFailed),
                                typed_mesh_state_source_unavailable = VulkanStats.GetVulkanAutoUniformFallbackReasonCount(EVulkanAutoUniformFallbackReason.TypedMeshStateSourceUnavailable),
                                typed_mesh_state_write_failed = VulkanStats.GetVulkanAutoUniformFallbackReasonCount(EVulkanAutoUniformFallbackReason.TypedMeshStateWriteFailed),
                                typed_material_or_runtime_write_failed = VulkanStats.GetVulkanAutoUniformFallbackReasonCount(EVulkanAutoUniformFallbackReason.TypedMaterialOrRuntimeWriteFailed),
                            },
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
                            worker_queued_chains = VulkanStats.VulkanCommandChainWorkerQueuedChains,
                            workers_started = VulkanStats.VulkanCommandChainWorkersStarted,
                            workers_completed = VulkanStats.VulkanCommandChainWorkersCompleted,
                            serially_recorded = VulkanStats.VulkanCommandChainSeriallyRecorded,
                            worker_reused = VulkanStats.VulkanCommandChainWorkerReused,
                            worker_conflicts = VulkanStats.VulkanCommandChainWorkerConflicts,
                            worker_failures = VulkanStats.VulkanCommandChainWorkerFailures,
                            worker_wait_timeouts = VulkanStats.VulkanCommandChainWorkerWaitTimeouts,
                            worker_eligibility = VulkanStats.VulkanLastCommandChainWorkerEligibility.ToString(),
                            worker_eligibility_counts = new
                            {
                                eligible = VulkanStats.GetVulkanCommandChainWorkerEligibilityCount(EVulkanCommandChainWorkerEligibility.Eligible),
                                too_little_independent_work = VulkanStats.GetVulkanCommandChainWorkerEligibilityCount(EVulkanCommandChainWorkerEligibility.TooLittleIndependentWork),
                                mutable_renderer_conflict = VulkanStats.GetVulkanCommandChainWorkerEligibilityCount(EVulkanCommandChainWorkerEligibility.MutableRendererConflict),
                                unsupported_operation = VulkanStats.GetVulkanCommandChainWorkerEligibilityCount(EVulkanCommandChainWorkerEligibility.UnsupportedOperation),
                                unsupported_inheritance = VulkanStats.GetVulkanCommandChainWorkerEligibilityCount(EVulkanCommandChainWorkerEligibility.UnsupportedInheritance),
                                primary_owned_indirect_stream = VulkanStats.GetVulkanCommandChainWorkerEligibilityCount(EVulkanCommandChainWorkerEligibility.PrimaryOwnedIndirectStream),
                                worker_quarantined = VulkanStats.GetVulkanCommandChainWorkerEligibilityCount(EVulkanCommandChainWorkerEligibility.WorkerQuarantined),
                                resource_preparation_failed = VulkanStats.GetVulkanCommandChainWorkerEligibilityCount(EVulkanCommandChainWorkerEligibility.ResourcePreparationFailed),
                            },
                            indirect_secondary_eligibility = VulkanStats.VulkanLastIndirectSecondaryEligibility.ToString(),
                            indirect_secondary_eligibility_counts = new
                            {
                                eligible_producer_complete = VulkanStats.GetVulkanIndirectSecondaryEligibilityCount(EVulkanIndirectSecondaryEligibility.EligibleProducerComplete),
                                mutable_current_frame = VulkanStats.GetVulkanIndirectSecondaryEligibilityCount(EVulkanIndirectSecondaryEligibility.MutableCurrentFrame),
                                producer_incomplete = VulkanStats.GetVulkanIndirectSecondaryEligibilityCount(EVulkanIndirectSecondaryEligibility.ProducerIncomplete),
                                buffer_identity_changed = VulkanStats.GetVulkanIndirectSecondaryEligibilityCount(EVulkanIndirectSecondaryEligibility.BufferIdentityChanged),
                                invalid_range = VulkanStats.GetVulkanIndirectSecondaryEligibilityCount(EVulkanIndirectSecondaryEligibility.InvalidRange),
                                command_chains_disabled = VulkanStats.GetVulkanIndirectSecondaryEligibilityCount(EVulkanIndirectSecondaryEligibility.CommandChainsDisabled),
                                unsupported_inheritance = VulkanStats.GetVulkanIndirectSecondaryEligibilityCount(EVulkanIndirectSecondaryEligibility.UnsupportedInheritance),
                                resource_preparation_failed = VulkanStats.GetVulkanIndirectSecondaryEligibilityCount(EVulkanIndirectSecondaryEligibility.ResourcePreparationFailed),
                            },
                            compute_secondary_eligibility = VulkanStats.GetVulkanLastSecondaryRecordingEligibility(EVulkanSecondaryCommandFamily.Compute).ToString(),
                            compute_secondary_eligibility_counts = CreateSecondaryRecordingEligibilityCounts(EVulkanSecondaryCommandFamily.Compute),
                            transfer_secondary_eligibility = VulkanStats.GetVulkanLastSecondaryRecordingEligibility(EVulkanSecondaryCommandFamily.Transfer).ToString(),
                            transfer_secondary_eligibility_counts = CreateSecondaryRecordingEligibilityCounts(EVulkanSecondaryCommandFamily.Transfer),
                            query_secondary_eligibility = VulkanStats.GetVulkanLastSecondaryRecordingEligibility(EVulkanSecondaryCommandFamily.Query).ToString(),
                            query_secondary_eligibility_counts = CreateSecondaryRecordingEligibilityCounts(EVulkanSecondaryCommandFamily.Query),
                            peak_concurrent_workers = VulkanStats.VulkanCommandChainPeakConcurrentWorkers,
                            worker_queue_delay_ms = VulkanStats.VulkanCommandChainWorkerQueueDelayMs,
                            chain_worker_record_ms = VulkanStats.VulkanCommandChainWorkerRecordMs,
                            worker_active_span_ms = VulkanStats.VulkanCommandChainWorkerActiveSpanMs,
                            worker_overlap_ms = VulkanStats.VulkanCommandChainWorkerOverlapMs,
                            worker_merge_ms = VulkanStats.VulkanCommandChainWorkerMergeMs,
                            render_thread_wait_for_workers_ms = VulkanStats.VulkanRenderThreadWaitForChainWorkersMs,
                            first_structural_dirty_reason = VulkanStats.VulkanFirstCommandChainStructuralDirtyReason,
                            first_descriptor_generation_mismatch = VulkanStats.VulkanFirstCommandChainDescriptorGenerationMismatch,
                            first_resource_plan_revision_mismatch = VulkanStats.VulkanFirstCommandChainResourcePlanRevisionMismatch,
                        },
                        pipelines = new
                        {
                            cache_lookup_hits = VulkanStats.VulkanPipelineCacheLookupHits,
                            cache_lookup_misses = VulkanStats.VulkanPipelineCacheLookupMisses,
                            driver_cache_persisted_hits = VulkanStats.VulkanDriverPipelineCachePersistedHits,
                            driver_cache_runtime_hits = VulkanStats.VulkanDriverPipelineCacheRuntimeHits,
                            driver_cache_misses = VulkanStats.VulkanDriverPipelineCacheMisses,
                            driver_cache_unknown = VulkanStats.VulkanDriverPipelineCacheUnknown,
                            compile_required_count = VulkanStats.VulkanPipelineCompileRequiredCount,
                            compile_completed_count = VulkanStats.VulkanPipelineCompileCompletedCount,
                            background_compile_completed_count = VulkanStats.VulkanPipelineBackgroundCompileCompletedCount,
                            foreground_compile_completed_count = Math.Max(
                                0,
                                VulkanStats.VulkanPipelineCompileCompletedCount - VulkanStats.VulkanPipelineBackgroundCompileCompletedCount),
                            required_pipeline_pending_count = VulkanStats.VulkanRequiredPipelinePendingCount,
                            record_deferred_count = VulkanStats.VulkanPipelineRecordDeferredCount,
                            render_thread_shader_compile_count = VulkanStats.VulkanRenderThreadShaderCompileCount,
                            compile_total_ms = VulkanStats.VulkanPipelineCompileTotalMs,
                            compile_max_ms = VulkanStats.VulkanPipelineCompileMaxMs,
                            async_queued_count = VulkanStats.VulkanPipelineAsyncQueuedCount,
                            queue_rejected_count = VulkanStats.VulkanPipelineQueueRejectedCount,
                            draw_not_ready_count = VulkanStats.VulkanPipelineDrawNotReadyCount,
                            queue_depth_high_water = VulkanStats.VulkanPipelineQueueDepthHighWater,
                            queue_capacity = VulkanStats.VulkanPipelineQueueCapacity,
                            cache_miss_summary = VulkanStats.VulkanPipelineCacheMissSummary,
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
                            swapchain_generation_queued_count = VulkanStats.VulkanSwapchainRetirementQueuedCount,
                            swapchain_generation_drained_count = VulkanStats.VulkanSwapchainRetirementDrainedCount,
                            swapchain_generation_pending_count = VulkanStats.VulkanSwapchainRetirementPendingCount,
                            swapchain_generation_pending_high_water = VulkanStats.VulkanSwapchainRetirementPendingHighWater,
                            swapchain_generation_deferred_count = VulkanStats.VulkanSwapchainRetirementDeferredCount,
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

        private static object BuildFrameOutputManifest(RuntimeEngine.Rendering.Stats.FrameOutputManifestSnapshot snapshot)
        {
            RuntimeEngine.Rendering.Stats.FrameOutputEntrySnapshot[] outputs = snapshot.Outputs ?? [];
            object[] outputData = new object[outputs.Length];
            for (int i = 0; i < outputs.Length; i++)
            {
                RuntimeEngine.Rendering.Stats.FrameOutputEntrySnapshot output = outputs[i];
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
                physical_plan_cache_hits = snapshot.Work.PhysicalPlanCacheHits,
                physical_plan_cache_misses = snapshot.Work.PhysicalPlanCacheMisses,
                physical_plan_generations = snapshot.Work.PhysicalPlanGenerations,
                physical_plan_alias_reuses = snapshot.Work.PhysicalPlanAliasReuses,
                planner_arena_high_water = snapshot.Work.PlannerArenaHighWater,
                render_graph_plan_generation = snapshot.Work.RenderGraphPlanGeneration,
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
                planner_eviction_deferral_count = snapshot.Work.PlannerEvictionDeferralCount,
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
                boundary_allocated_bytes = VulkanStats.VulkanCpuStageBoundaryAllocatedBytes(stage),
                boundary_allocation_high_water_bytes = VulkanStats.VulkanCpuStageBoundaryAllocationHighWaterBytes(stage),
                process_invocation_count = VulkanStats.VulkanCpuStageInvocationCount(stage),
                process_elapsed_ms = VulkanStats.VulkanCpuStageCumulativeMs(stage),
                process_peak_ms = VulkanStats.VulkanCpuStagePeakMs(stage),
            };

        private static object VulkanFrequencyPublication(int frequency)
            => new
            {
                publications =
                    VulkanStats.GetVulkanAutoUniformFrequencyPublicationCount(
                        frequency),
                reuses =
                    VulkanStats.GetVulkanAutoUniformFrequencyReuseCount(
                        frequency),
                published_bytes =
                    VulkanStats.GetVulkanAutoUniformFrequencyPublishedBytes(
                        frequency),
            };

        private static object[] GetVulkanProgramBindingArtifactFallbackSamples()
        {
            int count = VulkanStats.VulkanProgramBindingArtifactFallbackSampleCount;
            object[] samples = new object[count];

            for (int index = 0; index < count; index++)
            {
                VulkanProgramBindingArtifactFallbackSample sample =
                    VulkanStats.GetVulkanProgramBindingArtifactFallbackSample(index);
                samples[index] = new
                {
                    reason = sample.Reason.ToString(),
                    mesh_name = sample.MeshName,
                    material_name = sample.MaterialName,
                    program_name = sample.ProgramName,
                    detail = sample.Detail,
                };
            }

            return samples;
        }

        private static object[] GetVulkanAutoUniformSchemaMismatchSamples()
        {
            int count = VulkanStats.VulkanAutoUniformSchemaMismatchSampleCount;
            object[] samples = new object[count];

            for (int index = 0; index < count; index++)
            {
                VulkanAutoUniformSchemaMismatchSample sample =
                    VulkanStats.GetVulkanAutoUniformSchemaMismatchSample(index);
                samples[index] = new
                {
                    site = sample.Site.ToString(),
                    program_name = sample.ProgramName,
                    block_name = sample.BlockName,
                    entry_name = sample.EntryName,
                    program_link_generation = sample.ProgramLinkGeneration,
                    set = sample.Set,
                    binding = sample.Binding,
                    schema_size = sample.SchemaSize,
                    current_size = sample.CurrentSize,
                    buffer_size = sample.BufferSize,
                    same_block_reference = sample.SameBlockReference,
                    reflected_frequency = sample.ReflectedFrequency,
                    runtime_frequency = sample.RuntimeFrequency,
                    byte_offset = sample.ByteOffset,
                    legacy_value = sample.LegacyValue,
                    packed_value = sample.PackedValue,
                };
            }

            return samples;
        }

        private static object CreateSecondaryRecordingEligibilityCounts(
            EVulkanSecondaryCommandFamily family)
            => new
            {
                eligible = VulkanStats.GetVulkanSecondaryRecordingEligibilityCount(
                    family,
                    EVulkanSecondaryRecordingEligibility.Eligible),
                family_disabled = VulkanStats.GetVulkanSecondaryRecordingEligibilityCount(
                    family,
                    EVulkanSecondaryRecordingEligibility.FamilyDisabled),
                secondary_command_buffers_disabled = VulkanStats.GetVulkanSecondaryRecordingEligibilityCount(
                    family,
                    EVulkanSecondaryRecordingEligibility.SecondaryCommandBuffersDisabled),
                empty_range = VulkanStats.GetVulkanSecondaryRecordingEligibilityCount(
                    family,
                    EVulkanSecondaryRecordingEligibility.EmptyRange),
                queue_family_unsupported = VulkanStats.GetVulkanSecondaryRecordingEligibilityCount(
                    family,
                    EVulkanSecondaryRecordingEligibility.QueueFamilyUnsupported),
                active_render_scope = VulkanStats.GetVulkanSecondaryRecordingEligibilityCount(
                    family,
                    EVulkanSecondaryRecordingEligibility.ActiveRenderScope),
                query_inheritance_unsupported = VulkanStats.GetVulkanSecondaryRecordingEligibilityCount(
                    family,
                    EVulkanSecondaryRecordingEligibility.QueryInheritanceUnsupported),
                barrier_plan_unavailable = VulkanStats.GetVulkanSecondaryRecordingEligibilityCount(
                    family,
                    EVulkanSecondaryRecordingEligibility.BarrierPlanUnavailable),
                query_reset_primary_owned = VulkanStats.GetVulkanSecondaryRecordingEligibilityCount(
                    family,
                    EVulkanSecondaryRecordingEligibility.QueryResetPrimaryOwned),
                query_pair_primary_owned = VulkanStats.GetVulkanSecondaryRecordingEligibilityCount(
                    family,
                    EVulkanSecondaryRecordingEligibility.QueryPairPrimaryOwned),
                query_timestamp_primary_owned = VulkanStats.GetVulkanSecondaryRecordingEligibilityCount(
                    family,
                    EVulkanSecondaryRecordingEligibility.QueryTimestampPrimaryOwned),
                query_properties_primary_owned = VulkanStats.GetVulkanSecondaryRecordingEligibilityCount(
                    family,
                    EVulkanSecondaryRecordingEligibility.QueryPropertiesPrimaryOwned),
                query_result_ordering_unavailable = VulkanStats.GetVulkanSecondaryRecordingEligibilityCount(
                    family,
                    EVulkanSecondaryRecordingEligibility.QueryResultOrderingUnavailable),
                invalid_operation_state = VulkanStats.GetVulkanSecondaryRecordingEligibilityCount(
                    family,
                    EVulkanSecondaryRecordingEligibility.InvalidOperationState),
            };

        private static double? JsonFinite(double value)
            => double.IsFinite(value) ? value : null;
    }
}
