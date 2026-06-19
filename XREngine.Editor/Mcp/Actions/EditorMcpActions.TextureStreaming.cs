using System;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using XREngine.Data.Core;
using XREngine.Rendering;

namespace XREngine.Editor.Mcp
{
    public sealed partial class EditorMcpActions
    {
        [XRMcp(Name = "get_texture_streaming_summary", Permission = McpPermissionLevel.ReadOnly)]
        [Description("Return imported texture streaming summary telemetry, including Vulkan freeze and generation state.")]
        public static Task<McpToolResponse> GetTextureStreamingSummaryAsync(McpToolContext context)
        {
            ImportedTextureStreamingTelemetry telemetry = XRTexture2D.GetImportedTextureStreamingTelemetry();
            return Task.FromResult(new McpToolResponse(
                "Retrieved imported texture streaming summary.",
                DescribeTextureStreamingSummary(telemetry)));
        }

        [XRMcp(Name = "list_texture_streaming_textures", Permission = McpPermissionLevel.ReadOnly)]
        [Description("List imported texture streaming texture telemetry rows for live residency validation.")]
        public static Task<McpToolResponse> ListTextureStreamingTexturesAsync(
            McpToolContext context,
            [McpName("max_results"), Description("Maximum number of texture rows to return.")] int maxResults = 128,
            [McpName("visible_only"), Description("Only include textures currently reported visible.")] bool visibleOnly = false,
            [McpName("pending_only"), Description("Only include textures with a pending transition.")] bool pendingOnly = false,
            [McpName("slow_only"), Description("Only include textures flagged slow by streaming telemetry.")] bool slowOnly = false)
        {
            int clampedMaxResults = maxResults <= 0 ? 128 : Math.Min(maxResults, 512);
            ImportedTextureStreamingTelemetry telemetry = XRTexture2D.GetImportedTextureStreamingTelemetry();
            ImportedTextureStreamingTextureTelemetry[] rows = XRTexture2D.GetImportedTextureStreamingTextureTelemetry()
                .Where(row => !visibleOnly || row.IsVisible)
                .Where(row => !pendingOnly || row.HasPendingTransition)
                .Where(row => !slowOnly || row.IsSlow)
                .OrderByDescending(static row => row.CurrentCommittedBytes)
                .ThenBy(static row => row.TextureName ?? row.FilePath ?? string.Empty)
                .Take(clampedMaxResults)
                .ToArray();

            return Task.FromResult(new McpToolResponse(
                $"Listed {rows.Length} imported texture streaming texture(s).",
                new
                {
                    summary = DescribeTextureStreamingSummary(telemetry),
                    count = rows.Length,
                    textures = rows.Select(DescribeTextureStreamingTexture).ToArray(),
                }));
        }

        private static object DescribeTextureStreamingSummary(ImportedTextureStreamingTelemetry telemetry)
            => new
            {
                backend_name = telemetry.BackendName,
                display_backend_name = telemetry.DisplayBackendName,
                active_import_scopes = telemetry.ActiveImportScopes,
                tracked_texture_count = telemetry.TrackedTextureCount,
                pending_transition_count = telemetry.PendingTransitionCount,
                active_decode_count = telemetry.ActiveDecodeCount,
                queued_decode_count = telemetry.QueuedDecodeCount,
                active_gpu_upload_count = telemetry.ActiveGpuUploadCount,
                queued_transitions_this_frame = telemetry.QueuedTransitionsThisFrame,
                queued_promotion_transitions_this_frame = telemetry.QueuedPromotionTransitionsThisFrame,
                queued_demotion_transitions_this_frame = telemetry.QueuedDemotionTransitionsThisFrame,
                last_frame_id = telemetry.LastFrameId,
                current_managed_bytes = telemetry.CurrentManagedBytes,
                available_managed_bytes = telemetry.AvailableManagedBytes,
                assigned_managed_bytes = telemetry.AssignedManagedBytes,
                upload_bytes_scheduled_this_frame = telemetry.UploadBytesScheduledThisFrame,
                promotions_blocked = telemetry.PromotionsBlocked,
                vulkan_frozen = telemetry.VulkanFrozen,
                freeze_reason = telemetry.FreezeReason,
            };

        private static object DescribeTextureStreamingTexture(ImportedTextureStreamingTextureTelemetry row)
            => new
            {
                texture_name = row.TextureName,
                file_path = row.FilePath,
                sampler_name = row.SamplerName,
                source_width = row.SourceWidth,
                source_height = row.SourceHeight,
                resident_max_dimension = row.ResidentMaxDimension,
                desired_resident_max_dimension = row.DesiredResidentMaxDimension,
                pending_resident_max_dimension = row.PendingResidentMaxDimension,
                current_committed_bytes = row.CurrentCommittedBytes,
                desired_committed_bytes = row.DesiredCommittedBytes,
                preview_ready = row.PreviewReady,
                has_pending_transition = row.HasPendingTransition,
                is_visible = row.IsVisible,
                is_slow = row.IsSlow,
                was_pressure_demoted = row.WasPressureDemoted,
                has_validation_failure = row.HasValidationFailure,
                validation_failure_count = row.ValidationFailureCount,
                oldest_queue_wait_ms = row.OldestQueueWaitMilliseconds,
                last_upload_ms = row.LastUploadMilliseconds,
                priority_score = row.PriorityScore,
                projected_pixel_span = row.MaxProjectedPixelSpan,
                screen_coverage = row.MaxScreenCoverage,
                uv_density_hint = row.UvDensityHint,
                current_page_coverage = row.CurrentPageCoverage,
                desired_page_coverage = row.DesiredPageCoverage,
                last_visible_frame_id = row.LastVisibleFrameId,
                backend_name = row.BackendName,
                display_backend_name = row.DisplayBackendName,
                vulkan_frozen = row.VulkanFrozen,
                freeze_reason = row.FreezeReason,
                resident_generation = row.ResidentGeneration,
                published_generation = row.PublishedGeneration,
                upload_generation = row.UploadGeneration,
                retirement_generation = row.RetirementGeneration,
            };
    }
}
