using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using XREngine.Timers;

namespace XREngine;

public static partial class Engine
{
    public static bool IsSpeedProfileCaptureActive
    {
        get
        {
#if !XRE_PUBLISHED
            return ProfileCapture.IsRuntimeCaptureActive;
#else
            return false;
#endif
        }
    }

    public static double SpeedProfileCaptureSecondsRemaining
    {
        get
        {
#if !XRE_PUBLISHED
            return ProfileCapture.RuntimeCaptureSecondsRemaining;
#else
            return 0.0;
#endif
        }
    }

    public static string LastSpeedProfileCaptureSummaryPath
    {
        get
        {
#if !XRE_PUBLISHED
            return ProfileCapture.LastRuntimeCaptureSummaryPath;
#else
            return string.Empty;
#endif
        }
    }

    public static bool TryStartSpeedProfileCapture(double durationSeconds, string label, out string? error)
    {
#if !XRE_PUBLISHED
        return ProfileCapture.TryStartRuntimeCapture(durationSeconds, label, out error);
#else
        error = "Speed profile capture is not available in published builds.";
        return false;
#endif
    }

    public static bool TryStopSpeedProfileCapture(out string summaryPath, out string? error)
    {
#if !XRE_PUBLISHED
        return ProfileCapture.TryStopRuntimeCapture(out summaryPath, out error);
#else
        summaryPath = string.Empty;
        error = "Speed profile capture is not available in published builds.";
        return false;
#endif
    }

#if !XRE_PUBLISHED
    internal static class ProfileCapture
    {
        private const string FrameStatsFileName = "profiler-render-stats.ndjson";
        private const string ManifestFileName = "profiler-capture-manifest.json";
        private const string SummaryFileName = "profiler-capture-summary.json";
        private const string RuntimeCaptureDirectoryName = "speed-profiles";
        private const int RuntimeCaptureRetentionCount = 3;
        private const int FlushIntervalMilliseconds = 1000;
        private const int MaxBufferedCharacters = 256 * 1024;
        private const double MaxRuntimeCaptureSeconds = 600.0;

        private static readonly bool s_envCaptureEnabled = IsEnvFlagEnabled("XRE_PROFILE_CAPTURE");
        private static readonly bool s_envAutoDumpGpuTimings =
            s_envCaptureEnabled || IsEnvFlagEnabled("XRE_PROFILE_AUTO_DUMP");
        private static readonly object s_lock = new();
        private static readonly StringBuilder s_sampleBuffer = new(MaxBufferedCharacters);
        private static readonly StringBuilder s_lineBuilder = new(4096);
        private static readonly JsonSerializerOptions s_jsonOptions = new() { WriteIndented = true };

        private static volatile bool s_runtimeCaptureEnabled;
        private static long s_runtimeCaptureEndTicks;
        private static string s_runtimeRunLabel = string.Empty;
        private static string? s_outputDirectory;
        private static string s_lastRuntimeSummaryPath = string.Empty;
        private static long s_startTicks;
        private static long s_lastFlushTicks;
        private static int s_sampleCount;
        private static bool s_manifestWritten;
        private static bool s_shutdown;
        private static RunMetadata? s_metadata;

        public static bool IsRuntimeCaptureActive
        {
            get
            {
                lock (s_lock)
                {
                    return s_runtimeCaptureEnabled;
                }
            }
        }

        public static double RuntimeCaptureSecondsRemaining
        {
            get
            {
                lock (s_lock)
                {
                    if (!s_runtimeCaptureEnabled)
                        return 0.0;

                    long ticksRemaining = Math.Max(0L, s_runtimeCaptureEndTicks - Engine.ElapsedTicks);
                    return Math.Round(TicksToMilliseconds(ticksRemaining) / 1000.0, 1);
                }
            }
        }

        public static string LastRuntimeCaptureSummaryPath
        {
            get
            {
                lock (s_lock)
                {
                    return s_lastRuntimeSummaryPath;
                }
            }
        }

