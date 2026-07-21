using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using XREngine.Timers;
using OcclusionTelemetry = XREngine.Rendering.Occlusion.OcclusionTelemetry;

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
        private const int ProfileCaptureSchemaVersion = 4;
        private const int RuntimeCaptureRetentionCount = 3;
        private const int FlushIntervalMilliseconds = 1000;
        private const int MaxBufferedCharacters = 256 * 1024;
        private const double MaxRuntimeCaptureSeconds = 600.0;

        private static readonly bool s_envCaptureEnabled = IsEnvFlagEnabled(XREngineEnvironmentVariables.ProfileCapture);
        private static readonly bool s_envAutoDumpGpuTimings =
            s_envCaptureEnabled || IsEnvFlagEnabled(XREngineEnvironmentVariables.ProfileAutoDump);
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
                if (metadata.FrameOutputWorkloadIdentityHash != 0UL)
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
            {
                if (!s_manifestWritten)
                {
                    Engine.Rendering.Stats.FrameOutputManifestSnapshot currentOutputManifest =
                        Engine.Rendering.Stats.FrameOutputs.LastManifest;
                    if (currentOutputManifest.WorkloadIdentityHash != 0UL)
                    {
                        s_metadata = s_metadata with
                        {
                            FrameOutputWorkloadIdentityHash = currentOutputManifest.WorkloadIdentityHash,
                            OutputInventory = CaptureOutputInventory(currentOutputManifest),
                        };
                    }
                }
                return s_metadata;
            }

            bool runtimeCapture = s_runtimeCaptureEnabled;
            string runLabel = runtimeCapture && !string.IsNullOrWhiteSpace(s_runtimeRunLabel)
                ? s_runtimeRunLabel
                : Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.ProfileRunLabel) ?? string.Empty;

            string targetRefreshHzEnv = Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.TargetRefreshHz) ??
                Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.UpdateFps) ??
                string.Empty;
            double? targetRefreshHz = TryParsePositiveDouble(targetRefreshHzEnv);
            double? xrFrameBudgetMs = targetRefreshHz is > 0.0 ? 1000.0 / targetRefreshHz.Value : null;
            string benchmarkErrors = CaptureBenchmarkEnvironmentErrors();
            string renderTargetModeEnv = Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.VkRenderTargetMode) ?? string.Empty;
            string renderTargetModeSetting = CaptureString(() => Engine.EffectiveSettings.VulkanRenderTargetMode.ToString());
            string primaryReuseEnvironment = Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.VulkanPrimaryCommandBufferReuse) ?? string.Empty;
            bool primaryReuseSetting = CaptureBoolean(() => Engine.Rendering.Settings.EnableVulkanPrimaryCommandBufferReuse);
            bool primaryReuseEnabled = ResolveOptionalBooleanOverride(primaryReuseEnvironment) ?? primaryReuseSetting;
            string primaryReusePolicy = string.IsNullOrWhiteSpace(primaryReuseEnvironment)
                ? $"Setting:{primaryReuseSetting}"
                : $"Environment:{primaryReuseEnvironment}";
            string sceneIdentity = CaptureSceneIdentity();
            string settingsIdentity = BuildSettingsIdentity(renderTargetModeEnv, renderTargetModeSetting);
            string sceneIdentityHash = ComputeStableIdentityHash(sceneIdentity);
            string settingsIdentityHash = ComputeStableIdentityHash(settingsIdentity);
            string sceneSettingsHash = ComputeStableIdentityHash(sceneIdentity + "|" + settingsIdentity);
            Engine.Rendering.Stats.FrameOutputManifestSnapshot outputManifest = Engine.Rendering.Stats.FrameOutputs.LastManifest;

            s_metadata = new RunMetadata(
                SchemaVersion: ProfileCaptureSchemaVersion,
                CaptureMode: runtimeCapture ? "runtime" : "launch",
                RunLabel: runLabel,
                WorldMode: Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.WorldMode) ?? string.Empty,
                ForcedStrategy: Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.ForceMeshSubmissionStrategy) ?? string.Empty,
                EffectiveStrategy: CaptureString(() => Engine.Rendering.ResolveMeshSubmissionStrategy().ToString()),
                ZeroReadbackMaterialDrawPath: CaptureString(() => Engine.EffectiveSettings.ZeroReadbackMaterialDrawPath.ToString()),
                ZeroReadbackMaterialDrawPathEnv: Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.ZeroReadbackMaterialDrawPath) ?? string.Empty,
                Backend: CaptureString(() => Engine.Rendering.Stats.RendererState.ActiveRenderBackend),
                GpuName: CaptureString(() => RuntimeEngine.Rendering.State.OpenGLRendererName ?? RuntimeEngine.Rendering.State.VulkanDeviceName ?? string.Empty),
                GpuVendor: CaptureString(() => RuntimeEngine.Rendering.State.OpenGLVendor ?? string.Empty),
                GpuDeviceId: Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.GpuDeviceId) ?? string.Empty,
                Driver: Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.GpuDriver) ?? string.Empty,
                Scene: Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.ProfileScene) ?? string.Empty,
                Camera: Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.ProfileCamera) ?? string.Empty,
                Lights: Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.ProfileLights) ?? string.Empty,
                Viewport: Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.ProfileViewport) ?? string.Empty,
                RenderScale: Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.ProfileRenderScale) ??
                    CaptureString(() => Engine.Rendering.Settings.TsrRenderScale.ToString(CultureInfo.InvariantCulture)),
                SceneIdentity: sceneIdentity,
                SceneIdentityHash: sceneIdentityHash,
                SettingsIdentityHash: settingsIdentityHash,
                SceneSettingsHash: sceneSettingsHash,
                FrameOutputWorkloadIdentityHash: outputManifest.WorkloadIdentityHash,
                OutputInventory: CaptureOutputInventory(outputManifest),
                StereoMode: CaptureString(() => Engine.Rendering.Stats.RendererState.ActiveStereoMode),
                VrViewRenderModeRequested: CaptureString(() => Engine.Rendering.Stats.RendererState.ActiveVrViewRenderModeRequested),
                VrViewRenderModeEffective: CaptureString(() => Engine.Rendering.Stats.RendererState.ActiveVrViewRenderModeEffective),
                VrViewRenderImplementationPath: CaptureString(() => Engine.Rendering.Stats.RendererState.ActiveVrViewRenderImplementationPath),
                VrTemporalHistoryPolicy: CaptureString(() => Engine.Rendering.Stats.RendererState.ActiveVrTemporalHistoryPolicy),
                VrMirrorMode: CaptureString(() => Engine.Rendering.Settings.VrMirrorMode.ToString()),
                RenderWindowsWhileInVR: CaptureString(() => Engine.Rendering.Settings.RenderWindowsWhileInVR ? "true" : "false"),
                VrMirrorComposeFromEyeTextures: CaptureString(() => Engine.Rendering.Settings.VrMirrorComposeFromEyeTextures ? "true" : "false"),
                VrDesktopEditorTargetRateHz: CaptureString(() => Engine.Rendering.Settings.VrDesktopEditorTargetRateHz.ToString(CultureInfo.InvariantCulture)),
                VrCyclopeanDesktopTargetRateHz: CaptureString(() => Engine.Rendering.Settings.VrCyclopeanDesktopTargetRateHz.ToString(CultureInfo.InvariantCulture)),
                VrDesktopAutoSkipWhenOverBudget: CaptureString(() => Engine.Rendering.Settings.VrDesktopAutoSkipWhenOverBudget ? "true" : "false"),
                VulkanRenderTargetModeEnvironment: renderTargetModeEnv,
                VulkanRenderTargetModeSetting: renderTargetModeSetting,
                VulkanPrimaryCommandBufferReusePolicy: primaryReusePolicy,
                VulkanPrimaryCommandBufferReuseEnabled: primaryReuseEnabled,
                VulkanObsHookPolicy: Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.VkObsHook) ?? "Auto",
                VulkanSkipImGui: IsEnvFlagEnabled(XREngineEnvironmentVariables.VkSkipImGui),
                ValidationLayersEnabled: CaptureString(() => Engine.Rendering.Stats.RendererState.ValidationLayersEnabled ? "true" : "false"),
                DebugOutputEnabled: CaptureString(() => Engine.Rendering.Stats.RendererState.DebugOutputEnabled ? "true" : "false"),
                DeferredDebugView: CaptureString(() => global::XREngine.Rendering.RenderDiagnosticsFlags.DeferredDebugView.ToString(CultureInfo.InvariantCulture)),
                DeferredDebugEnv: Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.DeferredDebug) ?? string.Empty,
                ShaderCacheState: Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.ShaderCacheMode) ?? string.Empty,
                TextureCacheState: Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.TextureCacheMode) ?? string.Empty,
                CacheMode: Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.ProfileCacheMode) ?? string.Empty,
                GpuClockPolicy: Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.GpuClockPolicy) ?? string.Empty,
                TargetRefreshHz: targetRefreshHz,
                XrFrameBudgetMs: xrFrameBudgetMs,
                BenchmarkPhase: Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.ProfilePhase) ?? string.Empty,
                WarmupSeconds: TryParsePositiveDouble(Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.ProfileWarmupSeconds)),
                CaptureSeconds: TryParsePositiveDouble(Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.ProfileCaptureSeconds)),
                BenchmarkEnvironmentValid: string.IsNullOrWhiteSpace(benchmarkErrors),
                BenchmarkEnvironmentErrors: benchmarkErrors,
                GpuTimestampDenseMode: IsEnvFlagEnabled(XREngineEnvironmentVariables.GpuTimestampDense),
                P3Logging: Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.P3Logging) ?? string.Empty,
                BucketLoopDryRun: Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.BucketLoopDryRun) ?? string.Empty,
                SkipCommandSwapIfClean: Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.SkipCommandSwapIfClean) ?? string.Empty,
                BucketLoopSkipEmpty: Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.BucketLoopSkipEmpty) ?? string.Empty,
                ForceSingleBucket: Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.ForceSingleBucket) ?? string.Empty,
                Configuration: CaptureString(() => typeof(Engine).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyConfigurationAttribute), false)
                    .OfType<System.Reflection.AssemblyConfigurationAttribute>()
                    .FirstOrDefault()?.Configuration ?? string.Empty),
                GameBuildConfiguration: CaptureString(() => Engine.GameSettings?.BuildSettings?.Configuration.ToString() ?? string.Empty),
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
                schema = "xrengine.profile_capture.render_stats.v4",
                schema_version = ProfileCaptureSchemaVersion,
                fields_note = "One JSON object per completed render frame. CPU frame timings are wall-clock thread loop durations; GPU pipeline timings are backend timestamp-query snapshots when ready.",
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
            Engine.Rendering.Stats.FrameOutputManifestSnapshot frameOutputs = Engine.Rendering.Stats.FrameOutputs.LastManifest;

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
            AppendNumberField(s_lineBuilder, "update_frame_id", Engine.Rendering.Stats.FrameLifecycle.UpdateFrameId, ref first);
            AppendNumberField(s_lineBuilder, "collect_frame_id", Engine.Rendering.Stats.FrameLifecycle.CollectFrameId, ref first);
            AppendNumberField(s_lineBuilder, "swap_frame_id", Engine.Rendering.Stats.FrameLifecycle.SwapFrameId, ref first);
            AppendNumberField(s_lineBuilder, "present_frame_id", Engine.Rendering.Stats.FrameLifecycle.PresentFrameId, ref first);
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
            AppendStringField(s_lineBuilder, "vr_view_render_mode_requested", Engine.Rendering.Stats.RendererState.ActiveVrViewRenderModeRequested, ref first);
            AppendStringField(s_lineBuilder, "vr_view_render_mode_effective", Engine.Rendering.Stats.RendererState.ActiveVrViewRenderModeEffective, ref first);
            AppendStringField(s_lineBuilder, "vr_view_render_implementation_path", Engine.Rendering.Stats.RendererState.ActiveVrViewRenderImplementationPath, ref first);
            AppendStringField(s_lineBuilder, "vr_temporal_history_policy", Engine.Rendering.Stats.RendererState.ActiveVrTemporalHistoryPolicy, ref first);
            AppendStringField(s_lineBuilder, "vr_mirror_mode", frameOutputs.MirrorMode.ToString(), ref first);
            AppendStringField(s_lineBuilder, "vr_visibility_policy", frameOutputs.VisibilityPolicy.ToString(), ref first);
            AppendBoolField(s_lineBuilder, "render_windows_while_in_vr", Engine.Rendering.Settings.RenderWindowsWhileInVR, ref first);
            AppendBoolField(s_lineBuilder, "vr_mirror_compose_from_eye_textures", Engine.Rendering.Settings.VrMirrorComposeFromEyeTextures, ref first);
            AppendNumberField(s_lineBuilder, "vr_desktop_editor_target_rate_hz", Engine.Rendering.Settings.VrDesktopEditorTargetRateHz, ref first);
            AppendNumberField(s_lineBuilder, "vr_cyclopean_desktop_target_rate_hz", Engine.Rendering.Settings.VrCyclopeanDesktopTargetRateHz, ref first);
            AppendBoolField(s_lineBuilder, "vr_desktop_auto_skip_when_over_budget", Engine.Rendering.Settings.VrDesktopAutoSkipWhenOverBudget, ref first);
            AppendStringField(s_lineBuilder, "active_render_backend", Engine.Rendering.Stats.RendererState.ActiveRenderBackend, ref first);
            AppendBoolField(s_lineBuilder, "validation_layers_enabled", Engine.Rendering.Stats.RendererState.ValidationLayersEnabled, ref first);
            AppendBoolField(s_lineBuilder, "debug_output_enabled", Engine.Rendering.Stats.RendererState.DebugOutputEnabled, ref first);
            AppendNumberField(s_lineBuilder, "deferred_debug_view", global::XREngine.Rendering.RenderDiagnosticsFlags.DeferredDebugView, ref first);
            AppendStringField(s_lineBuilder, "deferred_debug_env", metadata.DeferredDebugEnv, ref first);
            AppendBoolField(s_lineBuilder, "gpu_timestamps_dense_mode", Engine.Rendering.Stats.RendererState.GpuTimestampsDenseMode, ref first);

            AppendNumberField(s_lineBuilder, "render_dispatch_ms", renderMs, ref first);
            AppendNumberField(s_lineBuilder, "update_ms", updateMs, ref first);
            AppendNumberField(s_lineBuilder, "collect_visible_ms", collectVisibleMs, ref first);
            AppendNumberField(s_lineBuilder, "fixed_update_ms", fixedUpdateMs, ref first);
            AppendStringField(s_lineBuilder, "collect_visible_late_policy", Engine.Rendering.Stats.FrameLifecycle.CollectVisibleLatePolicy, ref first);
            AppendNumberField(s_lineBuilder, "collect_generation_requested", Engine.Rendering.Stats.FrameLifecycle.RequestedCollectGeneration, ref first);
            AppendNumberField(s_lineBuilder, "collect_generation_completed", Engine.Rendering.Stats.FrameLifecycle.CompletedCollectGeneration, ref first);
            AppendNumberField(s_lineBuilder, "collect_generation_published", Engine.Rendering.Stats.FrameLifecycle.PublishedCollectGeneration, ref first);
            AppendNumberField(s_lineBuilder, "collect_generation_consumed", Engine.Rendering.Stats.FrameLifecycle.ConsumedCollectGeneration, ref first);
            AppendNumberField(s_lineBuilder, "collect_generation_required", Engine.Rendering.Stats.FrameLifecycle.RequiredCollectGeneration, ref first);
            AppendNumberField(s_lineBuilder, "collect_wait_for_render_ms", Engine.Rendering.Stats.FrameLifecycle.CollectWaitForRenderMs, ref first);
            AppendStringField(s_lineBuilder, "collect_wait_reason", Engine.Rendering.Stats.FrameLifecycle.CollectWaitReason, ref first);
            AppendNumberField(s_lineBuilder, "render_wait_for_collect_ms", Engine.Rendering.Stats.FrameLifecycle.RenderWaitForCollectMs, ref first);
            AppendStringField(s_lineBuilder, "render_wait_reason", Engine.Rendering.Stats.FrameLifecycle.RenderWaitReason, ref first);
            AppendNumberField(s_lineBuilder, "skipped_collect_frames", Engine.Rendering.Stats.FrameLifecycle.SkippedCollectFrames, ref first);
            AppendNumberField(s_lineBuilder, "stale_collect_reuse_frames", Engine.Rendering.Stats.FrameLifecycle.StaleCollectReuseFrames, ref first);
            AppendNumberField(s_lineBuilder, "frame_output_frame_id", frameOutputs.FrameId, ref first);
            AppendStringField(s_lineBuilder, "frame_output_budget_band", frameOutputs.BudgetBand, ref first);
            AppendNumberField(s_lineBuilder, "frame_output_budget_ms", frameOutputs.BudgetMs, ref first);
            AppendNumberField(s_lineBuilder, "frame_output_whole_frame_ms", frameOutputs.WholeFrameMs, ref first);
            AppendNumberField(s_lineBuilder, "frame_output_whole_frame_p50_ms", frameOutputs.WholeFrameP50Ms, ref first);
            AppendNumberField(s_lineBuilder, "frame_output_whole_frame_p90_ms", frameOutputs.WholeFrameP90Ms, ref first);
            AppendNumberField(s_lineBuilder, "frame_output_whole_frame_p95_ms", frameOutputs.WholeFrameP95Ms, ref first);
            AppendNumberField(s_lineBuilder, "frame_output_whole_frame_p99_ms", frameOutputs.WholeFrameP99Ms, ref first);
            AppendNumberField(s_lineBuilder, "frame_output_whole_frame_worst_ms", frameOutputs.WholeFrameWorstMs, ref first);
            AppendNumberField(s_lineBuilder, "frame_output_workload_identity_hash", frameOutputs.WorkloadIdentityHash, ref first);
            AppendNumberField(s_lineBuilder, "frame_output_request_count", frameOutputs.Work.OutputRequestCount, ref first);
            AppendNumberField(s_lineBuilder, "frame_output_event_count", frameOutputs.Work.OutputEventCount, ref first);
            AppendNumberField(s_lineBuilder, "frame_output_collect_event_count", frameOutputs.Work.CollectEventCount, ref first);
            AppendNumberField(s_lineBuilder, "frame_output_swap_event_count", frameOutputs.Work.SwapEventCount, ref first);
            AppendNumberField(s_lineBuilder, "frame_output_render_event_count", frameOutputs.Work.RenderEventCount, ref first);
            AppendNumberField(s_lineBuilder, "frame_output_submit_event_count", frameOutputs.Work.SubmitEventCount, ref first);
            AppendNumberField(s_lineBuilder, "frame_output_overlay_event_count", frameOutputs.Work.OverlayEventCount, ref first);
            AppendNumberField(s_lineBuilder, "frame_output_present_event_count", frameOutputs.Work.PresentEventCount, ref first);
            AppendNumberField(s_lineBuilder, "frame_output_unique_view_family_count", frameOutputs.Work.UniqueViewFamilyCount, ref first);
            AppendNumberField(s_lineBuilder, "frame_output_target_variant_count", frameOutputs.Work.TargetVariantCount, ref first);
            AppendNumberField(s_lineBuilder, "frame_output_scene_snapshot_count", frameOutputs.Work.SceneSnapshotCount, ref first);
            AppendNumberField(s_lineBuilder, "frame_output_visibility_build_count", frameOutputs.Work.VisibilityBuildCount, ref first);
            AppendNumberField(s_lineBuilder, "frame_output_compiled_plan_cache_hits", frameOutputs.Work.CompiledPlanCacheHits, ref first);
            AppendNumberField(s_lineBuilder, "frame_output_compiled_plan_cache_misses", frameOutputs.Work.CompiledPlanCacheMisses, ref first);
            AppendNumberField(s_lineBuilder, "frame_output_physical_plan_cache_hits", frameOutputs.Work.PhysicalPlanCacheHits, ref first);
            AppendNumberField(s_lineBuilder, "frame_output_physical_plan_cache_misses", frameOutputs.Work.PhysicalPlanCacheMisses, ref first);
            AppendNumberField(s_lineBuilder, "frame_output_physical_plan_generations", frameOutputs.Work.PhysicalPlanGenerations, ref first);
            AppendNumberField(s_lineBuilder, "frame_output_physical_plan_alias_reuses", frameOutputs.Work.PhysicalPlanAliasReuses, ref first);
            AppendNumberField(s_lineBuilder, "frame_output_planner_arena_high_water", frameOutputs.Work.PlannerArenaHighWater, ref first);
            AppendNumberField(s_lineBuilder, "frame_output_render_graph_plan_generation", frameOutputs.Work.RenderGraphPlanGeneration, ref first);
            AppendNumberField(s_lineBuilder, "frame_output_shared_pass_reuse_count", frameOutputs.Work.SharedPassReuseCount, ref first);
            AppendNumberField(s_lineBuilder, "frame_output_recorded_work_item_count", frameOutputs.Work.RecordedWorkItemCount, ref first);
            AppendNumberField(s_lineBuilder, "frame_output_reused_work_item_count", frameOutputs.Work.ReusedWorkItemCount, ref first);
            AppendNumberField(s_lineBuilder, "frame_output_duplicated_work_item_count", frameOutputs.Work.DuplicatedWorkItemCount, ref first);
            AppendNumberField(s_lineBuilder, "frame_output_cpu_budget_deferral_count", frameOutputs.Work.CpuBudgetDeferralCount, ref first);
            AppendNumberField(s_lineBuilder, "frame_output_gpu_budget_deferral_count", frameOutputs.Work.GpuBudgetDeferralCount, ref first);
            AppendNumberField(s_lineBuilder, "frame_output_stale_result_reuse_count", frameOutputs.Work.StaleResultReuseCount, ref first);
            AppendNumberField(s_lineBuilder, "frame_output_missed_deadline_count", frameOutputs.Work.MissedDeadlineCount, ref first);
            AppendNumberField(s_lineBuilder, "frame_output_unapproved_policy_event_count", frameOutputs.Work.UnapprovedPolicyEventCount, ref first);
            AppendNumberField(s_lineBuilder, "frame_output_submission_rejection_count", frameOutputs.Work.SubmissionRejectionCount, ref first);
            AppendNumberField(s_lineBuilder, "frame_output_planner_prune_count", frameOutputs.Work.PlannerPruneCount, ref first);
            AppendNumberField(s_lineBuilder, "frame_output_planner_eviction_deferral_count", frameOutputs.Work.PlannerEvictionDeferralCount, ref first);
            AppendNumberField(s_lineBuilder, "frame_output_global_in_flight_wait_count", frameOutputs.Work.GlobalInFlightWaitCount, ref first);
            AppendNumberField(s_lineBuilder, "frame_output_force_flush_count", frameOutputs.Work.ForceFlushCount, ref first);
            AppendRawJsonField(s_lineBuilder, "frame_outputs", JsonSerializer.Serialize(CreateFrameOutputCaptureManifest(frameOutputs)), ref first);
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
            AppendStringField(s_lineBuilder, "occlusion_effective_mode", OcclusionTelemetry.LastEffectiveMode.ToString(), ref first);
            AppendStringField(s_lineBuilder, "occlusion_submission_strategy", OcclusionTelemetry.LastSubmissionStrategy.ToString(), ref first);
            AppendNumberField(s_lineBuilder, "cpu_query_passes_active", OcclusionTelemetry.CpuPassesActive, ref first);
            AppendNumberField(s_lineBuilder, "cpu_query_passes_skipped_no_camera", OcclusionTelemetry.CpuPassesSkippedNoCamera, ref first);
            AppendNumberField(s_lineBuilder, "cpu_query_passes_skipped_shadow", OcclusionTelemetry.CpuPassesSkippedShadow, ref first);
            AppendNumberField(s_lineBuilder, "cpu_query_passes_skipped_depth_normal_prepass", OcclusionTelemetry.CpuPassesSkippedDepthNormalPrePass, ref first);
            AppendNumberField(s_lineBuilder, "cpu_query_passes_skipped_mode_off", OcclusionTelemetry.CpuPassesSkippedModeOff, ref first);
            AppendNumberField(s_lineBuilder, "cpu_query_tested", OcclusionTelemetry.CpuTested, ref first);
            AppendNumberField(s_lineBuilder, "cpu_query_culled", OcclusionTelemetry.CpuCulled, ref first);
            AppendNumberField(s_lineBuilder, "cpu_query_rendered", OcclusionTelemetry.CpuRendered, ref first);
            AppendNumberField(s_lineBuilder, "cpu_query_decision_seed", OcclusionTelemetry.CpuDecisionSeed, ref first);
            AppendNumberField(s_lineBuilder, "cpu_query_decision_cached", OcclusionTelemetry.CpuDecisionCached, ref first);
            AppendNumberField(s_lineBuilder, "cpu_query_decision_visible_query", OcclusionTelemetry.CpuDecisionVisibleQuery, ref first);
            AppendNumberField(s_lineBuilder, "cpu_query_decision_visible_hysteresis", OcclusionTelemetry.CpuDecisionVisibleHysteresis, ref first);
            AppendNumberField(s_lineBuilder, "cpu_query_decision_probe", OcclusionTelemetry.CpuDecisionProbe, ref first);
            AppendNumberField(s_lineBuilder, "cpu_query_decision_skip", OcclusionTelemetry.CpuDecisionSkip, ref first);
            AppendNumberField(s_lineBuilder, "cpu_query_decision_forced_visible", OcclusionTelemetry.CpuDecisionForcedVisible, ref first);
            AppendStringField(s_lineBuilder, "cpu_query_motion_tier", OcclusionTelemetry.CpuMotionTier.ToString(), ref first);
            AppendStringField(s_lineBuilder, "cpu_query_active_view_scope", OcclusionTelemetry.CpuActiveViewScope.ToString(), ref first);
            AppendNumberField(s_lineBuilder, "cpu_query_global_conservative_frames", OcclusionTelemetry.CpuGlobalConservativeFrames, ref first);
            AppendNumberField(s_lineBuilder, "cpu_query_pending", OcclusionTelemetry.CpuPendingQueries, ref first);
            AppendNumberField(s_lineBuilder, "cpu_query_submitted_total", OcclusionTelemetry.CpuQuerySubmittedTotal, ref first);
            AppendNumberField(s_lineBuilder, "cpu_query_resolved_total", OcclusionTelemetry.CpuQueryResolvedTotal, ref first);
            AppendNumberField(s_lineBuilder, "cpu_query_latency_samples", OcclusionTelemetry.CpuQueryLatencySamples, ref first);
            AppendNumberField(s_lineBuilder, "cpu_query_latency_avg_frames", OcclusionTelemetry.CpuQueryLatencyAverageFrames, ref first);
            AppendNumberField(s_lineBuilder, "cpu_query_latency_max_frames", OcclusionTelemetry.CpuQueryLatencyMaxFrames, ref first);
            AppendNumberField(s_lineBuilder, "cpu_query_budget_skipped_total", OcclusionTelemetry.CpuBudgetSkippedTotal, ref first);
            AppendNumberField(s_lineBuilder, "cpu_query_forced_visible_total", OcclusionTelemetry.CpuForcedVisibleTotal, ref first);
            AppendNumberField(s_lineBuilder, "cpu_query_unsupported_stereo_mode", OcclusionTelemetry.CpuUnsupportedStereoQueryMode, ref first);
            AppendNumberField(s_lineBuilder, "cpu_query_async_submitted", OcclusionTelemetry.CpuQueryAsyncSubmitted, ref first);
            AppendNumberField(s_lineBuilder, "cpu_query_async_resolved", OcclusionTelemetry.CpuQueryAsyncResolved, ref first);
            AppendNumberField(s_lineBuilder, "cpu_query_async_occluded", OcclusionTelemetry.CpuQueryAsyncOccluded, ref first);
            AppendNumberField(s_lineBuilder, "cpu_soc_tested", OcclusionTelemetry.CpuSocTested, ref first);
            AppendNumberField(s_lineBuilder, "cpu_soc_culled", OcclusionTelemetry.CpuSocCulled, ref first);
            AppendNumberField(s_lineBuilder, "directional_cascade_stale_sampled", Engine.Rendering.Stats.RendererState.DirectionalCascadeStaleSampled, ref first);
            AppendNumberField(s_lineBuilder, "directional_cascade_mixed_generation_prevented", Engine.Rendering.Stats.RendererState.DirectionalCascadeMixedGenerationPrevented, ref first);
            AppendNumberField(s_lineBuilder, "directional_cascade_physical_reprojected", Engine.Rendering.Stats.RendererState.DirectionalCascadePhysicalReprojected, ref first);
            AppendNumberField(s_lineBuilder, "directional_cascade_forced_fresh_render", Engine.Rendering.Stats.RendererState.DirectionalCascadeForcedFreshRender, ref first);
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
            AppendNumberField(s_lineBuilder, "vulkan_frame_wait_fence_ms", Engine.Rendering.Stats.Vulkan.VulkanFrameWaitFenceMs, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_frame_sample_timing_queries_ms", Engine.Rendering.Stats.Vulkan.VulkanFrameSampleTimingQueriesMs, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_frame_drain_retired_resources_ms", Engine.Rendering.Stats.Vulkan.VulkanFrameDrainRetiredResourcesMs, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_frame_acquire_image_ms", Engine.Rendering.Stats.Vulkan.VulkanFrameAcquireImageMs, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_frame_acquire_bridge_submit_ms", Engine.Rendering.Stats.Vulkan.VulkanFrameAcquireBridgeSubmitMs, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_frame_wait_swapchain_image_ms", Engine.Rendering.Stats.Vulkan.VulkanFrameWaitSwapchainImageMs, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_frame_reset_dynamic_uniform_ring_ms", Engine.Rendering.Stats.Vulkan.VulkanFrameResetDynamicUniformRingMs, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_frame_record_command_buffer_ms", Engine.Rendering.Stats.Vulkan.VulkanFrameRecordCommandBufferMs, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_frame_snapshot_imgui_overlay_ms", Engine.Rendering.Stats.Vulkan.VulkanFrameSnapshotImGuiOverlayMs, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_frame_record_scene_command_buffer_ms", Engine.Rendering.Stats.Vulkan.VulkanFrameRecordSceneCommandBufferMs, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_frame_record_imgui_overlay_ms", Engine.Rendering.Stats.Vulkan.VulkanFrameRecordImGuiOverlayMs, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_frame_record_dynamic_ui_text_overlay_ms", Engine.Rendering.Stats.Vulkan.VulkanFrameRecordDynamicUiTextOverlayMs, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_frame_submit_ms", Engine.Rendering.Stats.Vulkan.VulkanFrameSubmitMs, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_frame_trim_ms", Engine.Rendering.Stats.Vulkan.VulkanFrameTrimMs, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_frame_present_ms", Engine.Rendering.Stats.Vulkan.VulkanFramePresentMs, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_frame_op_total_count", Engine.Rendering.Stats.Vulkan.VulkanFrameOpTotalCount, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_frame_op_clear_count", Engine.Rendering.Stats.Vulkan.VulkanFrameOpClearCount, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_frame_op_mesh_draw_count", Engine.Rendering.Stats.Vulkan.VulkanFrameOpMeshDrawCount, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_frame_op_indirect_draw_count", Engine.Rendering.Stats.Vulkan.VulkanFrameOpIndirectDrawCount, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_frame_op_mesh_task_dispatch_count", Engine.Rendering.Stats.Vulkan.VulkanFrameOpMeshTaskDispatchCount, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_frame_op_blit_count", Engine.Rendering.Stats.Vulkan.VulkanFrameOpBlitCount, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_frame_op_compute_count", Engine.Rendering.Stats.Vulkan.VulkanFrameOpComputeCount, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_frame_op_swapchain_write_count", Engine.Rendering.Stats.Vulkan.VulkanFrameOpSwapchainWriteCount, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_frame_op_fbo_write_count", Engine.Rendering.Stats.Vulkan.VulkanFrameOpFboWriteCount, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_frame_op_unique_pass_count", Engine.Rendering.Stats.Vulkan.VulkanFrameOpUniquePassCount, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_frame_op_unique_context_count", Engine.Rendering.Stats.Vulkan.VulkanFrameOpUniqueContextCount, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_frame_op_unique_target_count", Engine.Rendering.Stats.Vulkan.VulkanFrameOpUniqueTargetCount, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_command_buffer_clean_reuse_count", Engine.Rendering.Stats.Vulkan.VulkanCommandBufferCleanReuseCount, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_command_buffer_record_count", Engine.Rendering.Stats.Vulkan.VulkanCommandBufferRecordCount, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_command_buffer_forced_dirty_count", Engine.Rendering.Stats.Vulkan.VulkanCommandBufferForcedDirtyCount, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_command_buffer_frame_op_signature_dirty_count", Engine.Rendering.Stats.Vulkan.VulkanCommandBufferFrameOpSignatureDirtyCount, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_command_buffer_planner_dirty_count", Engine.Rendering.Stats.Vulkan.VulkanCommandBufferPlannerDirtyCount, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_command_buffer_profiler_dirty_count", Engine.Rendering.Stats.Vulkan.VulkanCommandBufferProfilerDirtyCount, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_command_buffer_decision_reason_mask", (int)Engine.Rendering.Stats.Vulkan.VulkanCommandBufferDecisionReasonMask, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_command_buffer_decision_visibility_generation", Engine.Rendering.Stats.Vulkan.VulkanCommandBufferDecisionVisibilityGeneration, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_command_buffer_decision_structural_signature", Engine.Rendering.Stats.Vulkan.VulkanCommandBufferDecisionStructuralSignature, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_command_buffer_decision_descriptor_generation", Engine.Rendering.Stats.Vulkan.VulkanCommandBufferDecisionDescriptorGeneration, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_command_buffer_decision_swapchain_slot", Engine.Rendering.Stats.Vulkan.VulkanCommandBufferDecisionSwapchainSlot, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_exact_variants_dirtied", Engine.Rendering.Stats.Vulkan.VulkanExactVariantsDirtied, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_exact_command_chains_dirtied", Engine.Rendering.Stats.Vulkan.VulkanExactCommandChainsDirtied, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_unrelated_variants_preserved", Engine.Rendering.Stats.Vulkan.VulkanUnrelatedVariantsPreserved, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_global_fallback_invalidations", Engine.Rendering.Stats.Vulkan.VulkanGlobalFallbackInvalidations, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_tracking_dependency_binds", Engine.Rendering.Stats.Vulkan.VulkanTrackingDependencyBinds, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_tracking_unique_dependencies", Engine.Rendering.Stats.Vulkan.VulkanTrackingUniqueDependencies, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_tracking_image_access_writes", Engine.Rendering.Stats.Vulkan.VulkanTrackingImageAccessWrites, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_tracking_compact_image_ranges", Engine.Rendering.Stats.Vulkan.VulkanTrackingCompactImageRanges, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_descriptor_expansion_cache_hits", Engine.Rendering.Stats.Vulkan.VulkanDescriptorExpansionCacheHits, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_descriptor_expansion_cache_misses", Engine.Rendering.Stats.Vulkan.VulkanDescriptorExpansionCacheMisses, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_lifetime_lock_contentions", Engine.Rendering.Stats.Vulkan.VulkanLifetimeLockContentions, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_descriptor_pool_create_count", Engine.Rendering.Stats.Vulkan.VulkanDescriptorPoolCreateCount, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_lifetime_live_resource_count", Engine.Rendering.Stats.Vulkan.VulkanLifetimeLiveResourceCount, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_tracked_descriptor_set_count", Engine.Rendering.Stats.Vulkan.VulkanTrackedDescriptorSetCount, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_lifetime_pending_retirement_count", Engine.Rendering.Stats.Vulkan.VulkanLifetimePendingRetirementCount, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_lifetime_oldest_pending_retirement_age_ms", Engine.Rendering.Stats.Vulkan.VulkanLifetimeOldestPendingRetirementAgeMilliseconds, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_mesh_frame_data_arena_chunks", Engine.Rendering.Stats.Vulkan.VulkanMeshFrameDataArenaChunkCount, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_mesh_frame_data_mapped_bytes", Engine.Rendering.Stats.Vulkan.VulkanMeshFrameDataMappedBytes, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_mesh_frame_data_reserved_bytes", Engine.Rendering.Stats.Vulkan.VulkanMeshFrameDataReservedBytes, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_mesh_frame_data_reservations", Engine.Rendering.Stats.Vulkan.VulkanMeshFrameDataReservationCount, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_mesh_frame_data_generation", Engine.Rendering.Stats.Vulkan.VulkanMeshFrameDataGeneration, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_mesh_frame_data_recording_leases", Engine.Rendering.Stats.Vulkan.VulkanMeshFrameDataRecordingLeases, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_mesh_frame_data_cached_leases", Engine.Rendering.Stats.Vulkan.VulkanMeshFrameDataCachedLeases, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_mesh_frame_data_submitted_leases", Engine.Rendering.Stats.Vulkan.VulkanMeshFrameDataSubmittedLeases, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_mesh_frame_data_active_generations", Engine.Rendering.Stats.Vulkan.VulkanMeshFrameDataActiveGenerationCount, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_mesh_frame_data_lease_retained_generations", Engine.Rendering.Stats.Vulkan.VulkanMeshFrameDataLeaseRetainedGenerationCount, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_mesh_descriptor_allocation_variants", Engine.Rendering.Stats.Vulkan.VulkanMeshDescriptorAllocationVariants, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_mesh_descriptor_pools", Engine.Rendering.Stats.Vulkan.VulkanMeshDescriptorPools, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_mesh_descriptor_allocated_sets", Engine.Rendering.Stats.Vulkan.VulkanMeshDescriptorAllocatedSets, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_mesh_descriptor_reserved_sets", Engine.Rendering.Stats.Vulkan.VulkanMeshDescriptorReservedSets, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_mesh_frame_data_arena_chunk_high_water", Engine.Rendering.Stats.Vulkan.VulkanMeshFrameDataArenaChunkHighWater, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_mesh_frame_data_mapped_bytes_high_water", Engine.Rendering.Stats.Vulkan.VulkanMeshFrameDataMappedBytesHighWater, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_mesh_frame_data_reserved_bytes_high_water", Engine.Rendering.Stats.Vulkan.VulkanMeshFrameDataReservedBytesHighWater, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_mesh_frame_data_reservation_high_water", Engine.Rendering.Stats.Vulkan.VulkanMeshFrameDataReservationHighWater, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_mesh_frame_data_lease_high_water", Engine.Rendering.Stats.Vulkan.VulkanMeshFrameDataLeaseHighWater, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_mesh_descriptor_allocation_variant_high_water", Engine.Rendering.Stats.Vulkan.VulkanMeshDescriptorAllocationVariantHighWater, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_mesh_descriptor_pool_high_water", Engine.Rendering.Stats.Vulkan.VulkanMeshDescriptorPoolHighWater, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_mesh_descriptor_set_high_water", Engine.Rendering.Stats.Vulkan.VulkanMeshDescriptorSetHighWater, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_layout_lock_contentions", Engine.Rendering.Stats.Vulkan.VulkanLayoutLockContentions, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_record_command_buffer_allocated_bytes", Engine.Rendering.Stats.Vulkan.VulkanRecordCommandBufferAllocatedBytes, ref first);
            AppendVulkanCpuStageFields(s_lineBuilder, "frame_op_preparation", EVulkanCpuStage.FrameOpPreparation, ref first);
            AppendVulkanCpuStageFields(s_lineBuilder, "resource_planning", EVulkanCpuStage.ResourcePlanning, ref first);
            AppendVulkanCpuStageFields(s_lineBuilder, "frame_data_refresh", EVulkanCpuStage.FrameDataRefresh, ref first);
            AppendVulkanCpuStageFields(s_lineBuilder, "packet_construction", EVulkanCpuStage.PacketConstruction, ref first);
            AppendVulkanCpuStageFields(s_lineBuilder, "primary_recording", EVulkanCpuStage.PrimaryRecording, ref first);
            AppendVulkanCpuStageFields(s_lineBuilder, "secondary_recording", EVulkanCpuStage.SecondaryRecording, ref first);
            AppendVulkanCpuStageFields(s_lineBuilder, "descriptor_publication", EVulkanCpuStage.DescriptorPublication, ref first);
            AppendVulkanCpuStageFields(s_lineBuilder, "submission", EVulkanCpuStage.Submission, ref first);
            AppendStringField(s_lineBuilder, "vulkan_command_buffer_dirty_summary", Engine.Rendering.Stats.Vulkan.VulkanCommandBufferDirtySummary, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_command_chains_scheduled", Engine.Rendering.Stats.Vulkan.VulkanCommandChainsScheduled, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_command_chains_recorded", Engine.Rendering.Stats.Vulkan.VulkanCommandChainsRecorded, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_command_chains_reused", Engine.Rendering.Stats.Vulkan.VulkanCommandChainsReused, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_command_chains_frame_data_refreshed", Engine.Rendering.Stats.Vulkan.VulkanCommandChainsFrameDataRefreshed, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_volatile_command_chains_recorded", Engine.Rendering.Stats.Vulkan.VulkanVolatileCommandChainsRecorded, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_primary_command_buffers_reused", Engine.Rendering.Stats.Vulkan.VulkanPrimaryCommandBuffersReused, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_primary_command_buffers_recorded", Engine.Rendering.Stats.Vulkan.VulkanPrimaryCommandBuffersRecorded, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_visibility_packet_count", Engine.Rendering.Stats.Vulkan.VulkanVisibilityPacketCount, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_render_packet_count", Engine.Rendering.Stats.Vulkan.VulkanRenderPacketCount, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_secondary_command_buffer_count", Engine.Rendering.Stats.Vulkan.VulkanSecondaryCommandBufferCount, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_indirect_parallel_secondary_record_ops", Engine.Rendering.Stats.Vulkan.VulkanIndirectParallelSecondaryRecordOps, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_command_chain_worker_record_ms", Engine.Rendering.Stats.Vulkan.VulkanCommandChainWorkerRecordMs, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_render_thread_wait_for_chain_workers_ms", Engine.Rendering.Stats.Vulkan.VulkanRenderThreadWaitForChainWorkersMs, ref first);
            AppendStringField(s_lineBuilder, "vulkan_first_command_chain_structural_dirty_reason", Engine.Rendering.Stats.Vulkan.VulkanFirstCommandChainStructuralDirtyReason, ref first);
            AppendStringField(s_lineBuilder, "vulkan_first_command_chain_descriptor_generation_mismatch", Engine.Rendering.Stats.Vulkan.VulkanFirstCommandChainDescriptorGenerationMismatch, ref first);
            AppendStringField(s_lineBuilder, "vulkan_first_command_chain_resource_plan_revision_mismatch", Engine.Rendering.Stats.Vulkan.VulkanFirstCommandChainResourcePlanRevisionMismatch, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_pipeline_cache_lookup_hits", Engine.Rendering.Stats.Vulkan.VulkanPipelineCacheLookupHits, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_pipeline_cache_lookup_misses", Engine.Rendering.Stats.Vulkan.VulkanPipelineCacheLookupMisses, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_driver_pipeline_cache_persisted_hits", Engine.Rendering.Stats.Vulkan.VulkanDriverPipelineCachePersistedHits, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_driver_pipeline_cache_runtime_hits", Engine.Rendering.Stats.Vulkan.VulkanDriverPipelineCacheRuntimeHits, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_driver_pipeline_cache_misses", Engine.Rendering.Stats.Vulkan.VulkanDriverPipelineCacheMisses, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_driver_pipeline_cache_unknown", Engine.Rendering.Stats.Vulkan.VulkanDriverPipelineCacheUnknown, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_pipeline_compile_required_count", Engine.Rendering.Stats.Vulkan.VulkanPipelineCompileRequiredCount, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_pipeline_compile_completed_count", Engine.Rendering.Stats.Vulkan.VulkanPipelineCompileCompletedCount, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_pipeline_background_compile_completed_count", Engine.Rendering.Stats.Vulkan.VulkanPipelineBackgroundCompileCompletedCount, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_required_pipeline_pending_count", Engine.Rendering.Stats.Vulkan.VulkanRequiredPipelinePendingCount, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_pipeline_record_deferred_count", Engine.Rendering.Stats.Vulkan.VulkanPipelineRecordDeferredCount, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_render_thread_shader_compile_count", Engine.Rendering.Stats.Vulkan.VulkanRenderThreadShaderCompileCount, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_pipeline_compile_total_ms", Engine.Rendering.Stats.Vulkan.VulkanPipelineCompileTotalMs, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_pipeline_compile_max_ms", Engine.Rendering.Stats.Vulkan.VulkanPipelineCompileMaxMs, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_pipeline_async_queued_count", Engine.Rendering.Stats.Vulkan.VulkanPipelineAsyncQueuedCount, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_pipeline_queue_rejected_count", Engine.Rendering.Stats.Vulkan.VulkanPipelineQueueRejectedCount, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_pipeline_draw_not_ready_count", Engine.Rendering.Stats.Vulkan.VulkanPipelineDrawNotReadyCount, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_pipeline_queue_depth_high_water", Engine.Rendering.Stats.Vulkan.VulkanPipelineQueueDepthHighWater, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_pipeline_queue_capacity", Engine.Rendering.Stats.Vulkan.VulkanPipelineQueueCapacity, ref first);
            AppendStringField(s_lineBuilder, "vulkan_pipeline_cache_miss_summary", Engine.Rendering.Stats.Vulkan.VulkanPipelineCacheMissSummary, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_present_attempt_count", Engine.Rendering.Stats.Vulkan.VulkanPresentAttemptCount, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_present_accepted_count", Engine.Rendering.Stats.Vulkan.VulkanPresentAcceptedCount, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_last_present_result", Engine.Rendering.Stats.Vulkan.VulkanLastPresentResult, ref first);
            AppendBoolField(s_lineBuilder, "vulkan_validation_layers_enabled", Engine.Rendering.Stats.Vulkan.VulkanValidationLayersEnabled, ref first);
            AppendBoolField(s_lineBuilder, "vulkan_synchronization_validation_enabled", Engine.Rendering.Stats.Vulkan.VulkanSynchronizationValidationEnabled, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_validation_message_count", Engine.Rendering.Stats.Vulkan.VulkanValidationMessageCount, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_validation_error_count", Engine.Rendering.Stats.Vulkan.VulkanValidationErrorCount, ref first);
            AppendStringField(s_lineBuilder, "vulkan_last_validation_message", Engine.Rendering.Stats.Vulkan.VulkanLastValidationMessage, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_retired_resource_plan_replacements", Engine.Rendering.Stats.Vulkan.VulkanRetiredResourcePlanReplacements, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_retired_resource_plan_images", Engine.Rendering.Stats.Vulkan.VulkanRetiredResourcePlanImages, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_retired_resource_plan_buffers", Engine.Rendering.Stats.Vulkan.VulkanRetiredResourcePlanBuffers, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_swapchain_retirement_queued_count", Engine.Rendering.Stats.Vulkan.VulkanSwapchainRetirementQueuedCount, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_swapchain_retirement_drained_count", Engine.Rendering.Stats.Vulkan.VulkanSwapchainRetirementDrainedCount, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_swapchain_retirement_pending_count", Engine.Rendering.Stats.Vulkan.VulkanSwapchainRetirementPendingCount, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_swapchain_retirement_pending_high_water", Engine.Rendering.Stats.Vulkan.VulkanSwapchainRetirementPendingHighWater, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_swapchain_retirement_deferred_count", Engine.Rendering.Stats.Vulkan.VulkanSwapchainRetirementDeferredCount, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_retired_descriptor_pool_count", Engine.Rendering.Stats.Vulkan.VulkanRetiredDescriptorPoolCount, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_retired_descriptor_set_count", Engine.Rendering.Stats.Vulkan.VulkanRetiredDescriptorSetCount, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_retired_command_buffer_count", Engine.Rendering.Stats.Vulkan.VulkanRetiredCommandBufferCount, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_retired_query_pool_count", Engine.Rendering.Stats.Vulkan.VulkanRetiredQueryPoolCount, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_retired_buffer_view_count", Engine.Rendering.Stats.Vulkan.VulkanRetiredBufferViewCount, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_retired_pipeline_count", Engine.Rendering.Stats.Vulkan.VulkanRetiredPipelineCount, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_retired_framebuffer_count", Engine.Rendering.Stats.Vulkan.VulkanRetiredFramebufferCount, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_retired_buffer_count", Engine.Rendering.Stats.Vulkan.VulkanRetiredBufferCount, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_retired_buffer_memory_count", Engine.Rendering.Stats.Vulkan.VulkanRetiredBufferMemoryCount, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_retired_image_count", Engine.Rendering.Stats.Vulkan.VulkanRetiredImageCount, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_retired_image_view_count", Engine.Rendering.Stats.Vulkan.VulkanRetiredImageViewCount, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_retired_sampler_count", Engine.Rendering.Stats.Vulkan.VulkanRetiredSamplerCount, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_retired_image_memory_count", Engine.Rendering.Stats.Vulkan.VulkanRetiredImageMemoryCount, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_retired_image_bytes", Engine.Rendering.Stats.Vulkan.VulkanRetiredImageBytes, ref first);

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

        private static object CreateFrameOutputCaptureManifest(Engine.Rendering.Stats.FrameOutputManifestSnapshot snapshot)
        {
            Engine.Rendering.Stats.FrameOutputEntrySnapshot[] outputs = snapshot.Outputs ?? [];
            object[] rows = new object[outputs.Length];
            for (int i = 0; i < outputs.Length; i++)
            {
                Engine.Rendering.Stats.FrameOutputEntrySnapshot output = outputs[i];
                rows[i] = new
                {
                    frame_id = output.FrameId,
                    output_kind = output.OutputKind.ToString(),
                    view_kind = output.ViewKind.ToString(),
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
                    deadline_ms = output.Request.Schedule.DeadlineMs,
                    max_cpu_budget_ms = output.Request.Schedule.MaxCpuBudgetMs,
                    max_gpu_budget_ms = output.Request.Schedule.MaxGpuBudgetMs,
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
                    pipeline_name = output.PipelineName,
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
                    skip_reason = output.SkipReason.ToString(),
                    configured_target_rate_hz = output.ConfiguredTargetRateHz,
                    source_rate_hz = output.SourceRateHz,
                    achieved_rate_hz = output.AchievedRateHz,
                    total_render_count = output.TotalRenderCount,
                    total_skip_count = output.TotalSkipCount,
                    command_count = output.CommandCount,
                    draw_calls = output.DrawCalls,
                    multi_draw_calls = output.MultiDrawCalls,
                    triangles = output.Triangles,
                    collect_cpu_ms = output.CollectCpuMs,
                    swap_cpu_ms = output.SwapCpuMs,
                    render_cpu_ms = output.RenderCpuMs,
                    submit_cpu_ms = output.SubmitCpuMs,
                    overlay_cpu_ms = output.OverlayCpuMs,
                    present_cpu_ms = output.PresentCpuMs,
                    gpu_ms = output.GpuMs,
                };
            }

            return new
            {
                frame_id = snapshot.FrameId,
                vr_active = snapshot.VrActive,
                mirror_mode = snapshot.MirrorMode.ToString(),
                visibility_policy = snapshot.VisibilityPolicy.ToString(),
                budget_band = snapshot.BudgetBand,
                budget_ms = snapshot.BudgetMs,
                whole_frame_ms = snapshot.WholeFrameMs,
                whole_frame_p50_ms = snapshot.WholeFrameP50Ms,
                whole_frame_p90_ms = snapshot.WholeFrameP90Ms,
                whole_frame_p95_ms = snapshot.WholeFrameP95Ms,
                whole_frame_p99_ms = snapshot.WholeFrameP99Ms,
                whole_frame_worst_ms = snapshot.WholeFrameWorstMs,
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
                outputs = rows,
            };
        }

        private static void AppendVulkanCpuStageFields(
            StringBuilder builder,
            string name,
            EVulkanCpuStage stage,
            ref bool first)
        {
            AppendNumberField(builder, $"vulkan_cpu_{name}_ms", Engine.Rendering.Stats.Vulkan.VulkanCpuStageMs(stage), ref first);
            AppendNumberField(builder, $"vulkan_cpu_{name}_allocated_bytes", Engine.Rendering.Stats.Vulkan.VulkanCpuStageAllocatedBytes(stage), ref first);
            AppendNumberField(builder, $"vulkan_cpu_{name}_allocation_high_water_bytes", Engine.Rendering.Stats.Vulkan.VulkanCpuStageAllocationHighWaterBytes(stage), ref first);
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

            ValidateEnvFlag(errors, XREngineEnvironmentVariables.ProfilerEnabled);
            ValidateEnvFlag(errors, XREngineEnvironmentVariables.ProfileCapture);
            ValidateEnvFlag(errors, XREngineEnvironmentVariables.ProfileAutoDump);
            ValidateEnvFlag(errors, XREngineEnvironmentVariables.P3Logging);
            ValidateEnvFlag(errors, XREngineEnvironmentVariables.BucketLoopDryRun);
            ValidateEnvFlag(errors, XREngineEnvironmentVariables.SkipCommandSwapIfClean);
            ValidateEnvFlag(errors, XREngineEnvironmentVariables.BucketLoopSkipEmpty);
            ValidateEnvFlag(errors, XREngineEnvironmentVariables.ForceSingleBucket);
            ValidateEnvFlag(errors, XREngineEnvironmentVariables.HizCullTrace);
            ValidateEnvFlag(errors, XREngineEnvironmentVariables.GpuTimestampDense);
            ValidateEnvFlag(errors, XREngineEnvironmentVariables.ForceCpuIndirectBuild);
            ValidateEnvEnum(errors, XREngineEnvironmentVariables.CollectVisibleLatePolicy, "BlockUntilFresh", "ReusePreviousVisibility", "block", "fresh", "reuse", "stale");

            ValidateEnvEnum(
                errors,
                XREngineEnvironmentVariables.ForceMeshSubmissionStrategy,
                "CpuDirect",
                "GpuIndirectInstrumented",
                "GpuIndirectZeroReadback",
                "GpuMeshletInstrumented",
                "GpuMeshletZeroReadback");
            ValidateEnvEnum(
                errors,
                XREngineEnvironmentVariables.ZeroReadbackMaterialDrawPath,
                "FullBucketScan",
                "ActiveBucketList",
                "MaterialTable",
                "BindlessMaterialTable");
            ValidateEnvEnum(errors, XREngineEnvironmentVariables.ProfileCacheMode, "Cold", "Warm");
            ValidateEnvEnum(errors, XREngineEnvironmentVariables.ShaderCacheMode, "Cold", "Warm");
            ValidateEnvEnum(errors, XREngineEnvironmentVariables.TextureCacheMode, "Cold", "Warm");

            ValidateEnvPositiveDouble(errors, XREngineEnvironmentVariables.TargetRefreshHz);
            ValidateEnvPositiveDouble(errors, XREngineEnvironmentVariables.UpdateFps);
            ValidateEnvPositiveDouble(errors, XREngineEnvironmentVariables.ProfileRenderScale);
            ValidateEnvPositiveDouble(errors, XREngineEnvironmentVariables.ProfileWarmupSeconds);
            ValidateEnvPositiveDouble(errors, XREngineEnvironmentVariables.ProfileCaptureSeconds);

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

        private static string CaptureSceneIdentity()
        {
            try
            {
                string[] worldNames = Engine.WorldInstances
                    .Select(static world => world.TargetWorldName ?? "<unnamed>")
                    .OrderBy(static name => name, StringComparer.Ordinal)
                    .ToArray();
                string configuredScene = Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.ProfileScene) ?? string.Empty;
                return string.Join("|", worldNames) + "|profile=" + configuredScene;
            }
            catch
            {
                return Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.ProfileScene) ?? string.Empty;
            }
        }

        private static string BuildSettingsIdentity(string renderTargetModeEnv, string renderTargetModeSetting)
            => string.Join(
                "|",
                "backend=" + CaptureString(() => Engine.Rendering.Stats.RendererState.ActiveRenderBackend),
                "renderTargetEnv=" + renderTargetModeEnv,
                "renderTargetSetting=" + renderTargetModeSetting,
                "renderScale=" + CaptureString(() => Engine.Rendering.Settings.TsrRenderScale.ToString(CultureInfo.InvariantCulture)),
                "strategy=" + CaptureString(() => Engine.Rendering.ResolveMeshSubmissionStrategy().ToString()),
                "vrMode=" + CaptureString(() => Engine.Rendering.Settings.VrViewRenderMode.ToString()),
                "foveation=" + CaptureString(() => Engine.Rendering.Settings.VrFoveationMode.ToString()),
                "mirror=" + CaptureString(() => Engine.Rendering.Settings.VrMirrorMode.ToString()),
                "renderWindowsInVr=" + CaptureString(() => Engine.Rendering.Settings.RenderWindowsWhileInVR ? "1" : "0"),
                "primaryReuse=" + ((ResolveOptionalBooleanOverride(Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.VulkanPrimaryCommandBufferReuse)) ??
                    CaptureBoolean(() => Engine.Rendering.Settings.EnableVulkanPrimaryCommandBufferReuse)) ? "1" : "0"),
                "skipImGui=" + (IsEnvFlagEnabled(XREngineEnvironmentVariables.VkSkipImGui) ? "1" : "0"));

        private static string ComputeStableIdentityHash(string value)
        {
            ulong hash = 1469598103934665603UL;
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                hash ^= (byte)c;
                hash *= 1099511628211UL;
                hash ^= (byte)(c >> 8);
                hash *= 1099511628211UL;
            }
            return $"0x{hash:X16}";
        }

        private static bool CaptureBoolean(Func<bool> capture)
        {
            try
            {
                return capture();
            }
            catch
            {
                return false;
            }
        }

        private static bool? ResolveOptionalBooleanOverride(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;
            if (value is "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("yes", StringComparison.OrdinalIgnoreCase) || value.Equals("on", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            if (value is "0" || value.Equals("false", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("no", StringComparison.OrdinalIgnoreCase) || value.Equals("off", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return null;
        }

        private static FrameOutputInventoryMetadata[] CaptureOutputInventory(
            Engine.Rendering.Stats.FrameOutputManifestSnapshot snapshot)
        {
            Engine.Rendering.Stats.FrameOutputEntrySnapshot[] outputs = snapshot.Outputs ?? [];
            FrameOutputInventoryMetadata[] inventory = new FrameOutputInventoryMetadata[outputs.Length];
            for (int i = 0; i < outputs.Length; i++)
            {
                Engine.Rendering.Stats.FrameOutputEntrySnapshot output = outputs[i];
                inventory[i] = new(
                    output.Request.OutputId,
                    output.Request.ViewFamilyId,
                    output.OutputKindName,
                    output.ViewKindName,
                    output.Request.OutputClass.ToString(),
                    output.Request.Schedule.Priority.ToString(),
                    output.Request.Target.TargetClass.ToString(),
                    output.Request.Target.StableTargetId,
                    output.Request.Target.TargetGeneration,
                    output.Request.Target.DisplayWidth,
                    output.Request.Target.DisplayHeight,
                    output.Request.Target.InternalWidth,
                    output.Request.Target.InternalHeight,
                    output.Request.Target.FormatCompatibilityKey,
                    output.Request.Target.SampleCount,
                    output.Request.Target.ViewMask,
                    output.Request.Target.ExternalImageSlot,
                    output.Request.Schedule.DesiredRateHz,
                    output.Request.Schedule.DeadlineMs,
                    output.Request.Schedule.MaxCpuBudgetMs,
                    output.Request.Schedule.MaxGpuBudgetMs,
                    output.Request.Schedule.MaxContentAgeFrames,
                    output.Request.Schedule.HardDeadline,
                    output.Request.QualityRequirements.ToString(),
                    output.Request.FallbackPolicy.ToString(),
                    output.Request.CompletionRequirement.ToString(),
                    output.Request.ProducerDependencySetId,
                    output.Request.ConsumerDependencySetId);
            }
            return inventory;
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
            string SceneIdentity,
            string SceneIdentityHash,
            string SettingsIdentityHash,
            string SceneSettingsHash,
            ulong FrameOutputWorkloadIdentityHash,
            FrameOutputInventoryMetadata[] OutputInventory,
            string StereoMode,
            string VrViewRenderModeRequested,
            string VrViewRenderModeEffective,
            string VrViewRenderImplementationPath,
            string VrTemporalHistoryPolicy,
            string VrMirrorMode,
            string RenderWindowsWhileInVR,
            string VrMirrorComposeFromEyeTextures,
            string VrDesktopEditorTargetRateHz,
            string VrCyclopeanDesktopTargetRateHz,
            string VrDesktopAutoSkipWhenOverBudget,
            string VulkanRenderTargetModeEnvironment,
            string VulkanRenderTargetModeSetting,
            string VulkanPrimaryCommandBufferReusePolicy,
            bool VulkanPrimaryCommandBufferReuseEnabled,
            string VulkanObsHookPolicy,
            bool VulkanSkipImGui,
            string ValidationLayersEnabled,
            string DebugOutputEnabled,
            string DeferredDebugView,
            string DeferredDebugEnv,
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
            string GameBuildConfiguration,
            DateTimeOffset CreatedUtc,
            int ProcessId);

        private sealed record FrameOutputInventoryMetadata(
            ulong OutputId,
            ulong ViewFamilyId,
            string OutputKind,
            string ViewKind,
            string OutputClass,
            string Priority,
            string TargetClass,
            ulong StableTargetId,
            ulong TargetGeneration,
            uint DisplayWidth,
            uint DisplayHeight,
            uint InternalWidth,
            uint InternalHeight,
            ulong FormatCompatibilityKey,
            uint SampleCount,
            uint ViewMask,
            int ExternalImageSlot,
            float DesiredRateHz,
            double DeadlineMs,
            double MaxCpuBudgetMs,
            double MaxGpuBudgetMs,
            uint MaxContentAgeFrames,
            bool HardDeadline,
            string QualityRequirements,
            string FallbackPolicy,
            string CompletionRequirement,
            ulong ProducerDependencySetId,
            ulong ConsumerDependencySetId);

        private sealed record CaptureCompletion(
            RunMetadata Metadata,
            int SampleCount,
            string OutputDirectory,
            bool CaptureEnabled,
            bool AutoDumpGpuTimings);
    }
#endif
}
