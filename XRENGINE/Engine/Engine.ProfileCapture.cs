using System;
using System.Collections.Generic;
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
        private const int ProfileCaptureSchemaVersion = 2;
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
                gpuDumpSucceeded = Engine.Rendering.Stats.GpuPipelineProfiler.TryDumpAllGpuRenderPipelineTimingHistories(
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

            string targetRefreshHzEnv = Environment.GetEnvironmentVariable("XRE_TARGET_REFRESH_HZ") ??
                Environment.GetEnvironmentVariable("XRE_UPDATE_FPS") ??
                string.Empty;
            double? targetRefreshHz = TryParsePositiveDouble(targetRefreshHzEnv);
            double? xrFrameBudgetMs = targetRefreshHz is > 0.0 ? 1000.0 / targetRefreshHz.Value : null;
            string benchmarkErrors = CaptureBenchmarkEnvironmentErrors();

            s_metadata = new RunMetadata(
                SchemaVersion: ProfileCaptureSchemaVersion,
                CaptureMode: runtimeCapture ? "runtime" : "launch",
                RunLabel: runLabel,
                WorldMode: Environment.GetEnvironmentVariable("XRE_WORLD_MODE") ?? string.Empty,
                ForcedStrategy: Environment.GetEnvironmentVariable("XRE_FORCE_MESH_SUBMISSION_STRATEGY") ?? string.Empty,
                EffectiveStrategy: CaptureString(() => Engine.Rendering.ResolveMeshSubmissionStrategy().ToString()),
                ZeroReadbackMaterialDrawPath: CaptureString(() => Engine.EffectiveSettings.ZeroReadbackMaterialDrawPath.ToString()),
                ZeroReadbackMaterialDrawPathEnv: Environment.GetEnvironmentVariable("XRE_ZERO_READBACK_MATERIAL_DRAW_PATH") ?? string.Empty,
                Backend: CaptureString(() => Engine.Rendering.Stats.RendererState.ActiveRenderBackend),
                GpuName: CaptureString(() => RuntimeEngine.Rendering.State.OpenGLRendererName ?? RuntimeEngine.Rendering.State.VulkanDeviceName ?? string.Empty),
                GpuVendor: CaptureString(() => RuntimeEngine.Rendering.State.OpenGLVendor ?? string.Empty),
                GpuDeviceId: Environment.GetEnvironmentVariable("XRE_GPU_DEVICE_ID") ?? string.Empty,
                Driver: Environment.GetEnvironmentVariable("XRE_GPU_DRIVER") ?? string.Empty,
                Scene: Environment.GetEnvironmentVariable("XRE_PROFILE_SCENE") ?? string.Empty,
                Camera: Environment.GetEnvironmentVariable("XRE_PROFILE_CAMERA") ?? string.Empty,
                Lights: Environment.GetEnvironmentVariable("XRE_PROFILE_LIGHTS") ?? string.Empty,
                Viewport: Environment.GetEnvironmentVariable("XRE_PROFILE_VIEWPORT") ?? string.Empty,
                RenderScale: Environment.GetEnvironmentVariable("XRE_PROFILE_RENDER_SCALE") ??
                    CaptureString(() => Engine.Rendering.Settings.TsrRenderScale.ToString(CultureInfo.InvariantCulture)),
                StereoMode: CaptureString(() => Engine.Rendering.Stats.RendererState.ActiveStereoMode),
                ValidationLayersEnabled: CaptureString(() => Engine.Rendering.Stats.RendererState.ValidationLayersEnabled ? "true" : "false"),
                DebugOutputEnabled: CaptureString(() => Engine.Rendering.Stats.RendererState.DebugOutputEnabled ? "true" : "false"),
                ShaderCacheState: Environment.GetEnvironmentVariable("XRE_SHADER_CACHE_MODE") ?? string.Empty,
                TextureCacheState: Environment.GetEnvironmentVariable("XRE_TEXTURE_CACHE_MODE") ?? string.Empty,
                CacheMode: Environment.GetEnvironmentVariable("XRE_PROFILE_CACHE_MODE") ?? string.Empty,
                GpuClockPolicy: Environment.GetEnvironmentVariable("XRE_GPU_CLOCK_POLICY") ?? string.Empty,
                TargetRefreshHz: targetRefreshHz,
                XrFrameBudgetMs: xrFrameBudgetMs,
                BenchmarkPhase: Environment.GetEnvironmentVariable("XRE_PROFILE_PHASE") ?? string.Empty,
                WarmupSeconds: TryParsePositiveDouble(Environment.GetEnvironmentVariable("XRE_PROFILE_WARMUP_SEC")),
                CaptureSeconds: TryParsePositiveDouble(Environment.GetEnvironmentVariable("XRE_PROFILE_CAPTURE_SEC")),
                BenchmarkEnvironmentValid: string.IsNullOrWhiteSpace(benchmarkErrors),
                BenchmarkEnvironmentErrors: benchmarkErrors,
                GpuTimestampDenseMode: IsEnvFlagEnabled("XRE_GPU_TIMESTAMP_DENSE"),
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
                schema = "xrengine.profile_capture.render_stats.v2",
                schema_version = ProfileCaptureSchemaVersion,
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
            double gpuPipelineMs = Engine.Rendering.Stats.GpuPipelineProfiler.GpuRenderPipelineFrameMs;
            bool gpuTimingsReady = Engine.Rendering.Stats.GpuPipelineProfiler.GpuRenderPipelineTimingsReady;

            s_lineBuilder.Clear();
            s_lineBuilder.Append('{');
            bool first = true;

            AppendStringField(s_lineBuilder, "ts_utc", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture), ref first);
            AppendNumberField(s_lineBuilder, "profile_schema_version", ProfileCaptureSchemaVersion, ref first);
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
            AppendStringField(s_lineBuilder, "active_texture_binding_rung", Engine.Rendering.Stats.RendererState.ActiveTextureBindingRung, ref first);
            AppendStringField(s_lineBuilder, "active_stereo_mode", Engine.Rendering.Stats.RendererState.ActiveStereoMode, ref first);
            AppendStringField(s_lineBuilder, "active_render_backend", Engine.Rendering.Stats.RendererState.ActiveRenderBackend, ref first);
            AppendBoolField(s_lineBuilder, "validation_layers_enabled", Engine.Rendering.Stats.RendererState.ValidationLayersEnabled, ref first);
            AppendBoolField(s_lineBuilder, "debug_output_enabled", Engine.Rendering.Stats.RendererState.DebugOutputEnabled, ref first);
            AppendBoolField(s_lineBuilder, "gpu_timestamps_dense_mode", Engine.Rendering.Stats.RendererState.GpuTimestampsDenseMode, ref first);

            AppendNumberField(s_lineBuilder, "render_dispatch_ms", renderMs, ref first);
            AppendNumberField(s_lineBuilder, "update_ms", updateMs, ref first);
            AppendNumberField(s_lineBuilder, "collect_visible_ms", collectVisibleMs, ref first);
            AppendNumberField(s_lineBuilder, "fixed_update_ms", fixedUpdateMs, ref first);
            AppendNullableNumberField(
                s_lineBuilder,
                "render_thread_minus_gpu_ms",
                gpuTimingsReady && gpuPipelineMs > 0.0 ? Math.Max(0.0, renderMs - gpuPipelineMs) : null,
                ref first);

            AppendNumberField(s_lineBuilder, "draw_calls", Engine.Rendering.Stats.Frame.DrawCalls, ref first);
            AppendNumberField(s_lineBuilder, "multi_draw_calls", Engine.Rendering.Stats.Frame.MultiDrawCalls, ref first);
            AppendNumberField(s_lineBuilder, "triangles_rendered", Engine.Rendering.Stats.Frame.TrianglesRendered, ref first);
            AppendNumberField(s_lineBuilder, "gpu_mapped_buffers", Engine.Rendering.Stats.GpuReadback.GpuMappedBuffers, ref first);
            AppendNumberField(s_lineBuilder, "gpu_readback_bytes", Engine.Rendering.Stats.GpuReadback.GpuReadbackBytes, ref first);
            AppendNumberField(s_lineBuilder, "indirect_count_calls", Engine.Rendering.Stats.RendererState.IndirectCountCalls, ref first);
            AppendNumberField(s_lineBuilder, "shader_program_switches", Engine.Rendering.Stats.RendererState.ShaderProgramSwitches, ref first);
            AppendNumberField(s_lineBuilder, "program_pipeline_switches", Engine.Rendering.Stats.RendererState.ProgramPipelineSwitches, ref first);
            AppendNumberField(s_lineBuilder, "vao_binds", Engine.Rendering.Stats.RendererState.VaoBinds, ref first);
            AppendNumberField(s_lineBuilder, "vao_bind_skips", Engine.Rendering.Stats.RendererState.VaoBindSkips, ref first);
            AppendNumberField(s_lineBuilder, "array_buffer_binds", Engine.Rendering.Stats.RendererState.ArrayBufferBinds, ref first);
            AppendNumberField(s_lineBuilder, "element_array_buffer_binds", Engine.Rendering.Stats.RendererState.ElementArrayBufferBinds, ref first);
            AppendNumberField(s_lineBuilder, "draw_indirect_buffer_binds", Engine.Rendering.Stats.RendererState.DrawIndirectBufferBinds, ref first);
            AppendNumberField(s_lineBuilder, "parameter_buffer_binds", Engine.Rendering.Stats.RendererState.ParameterBufferBinds, ref first);
            AppendNumberField(s_lineBuilder, "ssbo_binds", Engine.Rendering.Stats.RendererState.SsboBinds, ref first);
            AppendNumberField(s_lineBuilder, "ubo_binds", Engine.Rendering.Stats.RendererState.UboBinds, ref first);
            AppendNumberField(s_lineBuilder, "texture_binds", Engine.Rendering.Stats.RendererState.TextureBinds, ref first);
            AppendNumberField(s_lineBuilder, "texture_bind_skips", Engine.Rendering.Stats.RendererState.TextureBindSkips, ref first);
            AppendNumberField(s_lineBuilder, "texture_unit_switches", Engine.Rendering.Stats.RendererState.TextureUnitSwitches, ref first);
            AppendNumberField(s_lineBuilder, "uniform_calls", Engine.Rendering.Stats.RendererState.UniformCalls, ref first);
            AppendNumberField(s_lineBuilder, "sampler_uniform_calls", Engine.Rendering.Stats.RendererState.SamplerUniformCalls, ref first);
            AppendNumberField(s_lineBuilder, "buffer_upload_bytes", Engine.Rendering.Stats.RendererState.BufferUploadBytes, ref first);
            AppendNumberField(s_lineBuilder, "barrier_calls", Engine.Rendering.Stats.RendererState.BarrierCalls, ref first);
            AppendNumberField(s_lineBuilder, "barrier_all", Engine.Rendering.Stats.RendererState.BarrierAll, ref first);
            AppendNumberField(s_lineBuilder, "barrier_command", Engine.Rendering.Stats.RendererState.BarrierCommand, ref first);
            AppendNumberField(s_lineBuilder, "barrier_buffer_update", Engine.Rendering.Stats.RendererState.BarrierBufferUpdate, ref first);
            AppendNumberField(s_lineBuilder, "barrier_shader_storage", Engine.Rendering.Stats.RendererState.BarrierShaderStorage, ref first);
            AppendNumberField(s_lineBuilder, "barrier_texture_fetch", Engine.Rendering.Stats.RendererState.BarrierTextureFetch, ref first);
            AppendNumberField(s_lineBuilder, "barrier_texture_update", Engine.Rendering.Stats.RendererState.BarrierTextureUpdate, ref first);
            AppendNumberField(s_lineBuilder, "barrier_framebuffer", Engine.Rendering.Stats.RendererState.BarrierFramebuffer, ref first);
            AppendNumberField(s_lineBuilder, "timestamp_query_count", Engine.Rendering.Stats.RendererState.TimestampQueryCount, ref first);
            AppendNumberField(s_lineBuilder, "timestamp_query_readback_bytes", Engine.Rendering.Stats.RendererState.TimestampQueryReadbackBytes, ref first);
            AppendNumberField(s_lineBuilder, "timestamp_dense_mode_frames", Engine.Rendering.Stats.RendererState.TimestampDenseModeFrames, ref first);
            AppendNumberField(s_lineBuilder, "redundant_state_skips", Engine.Rendering.Stats.RendererState.RedundantStateSkips, ref first);
            AppendNumberField(s_lineBuilder, "cpu_direct_draw_calls", Engine.Rendering.Stats.RendererState.CpuDirectDrawCalls, ref first);
            AppendNumberField(s_lineBuilder, "gpu_indirect_draw_calls", Engine.Rendering.Stats.RendererState.GpuIndirectDrawCalls, ref first);
            AppendNumberField(s_lineBuilder, "gpu_meshlet_draw_calls", Engine.Rendering.Stats.RendererState.GpuMeshletDrawCalls, ref first);
            AppendNumberField(s_lineBuilder, "unknown_strategy_draw_calls", Engine.Rendering.Stats.RendererState.UnknownStrategyDrawCalls, ref first);
            AppendNumberField(s_lineBuilder, "visible_renderer_count", Engine.Rendering.Stats.SceneAssets.VisibleRendererCount, ref first);
            AppendNumberField(s_lineBuilder, "visible_submesh_count", Engine.Rendering.Stats.SceneAssets.VisibleSubmeshCount, ref first);
            AppendNumberField(s_lineBuilder, "visible_triangle_count", Engine.Rendering.Stats.SceneAssets.VisibleTriangleCount, ref first);
            AppendNumberField(s_lineBuilder, "material_slot_count", Engine.Rendering.Stats.SceneAssets.MaterialSlotCount, ref first);
            AppendNumberField(s_lineBuilder, "active_material_count", Engine.Rendering.Stats.SceneAssets.ActiveMaterialCount, ref first);
            AppendNumberField(s_lineBuilder, "texture_count", Engine.Rendering.Stats.SceneAssets.TextureCount, ref first);
            AppendNumberField(s_lineBuilder, "resident_texture_memory_bytes", Engine.Rendering.Stats.SceneAssets.ResidentTextureMemoryBytes, ref first);
            AppendNumberField(s_lineBuilder, "texture_upload_jobs", Engine.Rendering.Stats.SceneAssets.TextureUploadJobs, ref first);
            AppendNumberField(s_lineBuilder, "texture_upload_bytes", Engine.Rendering.Stats.SceneAssets.TextureUploadBytes, ref first);
            AppendNumberField(s_lineBuilder, "texture_upload_ms", Engine.Rendering.Stats.SceneAssets.TextureUploadMs, ref first);
            AppendNumberField(s_lineBuilder, "shader_variants_requested", Engine.Rendering.Stats.SceneAssets.ShaderVariantsRequested, ref first);
            AppendNumberField(s_lineBuilder, "shader_variants_warming", Engine.Rendering.Stats.SceneAssets.ShaderVariantsWarming, ref first);
            AppendNumberField(s_lineBuilder, "shader_variants_linked", Engine.Rendering.Stats.SceneAssets.ShaderVariantsLinked, ref first);
            AppendNumberField(s_lineBuilder, "shader_variants_failed", Engine.Rendering.Stats.SceneAssets.ShaderVariantsFailed, ref first);
            AppendNumberField(s_lineBuilder, "shader_variants_loaded_from_disk_cache", Engine.Rendering.Stats.SceneAssets.ShaderVariantsLoadedFromDiskCache, ref first);
            AppendNumberField(s_lineBuilder, "shader_variants_generated_this_run", Engine.Rendering.Stats.SceneAssets.ShaderVariantsGeneratedThisRun, ref first);
            AppendNumberField(s_lineBuilder, "skinned_renderer_count", Engine.Rendering.Stats.SceneAssets.SkinnedRendererCount, ref first);
            AppendNumberField(s_lineBuilder, "bone_matrix_upload_bytes", Engine.Rendering.Stats.SceneAssets.BoneMatrixUploadBytes, ref first);
            AppendNumberField(s_lineBuilder, "blendshape_weight_upload_bytes", Engine.Rendering.Stats.SceneAssets.BlendshapeWeightUploadBytes, ref first);
            AppendNumberField(s_lineBuilder, "blendshape_active_list_upload_bytes", Engine.Rendering.Stats.SceneAssets.BlendshapeActiveListUploadBytes, ref first);
            AppendNumberField(s_lineBuilder, "blendshape_delta_bytes", Engine.Rendering.Stats.SceneAssets.BlendshapeDeltaBytes, ref first);
            AppendNumberField(s_lineBuilder, "skinning_core_influence_bytes", Engine.Rendering.Stats.SceneAssets.SkinningCoreInfluenceBytes, ref first);
            AppendNumberField(s_lineBuilder, "skinning_spill_header_bytes", Engine.Rendering.Stats.SceneAssets.SkinningSpillHeaderBytes, ref first);
            AppendNumberField(s_lineBuilder, "skinning_spill_entry_bytes", Engine.Rendering.Stats.SceneAssets.SkinningSpillEntryBytes, ref first);
            AppendNumberField(s_lineBuilder, "skin_palette_upload_bytes", Engine.Rendering.Stats.SceneAssets.SkinPaletteUploadBytes, ref first);
            AppendNumberField(s_lineBuilder, "skinning_compute_dispatch_count", Engine.Rendering.Stats.SceneAssets.SkinningComputeDispatchCount, ref first);
            AppendNumberField(s_lineBuilder, "blendshape_compute_dispatch_count", Engine.Rendering.Stats.SceneAssets.BlendshapeComputeDispatchCount, ref first);
            AppendNumberField(s_lineBuilder, "skipped_skinning_compute_dispatch_count", Engine.Rendering.Stats.SceneAssets.SkippedSkinningComputeDispatchCount, ref first);
            AppendNumberField(s_lineBuilder, "skipped_blendshape_compute_dispatch_count", Engine.Rendering.Stats.SceneAssets.SkippedBlendshapeComputeDispatchCount, ref first);
            AppendNumberField(s_lineBuilder, "reused_skinned_output_buffer_count", Engine.Rendering.Stats.SceneAssets.ReusedSkinnedOutputBufferCount, ref first);
            AppendNumberField(s_lineBuilder, "live_skinning_shader_permutation_count", Engine.Rendering.Stats.SceneAssets.LiveSkinningShaderPermutationCount, ref first);
            AppendNumberField(s_lineBuilder, "blendshape_authored_shape_count", Engine.Rendering.Stats.SceneAssets.BlendshapeAuthoredShapeCount, ref first);
            AppendNumberField(s_lineBuilder, "blendshape_active_shape_count", Engine.Rendering.Stats.SceneAssets.BlendshapeActiveShapeCount, ref first);
            AppendNumberField(s_lineBuilder, "blendshape_affected_vertex_count", Engine.Rendering.Stats.SceneAssets.BlendshapeAffectedVertexCount, ref first);
            AppendNumberField(s_lineBuilder, "compacted_active_blendshape_count", Engine.Rendering.Stats.SceneAssets.CompactedActiveBlendshapeCount, ref first);
            AppendNumberField(s_lineBuilder, "live_blendshape_shader_permutation_count", Engine.Rendering.Stats.SceneAssets.LiveBlendshapeShaderPermutationCount, ref first);
            AppendNumberField(s_lineBuilder, "avatar_source_mesh_count", Engine.Rendering.Stats.SceneAssets.AvatarSourceMeshCount, ref first);
            AppendNumberField(s_lineBuilder, "avatar_optimized_lod_count", Engine.Rendering.Stats.SceneAssets.AvatarOptimizedLodCount, ref first);
            AppendNumberField(s_lineBuilder, "avatar_meshlet_count", Engine.Rendering.Stats.SceneAssets.AvatarMeshletCount, ref first);
            AppendNumberField(s_lineBuilder, "avatar_visibility_buffer_count", Engine.Rendering.Stats.SceneAssets.AvatarVisibilityBufferCount, ref first);
            AppendNumberField(s_lineBuilder, "avatar_cluster_virtualized_count", Engine.Rendering.Stats.SceneAssets.AvatarClusterVirtualizedCount, ref first);
            AppendNumberField(s_lineBuilder, "avatar_octahedral_impostor_count", Engine.Rendering.Stats.SceneAssets.AvatarOctahedralImpostorCount, ref first);
            AppendNumberField(s_lineBuilder, "avatar_gaussian_splat_count", Engine.Rendering.Stats.SceneAssets.AvatarGaussianSplatCount, ref first);
            AppendRawJsonField(s_lineBuilder, "render_asset_cost_rows", JsonSerializer.Serialize(Engine.Rendering.Stats.SceneAssets.GetAssetCostRows()), ref first);
            AppendNumberField(s_lineBuilder, "gpu_driven_culled_command_count", Engine.Rendering.Stats.GpuDriven.CulledCommandCount, ref first);
            AppendNumberField(s_lineBuilder, "gpu_driven_active_bucket_count", Engine.Rendering.Stats.GpuDriven.ActiveBucketCount, ref first);
            AppendNumberField(s_lineBuilder, "gpu_driven_empty_bucket_skips", Engine.Rendering.Stats.GpuDriven.EmptyBucketSkips, ref first);
            AppendNumberField(s_lineBuilder, "gpu_driven_full_bucket_scans", Engine.Rendering.Stats.GpuDriven.FullBucketScans, ref first);
            AppendNumberField(s_lineBuilder, "gpu_driven_material_scatter_dispatches", Engine.Rendering.Stats.GpuDriven.MaterialScatterDispatches, ref first);
            AppendNumberField(s_lineBuilder, "gpu_driven_indirect_command_generation_ms", Engine.Rendering.Stats.GpuDriven.IndirectCommandGenerationMs, ref first);
            AppendNumberField(s_lineBuilder, "gpu_driven_gpu_cull_ms", Engine.Rendering.Stats.GpuDriven.GpuCullMs, ref first);
            AppendNumberField(s_lineBuilder, "gpu_driven_gpu_sort_compact_ms", Engine.Rendering.Stats.GpuDriven.GpuSortCompactMs, ref first);
            AppendNumberField(s_lineBuilder, "gpu_driven_delayed_draw_count_buffer_value", Engine.Rendering.Stats.GpuDriven.DelayedDrawCountBufferValue, ref first);
            AppendNumberField(s_lineBuilder, "gpu_driven_delayed_diagnostic_readback_bytes", Engine.Rendering.Stats.GpuDriven.DelayedDiagnosticReadbackBytes, ref first);
            AppendNumberField(s_lineBuilder, "gpu_driven_delayed_diagnostic_readback_count", Engine.Rendering.Stats.GpuDriven.DelayedDiagnosticReadbackCount, ref first);
            AppendNumberField(s_lineBuilder, "gpu_compaction_overflow", Engine.Rendering.Stats.GpuDriven.GpuCompactionOverflow, ref first);
            AppendNumberField(s_lineBuilder, "gpu_active_list_overflow", Engine.Rendering.Stats.GpuDriven.ActiveListOverflow, ref first);
            AppendNumberField(s_lineBuilder, "gpu_bucket_overflow", Engine.Rendering.Stats.GpuDriven.BucketOverflow, ref first);
            AppendNumberField(s_lineBuilder, "gpu_meshlet_overflow", Engine.Rendering.Stats.GpuDriven.MeshletOverflow, ref first);
            AppendStringField(s_lineBuilder, "gpu_hiz_mode", Engine.Rendering.Stats.GpuDriven.HiZMode, ref first);
            AppendNumberField(s_lineBuilder, "gpu_hiz_one_phase_frames", Engine.Rendering.Stats.GpuDriven.HiZOnePhaseFrames, ref first);
            AppendNumberField(s_lineBuilder, "gpu_hiz_two_phase_frames", Engine.Rendering.Stats.GpuDriven.HiZTwoPhaseFrames, ref first);
            AppendNumberField(s_lineBuilder, "gpu_hiz_phase_one_draws", Engine.Rendering.Stats.GpuDriven.HiZPhaseOneDraws, ref first);
            AppendNumberField(s_lineBuilder, "gpu_hiz_phase_two_draws", Engine.Rendering.Stats.GpuDriven.HiZPhaseTwoDraws, ref first);
            AppendNumberField(s_lineBuilder, "visibility_pass_draws", Engine.Rendering.Stats.GpuDriven.VisibilityPassDraws, ref first);
            AppendNumberField(s_lineBuilder, "visibility_classified_pixels", Engine.Rendering.Stats.GpuDriven.VisibilityClassifiedPixels, ref first);
            AppendNumberField(s_lineBuilder, "visibility_active_material_tiles", Engine.Rendering.Stats.GpuDriven.VisibilityActiveMaterialTiles, ref first);
            AppendNumberField(s_lineBuilder, "visibility_classification_overflow", Engine.Rendering.Stats.GpuDriven.VisibilityClassificationOverflow, ref first);
            AppendNumberField(s_lineBuilder, "visibility_reconstruction_ms", Engine.Rendering.Stats.GpuDriven.VisibilityReconstructionMs, ref first);
            AppendNumberField(s_lineBuilder, "visibility_material_shading_ms", Engine.Rendering.Stats.GpuDriven.VisibilityMaterialShadingMs, ref first);
            AppendNumberField(s_lineBuilder, "gpu_cpu_fallback_events", Engine.Rendering.Stats.GpuFallback.GpuCpuFallbackEvents, ref first);
            AppendNumberField(s_lineBuilder, "gpu_cpu_fallback_recovered_commands", Engine.Rendering.Stats.GpuFallback.GpuCpuFallbackRecoveredCommands, ref first);
            AppendNumberField(s_lineBuilder, "forbidden_gpu_fallback_events", Engine.Rendering.Stats.GpuFallback.ForbiddenGpuFallbackEvents, ref first);
            AppendNumberField(s_lineBuilder, "gpu_meshlet_requested_frames", Engine.Rendering.Stats.GpuMeshlets.GpuMeshletRequestedFrames, ref first);
            AppendNumberField(s_lineBuilder, "gpu_meshlet_production_frames", Engine.Rendering.Stats.GpuMeshlets.GpuMeshletProductionFrames, ref first);
            AppendNumberField(s_lineBuilder, "gpu_meshlet_fallback_frames", Engine.Rendering.Stats.GpuMeshlets.GpuMeshletFallbackFrames, ref first);
            AppendNumberField(s_lineBuilder, "gpu_meshlet_dispatch_skipped", Engine.Rendering.Stats.GpuMeshlets.GpuMeshletDispatchSkipped, ref first);
            AppendNumberField(s_lineBuilder, "gpu_meshlet_task_records_emitted", Engine.Rendering.Stats.GpuMeshlets.GpuMeshletTaskRecordsEmitted, ref first);
            AppendNumberField(s_lineBuilder, "gpu_meshlet_task_records_frustum_culled", Engine.Rendering.Stats.GpuMeshlets.GpuMeshletTaskRecordsFrustumCulled, ref first);
            AppendNumberField(s_lineBuilder, "gpu_meshlet_task_records_cone_culled", Engine.Rendering.Stats.GpuMeshlets.GpuMeshletTaskRecordsConeCulled, ref first);
            AppendNumberField(s_lineBuilder, "gpu_meshlet_task_records_hiz_culled", Engine.Rendering.Stats.GpuMeshlets.GpuMeshletTaskRecordsHiZCulled, ref first);
            AppendNumberField(s_lineBuilder, "gpu_meshlet_expansion_overflow_count", Engine.Rendering.Stats.GpuMeshlets.GpuMeshletExpansionOverflowCount, ref first);
            AppendNumberField(s_lineBuilder, "gpu_meshlet_buffer_bytes_resident", Engine.Rendering.Stats.GpuMeshlets.GpuMeshletBufferBytesResident, ref first);
            AppendNumberField(s_lineBuilder, "gpu_meshlet_last_visible_meshlet_count", Engine.Rendering.Stats.GpuMeshlets.LastVisibleMeshletCount, ref first);
            AppendNumberField(s_lineBuilder, "gpu_meshlet_last_dispatched_meshlet_count", Engine.Rendering.Stats.GpuMeshlets.LastDispatchedMeshletCount, ref first);
            AppendNumberField(s_lineBuilder, "gpu_meshlet_last_task_record_overflow_count", Engine.Rendering.Stats.GpuMeshlets.LastTaskRecordOverflowCount, ref first);
            AppendNumberField(s_lineBuilder, "gpu_meshlet_last_dispatch_ms", Engine.Rendering.Stats.GpuMeshlets.LastDispatchTime.TotalMilliseconds, ref first);
            AppendNumberField(s_lineBuilder, "gpu_meshlet_last_readback_bytes", Engine.Rendering.Stats.GpuMeshlets.LastReadbackBytes, ref first);
            AppendNumberField(s_lineBuilder, "gpu_meshlet_cache_hits", Engine.Rendering.Stats.GpuMeshlets.GpuMeshletCacheHits, ref first);
            AppendNumberField(s_lineBuilder, "gpu_meshlet_cache_misses", Engine.Rendering.Stats.GpuMeshlets.GpuMeshletCacheMisses, ref first);
            AppendNumberField(s_lineBuilder, "gpu_meshlet_cache_stale", Engine.Rendering.Stats.GpuMeshlets.GpuMeshletCacheStale, ref first);
            AppendNumberField(s_lineBuilder, "fbo_bind_count", Engine.Rendering.Stats.Vram.FBOBindCount, ref first);
            AppendNumberField(s_lineBuilder, "fbo_bandwidth_bytes", Engine.Rendering.Stats.Vram.FBOBandwidthBytes, ref first);
            AppendNumberField(s_lineBuilder, "allocated_vram_bytes", Engine.Rendering.Stats.Vram.AllocatedVRAMBytes, ref first);

            AppendBoolField(s_lineBuilder, "gpu_pipeline_profiling_enabled", Engine.Rendering.Stats.GpuPipelineProfiler.GpuRenderPipelineProfilingEnabled, ref first);
            AppendBoolField(s_lineBuilder, "gpu_pipeline_profiling_supported", Engine.Rendering.Stats.GpuPipelineProfiler.GpuRenderPipelineProfilingSupported, ref first);
            AppendBoolField(s_lineBuilder, "gpu_pipeline_timings_ready", gpuTimingsReady, ref first);
            AppendStringField(s_lineBuilder, "gpu_pipeline_backend", Engine.Rendering.Stats.GpuPipelineProfiler.GpuRenderPipelineBackend, ref first);
            AppendStringField(s_lineBuilder, "gpu_pipeline_status", Engine.Rendering.Stats.GpuPipelineProfiler.GpuRenderPipelineStatusMessage, ref first);
            AppendNumberField(s_lineBuilder, "gpu_pipeline_frame_ms", gpuPipelineMs, ref first);

            AppendNumberField(s_lineBuilder, "vulkan_indirect_api_calls", Engine.Rendering.Stats.Vulkan.VulkanIndirectApiCalls, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_indirect_submitted_draws", Engine.Rendering.Stats.Vulkan.VulkanIndirectSubmittedDraws, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_requested_draws", Engine.Rendering.Stats.Vulkan.VulkanRequestedDraws, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_consumed_draws", Engine.Rendering.Stats.Vulkan.VulkanConsumedDraws, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_oom_fallback_count", Engine.Rendering.Stats.Vulkan.VulkanOomFallbackCount, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_frame_total_ms", Engine.Rendering.Stats.Vulkan.VulkanFrameTotalMs, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_frame_gpu_command_buffer_ms", Engine.Rendering.Stats.Vulkan.VulkanFrameGpuCommandBufferMs, ref first);

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

        private static void AppendRawJsonField(StringBuilder builder, string name, string json, ref bool first)
        {
            AppendFieldPrefix(builder, name, ref first);
            if (string.IsNullOrWhiteSpace(json))
                builder.Append("null");
            else
                builder.Append(json);
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

        private static double? TryParsePositiveDouble(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return null;

            return double.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double value) && value > 0.0
                ? value
                : null;
        }

        private static string CaptureBenchmarkEnvironmentErrors()
        {
            List<string> errors = [];

            ValidateEnvFlag(errors, "XRE_PROFILER_ENABLED");
            ValidateEnvFlag(errors, "XRE_PROFILE_CAPTURE");
            ValidateEnvFlag(errors, "XRE_PROFILE_AUTO_DUMP");
            ValidateEnvFlag(errors, "XRE_P3_LOGGING");
            ValidateEnvFlag(errors, "XRE_BUCKET_LOOP_DRY_RUN");
            ValidateEnvFlag(errors, "XRE_SKIP_COMMAND_SWAP_IF_CLEAN");
            ValidateEnvFlag(errors, "XRE_BUCKET_LOOP_SKIP_EMPTY");
            ValidateEnvFlag(errors, "XRE_FORCE_SINGLE_BUCKET");
            ValidateEnvFlag(errors, "XRE_HIZ_CULL_TRACE");
            ValidateEnvFlag(errors, "XRE_GPU_TIMESTAMP_DENSE");

            ValidateEnvEnum(
                errors,
                "XRE_FORCE_MESH_SUBMISSION_STRATEGY",
                "CpuDirect",
                "GpuIndirectInstrumented",
                "GpuIndirectZeroReadback",
                "GpuMeshletInstrumented",
                "GpuMeshletZeroReadback");
            ValidateEnvEnum(
                errors,
                "XRE_ZERO_READBACK_MATERIAL_DRAW_PATH",
                "FullBucketScan",
                "ActiveBucketList",
                "MaterialTable",
                "BindlessMaterialTable");
            ValidateEnvEnum(errors, "XRE_PROFILE_CACHE_MODE", "Cold", "Warm");
            ValidateEnvEnum(errors, "XRE_SHADER_CACHE_MODE", "Cold", "Warm");
            ValidateEnvEnum(errors, "XRE_TEXTURE_CACHE_MODE", "Cold", "Warm");

            ValidateEnvPositiveDouble(errors, "XRE_TARGET_REFRESH_HZ");
            ValidateEnvPositiveDouble(errors, "XRE_UPDATE_FPS");
            ValidateEnvPositiveDouble(errors, "XRE_PROFILE_RENDER_SCALE");
            ValidateEnvPositiveDouble(errors, "XRE_PROFILE_WARMUP_SEC");
            ValidateEnvPositiveDouble(errors, "XRE_PROFILE_CAPTURE_SEC");

            return errors.Count == 0 ? string.Empty : string.Join("; ", errors);
        }

        private static void ValidateEnvFlag(List<string> errors, string name)
        {
            string? raw = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrWhiteSpace(raw))
                return;

            string value = raw.Trim();
            if (value is "0" or "1" ||
                value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("false", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("no", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("on", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("off", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            errors.Add(name + " must be a boolean flag, got '" + value + "'");
        }

        private static void ValidateEnvEnum(List<string> errors, string name, params string[] allowed)
        {
            string? raw = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrWhiteSpace(raw))
                return;

            string value = raw.Trim();
            if (allowed.Any(allowedValue => string.Equals(allowedValue, value, StringComparison.OrdinalIgnoreCase)))
                return;

            errors.Add(name + " must be one of [" + string.Join(", ", allowed) + "], got '" + value + "'");
        }

        private static void ValidateEnvPositiveDouble(List<string> errors, string name)
        {
            string? raw = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrWhiteSpace(raw))
                return;

            if (TryParsePositiveDouble(raw) is not null)
                return;

            errors.Add(name + " must be a positive number, got '" + raw.Trim() + "'");
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
            int SchemaVersion,
            string CaptureMode,
            string RunLabel,
            string WorldMode,
            string ForcedStrategy,
            string EffectiveStrategy,
            string ZeroReadbackMaterialDrawPath,
            string ZeroReadbackMaterialDrawPathEnv,
            string Backend,
            string GpuName,
            string GpuVendor,
            string GpuDeviceId,
            string Driver,
            string Scene,
            string Camera,
            string Lights,
            string Viewport,
            string RenderScale,
            string StereoMode,
            string ValidationLayersEnabled,
            string DebugOutputEnabled,
            string ShaderCacheState,
            string TextureCacheState,
            string CacheMode,
            string GpuClockPolicy,
            double? TargetRefreshHz,
            double? XrFrameBudgetMs,
            string BenchmarkPhase,
            double? WarmupSeconds,
            double? CaptureSeconds,
            bool BenchmarkEnvironmentValid,
            string BenchmarkEnvironmentErrors,
            bool GpuTimestampDenseMode,
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