        public static bool TryStartRuntimeCapture(double durationSeconds, string label, out string? error)
        {
            if (double.IsNaN(durationSeconds) || double.IsInfinity(durationSeconds) || durationSeconds <= 0.0)
            {
                error = "Speed profile duration must be greater than zero seconds.";
                return false;
            }

            durationSeconds = Math.Min(durationSeconds, MaxRuntimeCaptureSeconds);

            lock (s_lock)
            {
                if (s_shutdown)
                {
                    error = "Speed profile capture is not available after engine shutdown has started.";
                    return false;
                }

                if (s_envCaptureEnabled)
                {
                    error = "Launch-time profile capture is already active for this process.";
                    return false;
                }

                if (s_runtimeCaptureEnabled)
                {
                    error = "A speed profile capture is already running.";
                    return false;
                }

                if (!TryCreateRuntimeCaptureDirectory(label, out string outputDirectory, out error))
                    return false;

                ResetCaptureStateNoLock(preserveLastRuntimeSummaryPath: true);
                s_outputDirectory = outputDirectory;
                s_runtimeRunLabel = string.IsNullOrWhiteSpace(label) ? "profiler-panel" : label.Trim();
                s_runtimeCaptureEndTicks = Engine.ElapsedTicks + SecondsToTicks(durationSeconds);
                s_runtimeCaptureEnabled = true;
                s_lastRuntimeSummaryPath = Path.Combine(outputDirectory, SummaryFileName);
                error = null;
                return true;
            }
        }

        public static bool TryStopRuntimeCapture(out string summaryPath, out string? error)
        {
            CaptureCompletion completion;
            lock (s_lock)
            {
                if (!s_runtimeCaptureEnabled)
                {
                    summaryPath = s_lastRuntimeSummaryPath;
                    error = string.IsNullOrWhiteSpace(summaryPath)
                        ? "No speed profile capture is running."
                        : "No speed profile capture is running; the last capture has already completed.";
                    return false;
                }

                completion = CompleteRuntimeCaptureStateNoLock();
                summaryPath = Path.Combine(completion.OutputDirectory, SummaryFileName);
            }

            FinalizeCapture(completion);
            error = null;
            return true;
        }

        public static void RecordRenderStatsSnapshot()
        {
            if ((!s_envCaptureEnabled && !s_runtimeCaptureEnabled) || s_shutdown)
                return;

            CaptureCompletion? completedRuntimeCapture = null;

            lock (s_lock)
            {
                if (s_shutdown || (!s_envCaptureEnabled && !s_runtimeCaptureEnabled))
                    return;

                RunMetadata metadata = GetMetadataNoLock();
                WriteManifestNoLock(metadata);

                long nowTicks = Engine.ElapsedTicks;
                if (s_startTicks == 0L)
                {
                    s_startTicks = nowTicks;
                    s_lastFlushTicks = nowTicks;
                }

                AppendSampleLineNoLock(metadata, nowTicks);
                s_sampleCount++;

                if (ShouldFlushNoLock(nowTicks))
                    FlushSamplesNoLock();

                if (s_runtimeCaptureEnabled && nowTicks >= s_runtimeCaptureEndTicks)
                    completedRuntimeCapture = CompleteRuntimeCaptureStateNoLock();
            }

            if (completedRuntimeCapture is not null)
                FinalizeCapture(completedRuntimeCapture);
        }

        public static void Shutdown()
        {
            if ((!s_envCaptureEnabled && !s_envAutoDumpGpuTimings && !s_runtimeCaptureEnabled) || s_shutdown)
                return;

            CaptureCompletion completion;
            lock (s_lock)
            {
                if (s_shutdown)
                    return;

                s_shutdown = true;
                completion = s_runtimeCaptureEnabled
                    ? CompleteRuntimeCaptureStateNoLock()
                    : CompleteEnvironmentCaptureStateNoLock();
            }

            FinalizeCapture(completion);
        }

        private static CaptureCompletion CompleteRuntimeCaptureStateNoLock()
        {
            RunMetadata metadata = GetMetadataNoLock();
            WriteManifestNoLock(metadata);
            FlushSamplesNoLock();

            string outputDirectory = GetCurrentOutputDirectoryNoLock();
            int sampleCount = s_sampleCount;
            string summaryPath = Path.Combine(outputDirectory, SummaryFileName);

            ResetCaptureStateNoLock(preserveLastRuntimeSummaryPath: true);
            s_lastRuntimeSummaryPath = summaryPath;

            return new CaptureCompletion(
                metadata,
                sampleCount,
                outputDirectory,
                CaptureEnabled: true,
                AutoDumpGpuTimings: true);
        }

        private static CaptureCompletion CompleteEnvironmentCaptureStateNoLock()
        {
            RunMetadata metadata = GetMetadataNoLock();
            WriteManifestNoLock(metadata);
            FlushSamplesNoLock();

            return new CaptureCompletion(
                metadata,
                s_sampleCount,
                GetCurrentOutputDirectoryNoLock(),
                s_envCaptureEnabled,
                s_envAutoDumpGpuTimings);
        }

        private static void FinalizeCapture(CaptureCompletion completion)
        {
            string[] gpuDumpFiles = [];
            string? gpuDumpError = null;
            bool gpuDumpSucceeded = false;
            if (completion.AutoDumpGpuTimings)
            {
                gpuDumpSucceeded = Engine.Rendering.Stats.TryDumpAllGpuRenderPipelineTimingHistories(
                    out gpuDumpFiles,
                    out gpuDumpError);
            }

            var summary = new
            {
                completed_utc = DateTimeOffset.UtcNow,
                process_id = Environment.ProcessId,
                sample_count = completion.SampleCount,
                capture_enabled = completion.CaptureEnabled,
                gpu_auto_dump_enabled = completion.AutoDumpGpuTimings,
                gpu_dump_succeeded = gpuDumpSucceeded,
                gpu_dump_files = gpuDumpFiles,
                gpu_dump_error = gpuDumpError ?? string.Empty,
                output_directory = completion.OutputDirectory,
                run = completion.Metadata,
            };

            WriteTextFileNoThrow(
                completion.OutputDirectory,
                SummaryFileName,
                JsonSerializer.Serialize(summary, s_jsonOptions) + Environment.NewLine,
                append: false);
        }

        private static RunMetadata GetMetadataNoLock()
        {
            if (s_metadata is not null)
                return s_metadata;

            bool runtimeCapture = s_runtimeCaptureEnabled;
            string runLabel = runtimeCapture && !string.IsNullOrWhiteSpace(s_runtimeRunLabel)
                ? s_runtimeRunLabel
                : Environment.GetEnvironmentVariable("XRE_PROFILE_RUN_LABEL") ?? string.Empty;

            s_metadata = new RunMetadata(
                CaptureMode: runtimeCapture ? "runtime" : "launch",
                RunLabel: runLabel,
                WorldMode: Environment.GetEnvironmentVariable("XRE_WORLD_MODE") ?? string.Empty,
                ForcedStrategy: Environment.GetEnvironmentVariable("XRE_FORCE_MESH_SUBMISSION_STRATEGY") ?? string.Empty,
                EffectiveStrategy: CaptureString(() => Engine.Rendering.ResolveMeshSubmissionStrategy().ToString()),
                ZeroReadbackMaterialDrawPath: CaptureString(() => Engine.EffectiveSettings.ZeroReadbackMaterialDrawPath.ToString()),
                ZeroReadbackMaterialDrawPathEnv: Environment.GetEnvironmentVariable("XRE_ZERO_READBACK_MATERIAL_DRAW_PATH") ?? string.Empty,
                P3Logging: Environment.GetEnvironmentVariable("XRE_P3_LOGGING") ?? string.Empty,
                BucketLoopDryRun: Environment.GetEnvironmentVariable("XRE_BUCKET_LOOP_DRY_RUN") ?? string.Empty,
                SkipCommandSwapIfClean: Environment.GetEnvironmentVariable("XRE_SKIP_COMMAND_SWAP_IF_CLEAN") ?? string.Empty,
                BucketLoopSkipEmpty: Environment.GetEnvironmentVariable("XRE_BUCKET_LOOP_SKIP_EMPTY") ?? string.Empty,
                ForceSingleBucket: Environment.GetEnvironmentVariable("XRE_FORCE_SINGLE_BUCKET") ?? string.Empty,
                Configuration: CaptureString(() => Engine.GameSettings?.BuildSettings?.Configuration.ToString() ?? string.Empty),
                CreatedUtc: DateTimeOffset.UtcNow,
                ProcessId: Environment.ProcessId);

            return s_metadata;
        }

        private static void WriteManifestNoLock(RunMetadata metadata)
        {
            if (s_manifestWritten)
                return;

            var manifest = new
            {
                capture_file = FrameStatsFileName,
                schema = "xrengine.profile_capture.render_stats.v1",
                fields_note = "One JSON object per completed render frame. CPU frame timings are wall-clock thread loop durations; GPU pipeline timings are OpenGL timestamp-query snapshots when ready.",
                run = metadata,
            };

            WriteTextFileNoThrow(GetCurrentOutputDirectoryNoLock(), ManifestFileName, JsonSerializer.Serialize(manifest, s_jsonOptions) + Environment.NewLine, append: false);
            s_manifestWritten = true;
        }

        private static void AppendSampleLineNoLock(RunMetadata metadata, long nowTicks)
        {
            var timer = Engine.Time.Timer;
            double renderMs = TicksToMilliseconds(timer.Render.ElapsedTicks);
            double updateMs = TicksToMilliseconds(timer.Update.ElapsedTicks);
            double collectVisibleMs = TicksToMilliseconds(timer.Collect.ElapsedTicks);
            double fixedUpdateMs = TicksToMilliseconds(timer.FixedUpdateManager.ElapsedTicks);
            double elapsedMs = TicksToMilliseconds(Math.Max(0L, nowTicks - s_startTicks));
            double gpuPipelineMs = Engine.Rendering.Stats.GpuRenderPipelineFrameMs;
            bool gpuTimingsReady = Engine.Rendering.Stats.GpuRenderPipelineTimingsReady;

            s_lineBuilder.Clear();
            s_lineBuilder.Append('{');
            bool first = true;

            AppendStringField(s_lineBuilder, "ts_utc", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture), ref first);
            AppendNumberField(s_lineBuilder, "elapsed_ms", elapsedMs, ref first);
            AppendNumberField(s_lineBuilder, "process_id", Environment.ProcessId, ref first);
            ulong renderFrameId = Engine.Rendering.State.RenderFrameId;
            AppendNumberField(s_lineBuilder, "render_frame_id", renderFrameId, ref first);
            AppendNumberField(s_lineBuilder, "completed_frame_id", renderFrameId == 0UL ? 0UL : renderFrameId - 1UL, ref first);
            AppendStringField(s_lineBuilder, "capture_mode", metadata.CaptureMode, ref first);
            AppendStringField(s_lineBuilder, "run_label", metadata.RunLabel, ref first);
            AppendStringField(s_lineBuilder, "world_mode", metadata.WorldMode, ref first);
            AppendStringField(s_lineBuilder, "forced_strategy", metadata.ForcedStrategy, ref first);
            AppendStringField(s_lineBuilder, "effective_strategy", metadata.EffectiveStrategy, ref first);
            AppendStringField(s_lineBuilder, "zero_readback_material_draw_path", metadata.ZeroReadbackMaterialDrawPath, ref first);
            AppendStringField(s_lineBuilder, "zero_readback_material_draw_path_env", metadata.ZeroReadbackMaterialDrawPathEnv, ref first);
            AppendStringField(s_lineBuilder, "p3_logging", metadata.P3Logging, ref first);

            AppendNumberField(s_lineBuilder, "render_dispatch_ms", renderMs, ref first);
            AppendNumberField(s_lineBuilder, "update_ms", updateMs, ref first);
            AppendNumberField(s_lineBuilder, "collect_visible_ms", collectVisibleMs, ref first);
            AppendNumberField(s_lineBuilder, "fixed_update_ms", fixedUpdateMs, ref first);
            AppendNullableNumberField(
                s_lineBuilder,
                "render_thread_minus_gpu_ms",
                gpuTimingsReady && gpuPipelineMs > 0.0 ? Math.Max(0.0, renderMs - gpuPipelineMs) : null,
                ref first);

            AppendNumberField(s_lineBuilder, "draw_calls", Engine.Rendering.Stats.DrawCalls, ref first);
            AppendNumberField(s_lineBuilder, "multi_draw_calls", Engine.Rendering.Stats.MultiDrawCalls, ref first);
            AppendNumberField(s_lineBuilder, "triangles_rendered", Engine.Rendering.Stats.TrianglesRendered, ref first);
            AppendNumberField(s_lineBuilder, "gpu_mapped_buffers", Engine.Rendering.Stats.GpuMappedBuffers, ref first);
            AppendNumberField(s_lineBuilder, "gpu_readback_bytes", Engine.Rendering.Stats.GpuReadbackBytes, ref first);
            AppendNumberField(s_lineBuilder, "gpu_cpu_fallback_events", Engine.Rendering.Stats.GpuCpuFallbackEvents, ref first);
            AppendNumberField(s_lineBuilder, "gpu_cpu_fallback_recovered_commands", Engine.Rendering.Stats.GpuCpuFallbackRecoveredCommands, ref first);
            AppendNumberField(s_lineBuilder, "forbidden_gpu_fallback_events", Engine.Rendering.Stats.ForbiddenGpuFallbackEvents, ref first);
            AppendNumberField(s_lineBuilder, "gpu_meshlet_requested_frames", Engine.Rendering.Stats.GpuMeshletRequestedFrames, ref first);
            AppendNumberField(s_lineBuilder, "gpu_meshlet_production_frames", Engine.Rendering.Stats.GpuMeshletProductionFrames, ref first);
            AppendNumberField(s_lineBuilder, "gpu_meshlet_fallback_frames", Engine.Rendering.Stats.GpuMeshletFallbackFrames, ref first);
            AppendNumberField(s_lineBuilder, "gpu_meshlet_dispatch_skipped", Engine.Rendering.Stats.GpuMeshletDispatchSkipped, ref first);
            AppendNumberField(s_lineBuilder, "gpu_meshlet_task_records_emitted", Engine.Rendering.Stats.GpuMeshletTaskRecordsEmitted, ref first);
            AppendNumberField(s_lineBuilder, "gpu_meshlet_task_records_frustum_culled", Engine.Rendering.Stats.GpuMeshletTaskRecordsFrustumCulled, ref first);
            AppendNumberField(s_lineBuilder, "gpu_meshlet_task_records_cone_culled", Engine.Rendering.Stats.GpuMeshletTaskRecordsConeCulled, ref first);
            AppendNumberField(s_lineBuilder, "gpu_meshlet_task_records_hiz_culled", Engine.Rendering.Stats.GpuMeshletTaskRecordsHiZCulled, ref first);
            AppendNumberField(s_lineBuilder, "gpu_meshlet_expansion_overflow_count", Engine.Rendering.Stats.GpuMeshletExpansionOverflowCount, ref first);
            AppendNumberField(s_lineBuilder, "gpu_meshlet_buffer_bytes_resident", Engine.Rendering.Stats.GpuMeshletBufferBytesResident, ref first);
            AppendNumberField(s_lineBuilder, "gpu_meshlet_cache_hits", Engine.Rendering.Stats.GpuMeshletCacheHits, ref first);
            AppendNumberField(s_lineBuilder, "gpu_meshlet_cache_misses", Engine.Rendering.Stats.GpuMeshletCacheMisses, ref first);
            AppendNumberField(s_lineBuilder, "gpu_meshlet_cache_stale", Engine.Rendering.Stats.GpuMeshletCacheStale, ref first);
            AppendNumberField(s_lineBuilder, "fbo_bind_count", Engine.Rendering.Stats.FBOBindCount, ref first);
            AppendNumberField(s_lineBuilder, "fbo_bandwidth_bytes", Engine.Rendering.Stats.FBOBandwidthBytes, ref first);
            AppendNumberField(s_lineBuilder, "allocated_vram_bytes", Engine.Rendering.Stats.AllocatedVRAMBytes, ref first);

            AppendBoolField(s_lineBuilder, "gpu_pipeline_profiling_enabled", Engine.Rendering.Stats.GpuRenderPipelineProfilingEnabled, ref first);
            AppendBoolField(s_lineBuilder, "gpu_pipeline_profiling_supported", Engine.Rendering.Stats.GpuRenderPipelineProfilingSupported, ref first);
            AppendBoolField(s_lineBuilder, "gpu_pipeline_timings_ready", gpuTimingsReady, ref first);
            AppendStringField(s_lineBuilder, "gpu_pipeline_backend", Engine.Rendering.Stats.GpuRenderPipelineBackend, ref first);
            AppendStringField(s_lineBuilder, "gpu_pipeline_status", Engine.Rendering.Stats.GpuRenderPipelineStatusMessage, ref first);
            AppendNumberField(s_lineBuilder, "gpu_pipeline_frame_ms", gpuPipelineMs, ref first);

            AppendNumberField(s_lineBuilder, "vulkan_indirect_api_calls", Engine.Rendering.Stats.VulkanIndirectApiCalls, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_indirect_submitted_draws", Engine.Rendering.Stats.VulkanIndirectSubmittedDraws, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_requested_draws", Engine.Rendering.Stats.VulkanRequestedDraws, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_consumed_draws", Engine.Rendering.Stats.VulkanConsumedDraws, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_oom_fallback_count", Engine.Rendering.Stats.VulkanOomFallbackCount, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_frame_total_ms", Engine.Rendering.Stats.VulkanFrameTotalMs, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_frame_gpu_command_buffer_ms", Engine.Rendering.Stats.VulkanFrameGpuCommandBufferMs, ref first);

            s_lineBuilder.Append('}');
            s_sampleBuffer.Append(s_lineBuilder);
            s_sampleBuffer.AppendLine();
        }

        private static bool ShouldFlushNoLock(long nowTicks)
            => s_sampleBuffer.Length >= MaxBufferedCharacters ||
               TicksToMilliseconds(Math.Max(0L, nowTicks - s_lastFlushTicks)) >= FlushIntervalMilliseconds;

        private static void FlushSamplesNoLock()
        {
            if (s_sampleBuffer.Length == 0)
                return;

            WriteTextFileNoThrow(GetCurrentOutputDirectoryNoLock(), FrameStatsFileName, s_sampleBuffer.ToString(), append: true);
            s_sampleBuffer.Clear();
            s_lastFlushTicks = Engine.ElapsedTicks;
        }

        private static void ResetCaptureStateNoLock(bool preserveLastRuntimeSummaryPath)
        {
            string lastRuntimeSummaryPath = s_lastRuntimeSummaryPath;

            s_runtimeCaptureEnabled = false;
            s_runtimeCaptureEndTicks = 0L;
            s_runtimeRunLabel = string.Empty;
            s_outputDirectory = null;
            s_startTicks = 0L;
            s_lastFlushTicks = 0L;
            s_sampleCount = 0;
            s_manifestWritten = false;
            s_metadata = null;
            s_sampleBuffer.Clear();
            s_lineBuilder.Clear();

            s_lastRuntimeSummaryPath = preserveLastRuntimeSummaryPath ? lastRuntimeSummaryPath : string.Empty;
        }

        private static string GetCurrentOutputDirectoryNoLock()
            => string.IsNullOrWhiteSpace(s_outputDirectory)
                ? Debug.EnsureLogRunDirectory()
                : s_outputDirectory!;

        private static bool TryCreateRuntimeCaptureDirectory(string label, out string outputDirectory, out string? error)
        {
            outputDirectory = string.Empty;

            try
            {
                string sessionDirectory = Debug.EnsureLogRunDirectory();
                string profileRoot = Path.Combine(sessionDirectory, RuntimeCaptureDirectoryName);
                Directory.CreateDirectory(profileRoot);

                string stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture);
                string safeLabel = SanitizePathSegment(label);
                string directoryName = string.IsNullOrWhiteSpace(safeLabel) ? stamp : stamp + "_" + safeLabel;
                outputDirectory = Path.Combine(profileRoot, directoryName);
                Directory.CreateDirectory(outputDirectory);
                EnforceRuntimeCaptureRetention(profileRoot);

                error = null;
                return true;
            }
            catch (Exception ex)
            {
                error = "Failed to create speed profile directory: " + ex.Message;
                return false;
            }
        }

        private static void EnforceRuntimeCaptureRetention(string profileRoot)
        {
            try
            {
                string rootFullPath = Path.GetFullPath(profileRoot);
                string rootWithSeparator = rootFullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

                foreach (DirectoryInfo directory in new DirectoryInfo(rootFullPath)
                    .GetDirectories()
                    .OrderByDescending(static d => d.CreationTimeUtc)
                    .Skip(RuntimeCaptureRetentionCount))
                {
                    string directoryFullPath = Path.GetFullPath(directory.FullName);
                    if (!directoryFullPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
                        continue;

                    try
                    {
                        directory.Delete(recursive: true);
                    }
                    catch
                    {
                        // Retention must not disrupt profiling.
                    }
                }
            }
            catch
            {
                // Retention is opportunistic.
            }
        }

        private static string SanitizePathSegment(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            char[] invalidChars = Path.GetInvalidFileNameChars();
            StringBuilder builder = new(value.Length);
            foreach (char c in value.Trim())
            {
                if (char.IsControl(c) || Array.IndexOf(invalidChars, c) >= 0)
                {
                    if (builder.Length > 0 && builder[^1] != '_')
                        builder.Append('_');
                    continue;
                }

                builder.Append(c);
            }

            return builder.ToString().Trim('_');
        }

        private static void WriteTextFileNoThrow(string directory, string fileName, string contents, bool append)
        {
            if (string.IsNullOrEmpty(contents))
                return;

            try
            {
                Directory.CreateDirectory(directory);
                string path = Path.Combine(directory, fileName);
                if (append)
                    File.AppendAllText(path, contents, Encoding.UTF8);
                else
                    File.WriteAllText(path, contents, Encoding.UTF8);
            }
            catch
            {
                // Diagnostics capture must never perturb engine shutdown or the render loop.
            }
        }

        private static void AppendStringField(StringBuilder builder, string name, string value, ref bool first)
        {
            AppendFieldPrefix(builder, name, ref first);
            builder.Append(JsonSerializer.Serialize(value ?? string.Empty));
        }

        private static void AppendBoolField(StringBuilder builder, string name, bool value, ref bool first)
        {
            AppendFieldPrefix(builder, name, ref first);
            builder.Append(value ? "true" : "false");
        }

        private static void AppendNumberField(StringBuilder builder, string name, int value, ref bool first)
        {
            AppendFieldPrefix(builder, name, ref first);
            builder.Append(value.ToString(CultureInfo.InvariantCulture));
        }

        private static void AppendNumberField(StringBuilder builder, string name, long value, ref bool first)
        {
            AppendFieldPrefix(builder, name, ref first);
            builder.Append(value.ToString(CultureInfo.InvariantCulture));
        }

        private static void AppendNumberField(StringBuilder builder, string name, ulong value, ref bool first)
        {
            AppendFieldPrefix(builder, name, ref first);
            builder.Append(value.ToString(CultureInfo.InvariantCulture));
        }

        private static void AppendNumberField(StringBuilder builder, string name, double value, ref bool first)
        {
            AppendFieldPrefix(builder, name, ref first);
            AppendDoubleValue(builder, value);
        }

        private static void AppendNullableNumberField(StringBuilder builder, string name, double? value, ref bool first)
        {
            AppendFieldPrefix(builder, name, ref first);
            if (value is double number)
                AppendDoubleValue(builder, number);
            else
                builder.Append("null");
        }

        private static void AppendFieldPrefix(StringBuilder builder, string name, ref bool first)
        {
            if (!first)
                builder.Append(',');

            first = false;
            builder.Append('"');
            builder.Append(name);
            builder.Append("\":");
        }

        private static void AppendDoubleValue(StringBuilder builder, double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                builder.Append("null");
                return;
            }

            builder.Append(value.ToString("0.###", CultureInfo.InvariantCulture));
        }

        private static double TicksToMilliseconds(long ticks)
            => ticks <= 0L ? 0.0 : ticks * 1000.0 / EngineTimer.StopwatchTickFrequency;

        private static long SecondsToTicks(double seconds)
            => (long)Math.Ceiling(seconds * EngineTimer.StopwatchTickFrequency);

        private static string CaptureString(Func<string> read)
        {
            try
            {
                return read() ?? string.Empty;
            }
            catch (Exception ex)
            {
                return "<error:" + ex.GetType().Name + ">";
            }
        }

        private static bool IsEnvFlagEnabled(string name)
        {
            string? raw = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrWhiteSpace(raw))
                return false;

            raw = raw.Trim();
            return raw == "1" ||
                   raw.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                   raw.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
                   raw.Equals("on", StringComparison.OrdinalIgnoreCase);
        }

        private sealed record RunMetadata(
            string CaptureMode,
            string RunLabel,
            string WorldMode,
            string ForcedStrategy,
            string EffectiveStrategy,
            string ZeroReadbackMaterialDrawPath,
            string ZeroReadbackMaterialDrawPathEnv,
            string P3Logging,
            string BucketLoopDryRun,
            string SkipCommandSwapIfClean,
            string BucketLoopSkipEmpty,
            string ForceSingleBucket,
            string Configuration,
            DateTimeOffset CreatedUtc,
            int ProcessId);

        private sealed record CaptureCompletion(
            RunMetadata Metadata,
            int SampleCount,
            string OutputDirectory,
            bool CaptureEnabled,
            bool AutoDumpGpuTimings);
    }
#endif
}
