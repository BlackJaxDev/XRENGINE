using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using XREngine.Data.Profiling;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Vulkan;
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
        private const int ProfileCaptureSchemaVersion = 7;
        private const int RuntimeCaptureRetentionCount = 3;
        private const int FlushIntervalMilliseconds = 1000;
        private const int MaxBufferedCharacters = 256 * 1024;
        private const double MaxRuntimeCaptureSeconds = 600.0;

        private static bool s_envCaptureEnabled
            => IsEnvFlagEnabled(XREngineEnvironmentVariables.ProfileCapture);
        private static bool s_envAutoDumpGpuTimings
            => s_envCaptureEnabled ||
               IsEnvFlagEnabled(XREngineEnvironmentVariables.ProfileAutoDump);
        private static readonly object s_lock = new();
        private static readonly StringBuilder s_sampleBuffer = new(MaxBufferedCharacters);
        private static readonly StringBuilder s_lineBuilder = new(4096);
        private static readonly JsonSerializerOptions s_jsonOptions = new() { WriteIndented = true };
        private static readonly string[] s_diagnosticTraceEnvironmentVariables =
        [
            XREngineEnvironmentVariables.VulkanFrameDataReuseDiag,
            XREngineEnvironmentVariables.VulkanAutoUniformParity,
            XREngineEnvironmentVariables.VulkanDescriptorFingerprintDiag,
            XREngineEnvironmentVariables.VulkanMaterialBindingDiag,
            XREngineEnvironmentVariables.VulkanRecordingDiag,
            XREngineEnvironmentVariables.VulkanRecordingProfileDetail,
            XREngineEnvironmentVariables.VulkanFrameOpTrace,
            XREngineEnvironmentVariables.VulkanTargetTrace,
            XREngineEnvironmentVariables.VulkanIndirectTrace,
            XREngineEnvironmentVariables.VulkanCounterDiagnostics,
            XREngineEnvironmentVariables.VulkanDescriptorTrace,
        ];

        private static volatile bool s_runtimeCaptureEnabled;
        private static long s_runtimeCaptureEndTicks;
        private static string s_runtimeRunLabel = string.Empty;
        private static string? s_outputDirectory;
        private static string s_lastRuntimeSummaryPath = string.Empty;
        private static long s_startTicks;
        private static long s_lastFlushTicks;
        private static int s_sampleCount;
        private static int s_snapshotCount;
        private static int s_sampleIntervalFrames;
        private static bool s_manifestWritten;
        private static bool s_shutdown;
        private static RunMetadata? s_metadata;
        private static IRuntimeDebugHostServices? s_uncappedDebugHostServices;

        internal static EOutputVerbosity? OutputVerbosityOverride { get; private set; }
        internal static bool? GpuIndirectDebugLoggingOverride { get; private set; }
        internal static bool? GpuIndirectValidationLoggingOverride { get; private set; }
        internal static EVulkanGpuDrivenProfile? VulkanGpuDrivenProfileOverride { get; private set; }

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

        /// <summary>
        /// Applies the non-intrusive observer policy before renderer creation.
        /// The overrides are process-local and only activate for an explicitly
        /// selected clean or release benchmark profile.
        /// </summary>
        public static void ApplyPerformanceProfileContract()
        {
            string profileMode = ResolvePerformanceProfileMode();
            VulkanGpuDrivenProfileOverride = Enum.TryParse(
                Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.ProfileVulkanGpuDrivenProfile),
                ignoreCase: true,
                out EVulkanGpuDrivenProfile requestedGpuDrivenProfile)
                    ? requestedGpuDrivenProfile
                    : null;
            if (!IsCleanPerformanceProfile(profileMode))
            {
                OutputVerbosityOverride = null;
                GpuIndirectDebugLoggingOverride = null;
                GpuIndirectValidationLoggingOverride = null;
                RestoreDebugHostServices();
                return;
            }

            SetEnvironmentFlag(XREngineEnvironmentVariables.VkSkipImGui, enabled: true);
            SetEnvironmentFlag(XREngineEnvironmentVariables.P3Logging, enabled: false);
            SetEnvironmentFlag(XREngineEnvironmentVariables.GpuTimestampDense, enabled: false);
            SetEnvironmentFlag(XREngineEnvironmentVariables.VulkanValidation, enabled: false);
            SetEnvironmentFlag(XREngineEnvironmentVariables.VulkanSynchronizationValidation, enabled: false);
            SetEnvironmentFlag(XREngineEnvironmentVariables.VulkanGpuAssistedValidation, enabled: false);
            SetEnvironmentFlag(XREngineEnvironmentVariables.VulkanBestPracticesValidation, enabled: false);
            SetEnvironmentFlag(XREngineEnvironmentVariables.VulkanCommandBufferLabels, enabled: false);
            SetEnvironmentFlag(XREngineEnvironmentVariables.VulkanCrashBreadcrumbs, enabled: false);
            SetEnvironmentFlag(XREngineEnvironmentVariables.VulkanDeviceFault, enabled: false);
            SetEnvironmentFlag(XREngineEnvironmentVariables.VulkanDeviceAddressBindingReport, enabled: false);
            SetEnvironmentFlag(XREngineEnvironmentVariables.VulkanNvDiagnosticCheckpoints, enabled: false);
            SetEnvironmentFlag(XREngineEnvironmentVariables.VulkanNvDiagnosticsConfig, enabled: false);
            SetEnvironmentFlag(XREngineEnvironmentVariables.VulkanRenderDocFriendly, enabled: false);
            for (int i = 0; i < s_diagnosticTraceEnvironmentVariables.Length; i++)
            {
                SetEnvironmentFlag(
                    s_diagnosticTraceEnvironmentVariables[i],
                    enabled: false);
            }
            Environment.SetEnvironmentVariable(
                XREngineEnvironmentVariables.VulkanDiagnosticPreset,
                "Off");
            Environment.SetEnvironmentVariable(
                XREngineEnvironmentVariables.VulkanDiagnosticFlags,
                "None");

            RenderDiagnosticsFlags.SetVkSkipImGui(true);
            OutputVerbosityOverride = EOutputVerbosity.Normal;
            GpuIndirectDebugLoggingOverride = false;
            GpuIndirectValidationLoggingOverride = false;
            if (s_uncappedDebugHostServices is null)
            {
                s_uncappedDebugHostServices = RuntimeDebugHostServices.Current;
                RuntimeDebugHostServices.Current =
                    new PerformanceProfileDebugHostServices(
                        s_uncappedDebugHostServices,
                        EOutputVerbosity.Normal);
            }
        }

        private static void RestoreDebugHostServices()
        {
            if (s_uncappedDebugHostServices is null)
                return;

            RuntimeDebugHostServices.Current = s_uncappedDebugHostServices;
            s_uncappedDebugHostServices = null;
        }

        /// <summary>
        /// Emits the single startup identity line required by the performance
        /// profile contract and warns when a clean mode still has intrusive state.
        /// </summary>
        public static void LogActivePerformanceProfile()
        {
            string profileMode = ResolvePerformanceProfileMode();
            RuntimeEngine.Rendering.Stats.FrameOutputManifestSnapshot outputManifest =
                RuntimeEngine.Rendering.Stats.FrameOutputs.LastManifest;
            PerformanceObserverMetadata observers =
                CapturePerformanceObserverMetadata(profileMode, outputManifest);
            Debug.Rendering(
                EOutputVerbosity.Normal,
                false,
                "[PerformanceProfile] Mode={0} Suitability={1} CleanComparison={2} PromotionEligible={3} Intrusive={4} WarmupSec={5} CaptureSec={6}",
                profileMode,
                observers.Suitability,
                observers.ComparisonSuitable,
                observers.PromotionEligible,
                observers.Intrusive,
                Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.ProfileWarmupSeconds) ?? "unspecified",
                Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.ProfileCaptureSeconds) ?? "unspecified");

            if (IsCleanPerformanceProfile(profileMode) && !observers.ComparisonSuitable)
            {
                Debug.LogWarning(
                    $"[PerformanceProfile] {profileMode} requested, but intrusive observer state remains active. " +
                    "The capture will be rejected for clean comparison.");
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

            // Capture is itself an explicit request for render telemetry. Reassert tracking here
            // because persisted preference side effects can run after startup capture setup.
            RuntimeEngine.Rendering.Stats.EnableTracking = true;

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

                int sampleIntervalFrames = GetSampleIntervalFramesNoLock();
                int snapshotCount = ++s_snapshotCount;
                if (snapshotCount == 1 || snapshotCount % sampleIntervalFrames == 0)
                {
                    AppendSampleLineNoLock(metadata, nowTicks);
                    s_sampleCount++;

                    if (ShouldFlushNoLock(nowTicks))
                        FlushSamplesNoLock();
                }

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
                gpuDumpSucceeded = RuntimeEngine.Rendering.Stats.GpuPipelineProfiler.TryDumpAllGpuRenderPipelineTimingHistories(
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
                    RuntimeEngine.Rendering.Stats.FrameOutputManifestSnapshot currentOutputManifest =
                        RuntimeEngine.Rendering.Stats.FrameOutputs.LastManifest;
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
            bool primaryReuseSetting = CaptureBoolean(() => RuntimeEngine.Rendering.Settings.EnableVulkanPrimaryCommandBufferReuse);
            bool primaryReuseEnabled = ResolveOptionalBooleanOverride(primaryReuseEnvironment) ?? primaryReuseSetting;
            string primaryReusePolicy = string.IsNullOrWhiteSpace(primaryReuseEnvironment)
                ? $"Setting:{primaryReuseSetting}"
                : $"Environment:{primaryReuseEnvironment}";
            string sceneIdentity = CaptureSceneIdentity();
            string settingsIdentity = BuildSettingsIdentity(renderTargetModeEnv, renderTargetModeSetting);
            string sceneIdentityHash = ComputeStableIdentityHash(sceneIdentity);
            string settingsIdentityHash = ComputeStableIdentityHash(settingsIdentity);
            string sceneSettingsHash = ComputeStableIdentityHash(sceneIdentity + "|" + settingsIdentity);
            RuntimeEngine.Rendering.Stats.FrameOutputManifestSnapshot outputManifest = RuntimeEngine.Rendering.Stats.FrameOutputs.LastManifest;
            string profileMode = ResolvePerformanceProfileMode();
            PerformanceObserverMetadata observers =
                CapturePerformanceObserverMetadata(profileMode, outputManifest);
            ActiveRenderFeaturesMetadata activeRenderFeatures =
                CaptureActiveRenderFeatures(outputManifest);

            s_metadata = new RunMetadata(
                SchemaVersion: ProfileCaptureSchemaVersion,
                CaptureMode: runtimeCapture ? "runtime" : "launch",
                RunLabel: runLabel,
                WorldMode: Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.WorldMode) ?? string.Empty,
                ForcedStrategy: Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.ForceMeshSubmissionStrategy) ?? string.Empty,
                EffectiveStrategy: CaptureString(() => RuntimeEngine.Rendering.LastResolvedMeshSubmissionStrategy.ToString()),
                ZeroReadbackMaterialDrawPath: CaptureString(() => Engine.EffectiveSettings.ZeroReadbackMaterialDrawPath.ToString()),
                ZeroReadbackMaterialDrawPathEnv: Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.ZeroReadbackMaterialDrawPath) ?? string.Empty,
                Backend: CaptureString(() => RuntimeEngine.Rendering.Stats.RendererState.ActiveRenderBackend),
                GpuName: CaptureString(() => RuntimeEngine.Rendering.State.OpenGLRendererName ?? RuntimeEngine.Rendering.State.VulkanDeviceName ?? string.Empty),
                GpuVendor: CaptureString(() => RuntimeEngine.Rendering.State.OpenGLVendor ?? string.Empty),
                GpuDeviceId: Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.GpuDeviceId) ?? string.Empty,
                Driver: Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.GpuDriver) ?? string.Empty,
                Scene: Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.ProfileScene) ?? string.Empty,
                Camera: Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.ProfileCamera) ?? string.Empty,
                Lights: Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.ProfileLights) ?? string.Empty,
                Viewport: Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.ProfileViewport) ?? string.Empty,
                RenderScale: Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.ProfileRenderScale) ??
                    CaptureString(() => RuntimeEngine.Rendering.Settings.TsrRenderScale.ToString(CultureInfo.InvariantCulture)),
                SceneIdentity: sceneIdentity,
                SceneIdentityHash: sceneIdentityHash,
                SettingsIdentityHash: settingsIdentityHash,
                SceneSettingsHash: sceneSettingsHash,
                FrameOutputWorkloadIdentityHash: outputManifest.WorkloadIdentityHash,
                OutputInventory: CaptureOutputInventory(outputManifest),
                StereoMode: CaptureString(() => RuntimeEngine.Rendering.Stats.RendererState.ActiveStereoMode),
                VrViewRenderModeRequested: CaptureString(() => RuntimeEngine.Rendering.Stats.RendererState.ActiveVrViewRenderModeRequested),
                VrViewRenderModeEffective: CaptureString(() => RuntimeEngine.Rendering.Stats.RendererState.ActiveVrViewRenderModeEffective),
                VrViewRenderImplementationPath: CaptureString(() => RuntimeEngine.Rendering.Stats.RendererState.ActiveVrViewRenderImplementationPath),
                VrTemporalHistoryPolicy: CaptureString(() => RuntimeEngine.Rendering.Stats.RendererState.ActiveVrTemporalHistoryPolicy),
                VrFoveationMode: CaptureString(() => RuntimeEngine.Rendering.Settings.VrFoveationMode.ToString()),
                VrMirrorMode: CaptureString(() => RuntimeEngine.Rendering.Settings.VrMirrorMode.ToString()),
                RenderWindowsWhileInVR: CaptureString(() => RuntimeEngine.Rendering.Settings.RenderWindowsWhileInVR ? "true" : "false"),
                VrMirrorComposeFromEyeTextures: CaptureString(() => RuntimeEngine.Rendering.Settings.VrMirrorComposeFromEyeTextures ? "true" : "false"),
                VrDesktopEditorTargetRateHz: CaptureString(() => RuntimeEngine.Rendering.Settings.VrDesktopEditorTargetRateHz.ToString(CultureInfo.InvariantCulture)),
                VrCyclopeanDesktopTargetRateHz: CaptureString(() => RuntimeEngine.Rendering.Settings.VrCyclopeanDesktopTargetRateHz.ToString(CultureInfo.InvariantCulture)),
                VrDesktopAutoSkipWhenOverBudget: CaptureString(() => RuntimeEngine.Rendering.Settings.VrDesktopAutoSkipWhenOverBudget ? "true" : "false"),
                VulkanRenderTargetModeEnvironment: renderTargetModeEnv,
                VulkanRenderTargetModeSetting: renderTargetModeSetting,
                VulkanPrimaryCommandBufferReusePolicy: primaryReusePolicy,
                VulkanPrimaryCommandBufferReuseEnabled: primaryReuseEnabled,
                VulkanObsHookPolicy: Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.VkObsHook) ?? "Auto",
                VulkanSkipImGui: IsEnvFlagEnabled(XREngineEnvironmentVariables.VkSkipImGui),
                ValidationLayersEnabled: CaptureString(() => RuntimeEngine.Rendering.Stats.RendererState.ValidationLayersEnabled ? "true" : "false"),
                DebugOutputEnabled: CaptureString(() => RuntimeEngine.Rendering.Stats.RendererState.DebugOutputEnabled ? "true" : "false"),
                DeferredDebugView: CaptureString(() => global::XREngine.Rendering.RenderDiagnosticsFlags.DeferredDebugView.ToString(CultureInfo.InvariantCulture)),
                DeferredDebugEnv: Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.DeferredDebug) ?? string.Empty,
                ShaderCacheState: Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.ShaderCacheMode) ?? string.Empty,
                TextureCacheState: Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.TextureCacheMode) ?? string.Empty,
                CacheMode: Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.ProfileCacheMode) ?? string.Empty,
                ProfileMode: profileMode,
                ProfileSuitability: observers.Suitability,
                ProfileComparisonSuitable: observers.ComparisonSuitable,
                ProfilePromotionEligible: observers.PromotionEligible,
                ProfileIntrusive: observers.Intrusive,
                VulkanCommandBufferLabelsEnabled: observers.CommandBufferLabelsEnabled,
                P3LoggingEnabled: observers.P3LoggingEnabled,
                DiagnosticTraceFlagsEnabled: observers.DiagnosticTraceFlagsEnabled,
                ActiveDiagnosticTraceFlags: observers.ActiveDiagnosticTraceFlags,
                ProfilerUiState: observers.ProfilerUiState,
                EditorUiState: observers.EditorUiState,
                DynamicTextOverlayEnabled: observers.DynamicTextOverlayEnabled,
                DebugOverlayEnabled: observers.DebugOverlayEnabled,
                LogVerbosity: RuntimeDebugHostServices.Current.OutputVerbosity.ToString(),
                LogOutputToFile: RuntimeDebugHostServices.Current.LogOutputToFile,
                LogSessionPath: CaptureString(Debug.EnsureLogRunDirectory),
                XrRuntime: CaptureString(() => RuntimeEngine.VRState.ActiveRuntime.ToString()),
                XrRuntimeManifest: Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.XrRuntimeJson) ?? string.Empty,
                ActiveRenderFeatures: activeRenderFeatures,
                VulkanGpuDrivenProfile: CaptureString(() => Engine.EffectiveSettings.VulkanGpuDrivenProfile.ToString()),
                GpuClockPolicy: Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.GpuClockPolicy) ?? string.Empty,
                TargetRefreshHz: targetRefreshHz,
                XrFrameBudgetMs: xrFrameBudgetMs,
                BenchmarkPhase: Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.ProfilePhase) ?? string.Empty,
                WarmupSeconds: TryParsePositiveDouble(Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.ProfileWarmupSeconds)),
                CaptureSeconds: TryParsePositiveDouble(Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.ProfileCaptureSeconds)),
                SampleIntervalFrames: GetSampleIntervalFramesNoLock(),
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
                schema = "xrengine.profile_capture.render_stats.v7",
                schema_version = ProfileCaptureSchemaVersion,
                fields_note = metadata.SampleIntervalFrames == 1
                    ? "One JSON object per completed render frame. CPU frame timings are wall-clock thread loop durations; GPU pipeline timings are backend timestamp-query snapshots when ready."
                    : $"One JSON object for the first completed render frame and then every {metadata.SampleIntervalFrames} completed render frames. CPU frame timings are wall-clock thread loop durations; GPU pipeline timings are backend timestamp-query snapshots when ready.",
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
            double gpuPipelineMs = RuntimeEngine.Rendering.Stats.GpuPipelineProfiler.GpuRenderPipelineFrameMs;
            bool gpuTimingsReady = RuntimeEngine.Rendering.Stats.GpuPipelineProfiler.GpuRenderPipelineTimingsReady;
            RuntimeEngine.Rendering.Stats.FrameOutputManifestSnapshot frameOutputs = RuntimeEngine.Rendering.Stats.FrameOutputs.LastManifest;

            s_lineBuilder.Clear();
            s_lineBuilder.Append('{');
            bool first = true;

            AppendStringField(s_lineBuilder, "ts_utc", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture), ref first);
            AppendNumberField(s_lineBuilder, "profile_schema_version", ProfileCaptureSchemaVersion, ref first);
            AppendNumberField(s_lineBuilder, "elapsed_ms", elapsedMs, ref first);
            AppendNumberField(s_lineBuilder, "process_id", Environment.ProcessId, ref first);
            ulong renderFrameId = RuntimeEngine.Rendering.State.RenderFrameId;
            AppendNumberField(s_lineBuilder, "render_frame_id", renderFrameId, ref first);
            AppendNumberField(s_lineBuilder, "completed_frame_id", renderFrameId == 0UL ? 0UL : renderFrameId - 1UL, ref first);
            AppendNumberField(s_lineBuilder, "update_frame_id", RuntimeEngine.Rendering.Stats.FrameLifecycle.UpdateFrameId, ref first);
            AppendNumberField(s_lineBuilder, "collect_frame_id", RuntimeEngine.Rendering.Stats.FrameLifecycle.CollectFrameId, ref first);
            AppendNumberField(s_lineBuilder, "swap_frame_id", RuntimeEngine.Rendering.Stats.FrameLifecycle.SwapFrameId, ref first);
            AppendNumberField(s_lineBuilder, "present_frame_id", RuntimeEngine.Rendering.Stats.FrameLifecycle.PresentFrameId, ref first);
            AppendStringField(s_lineBuilder, "capture_mode", metadata.CaptureMode, ref first);
            AppendStringField(s_lineBuilder, "run_label", metadata.RunLabel, ref first);
            AppendStringField(s_lineBuilder, "world_mode", metadata.WorldMode, ref first);
            AppendStringField(s_lineBuilder, "forced_strategy", metadata.ForcedStrategy, ref first);
            AppendStringField(s_lineBuilder, "requested_strategy", RuntimeEngine.Rendering.ResolveRequestedMeshSubmissionStrategy().ToString(), ref first);
            AppendStringField(s_lineBuilder, "effective_strategy", RuntimeEngine.Rendering.LastResolvedMeshSubmissionStrategy.ToString(), ref first);
            AppendStringField(s_lineBuilder, "meshlet_renderer_backend", RuntimeEngine.Rendering.LastResolvedRendererBackend.ToString(), ref first);
            AppendStringField(s_lineBuilder, "meshlet_shader_dialect", RuntimeEngine.Rendering.LastResolvedMeshShaderDialect.ToString(), ref first);
            AppendBoolField(s_lineBuilder, "meshlet_renderer_dispatch_ready", RuntimeEngine.Rendering.LastResolvedSupportsMeshletDispatch, ref first);
            AppendStringField(s_lineBuilder, "meshlet_downgrade_requested", RuntimeEngine.Rendering.LastMeshletDowngradeRequested?.ToString() ?? string.Empty, ref first);
            AppendStringField(s_lineBuilder, "meshlet_downgrade_resolved", RuntimeEngine.Rendering.LastMeshletDowngradeResolved?.ToString() ?? string.Empty, ref first);
            AppendStringField(s_lineBuilder, "meshlet_downgrade_reason", RuntimeEngine.Rendering.LastMeshletDowngradeReason ?? string.Empty, ref first);
            AppendStringField(s_lineBuilder, "zero_readback_material_draw_path", metadata.ZeroReadbackMaterialDrawPath, ref first);
            AppendStringField(s_lineBuilder, "zero_readback_material_draw_path_env", metadata.ZeroReadbackMaterialDrawPathEnv, ref first);
            AppendStringField(s_lineBuilder, "p3_logging", metadata.P3Logging, ref first);
            AppendStringField(s_lineBuilder, "active_texture_binding_rung", RuntimeEngine.Rendering.Stats.RendererState.ActiveTextureBindingRung, ref first);
            AppendStringField(s_lineBuilder, "active_stereo_mode", RuntimeEngine.Rendering.Stats.RendererState.ActiveStereoMode, ref first);
            AppendStringField(s_lineBuilder, "vr_view_render_mode_requested", RuntimeEngine.Rendering.Stats.RendererState.ActiveVrViewRenderModeRequested, ref first);
            AppendStringField(s_lineBuilder, "vr_view_render_mode_effective", RuntimeEngine.Rendering.Stats.RendererState.ActiveVrViewRenderModeEffective, ref first);
            AppendStringField(s_lineBuilder, "vr_view_render_implementation_path", RuntimeEngine.Rendering.Stats.RendererState.ActiveVrViewRenderImplementationPath, ref first);
            AppendStringField(s_lineBuilder, "vr_temporal_history_policy", RuntimeEngine.Rendering.Stats.RendererState.ActiveVrTemporalHistoryPolicy, ref first);
            AppendStringField(s_lineBuilder, "vr_foveation_mode", RuntimeEngine.Rendering.Settings.VrFoveationMode.ToString(), ref first);
            AppendStringField(s_lineBuilder, "vr_mirror_mode", frameOutputs.MirrorMode.ToString(), ref first);
            AppendStringField(s_lineBuilder, "vr_visibility_policy", frameOutputs.VisibilityPolicy.ToString(), ref first);
            AppendBoolField(s_lineBuilder, "render_windows_while_in_vr", RuntimeEngine.Rendering.Settings.RenderWindowsWhileInVR, ref first);
            AppendBoolField(s_lineBuilder, "vr_mirror_compose_from_eye_textures", RuntimeEngine.Rendering.Settings.VrMirrorComposeFromEyeTextures, ref first);
            AppendNumberField(s_lineBuilder, "vr_desktop_editor_target_rate_hz", RuntimeEngine.Rendering.Settings.VrDesktopEditorTargetRateHz, ref first);
            AppendNumberField(s_lineBuilder, "vr_cyclopean_desktop_target_rate_hz", RuntimeEngine.Rendering.Settings.VrCyclopeanDesktopTargetRateHz, ref first);
            AppendBoolField(s_lineBuilder, "vr_desktop_auto_skip_when_over_budget", RuntimeEngine.Rendering.Settings.VrDesktopAutoSkipWhenOverBudget, ref first);
            AppendStringField(s_lineBuilder, "active_render_backend", RuntimeEngine.Rendering.Stats.RendererState.ActiveRenderBackend, ref first);
            AppendStringField(s_lineBuilder, "profile_mode", metadata.ProfileMode, ref first);
            AppendStringField(s_lineBuilder, "profile_suitability", metadata.ProfileSuitability, ref first);
            AppendBoolField(s_lineBuilder, "profile_comparison_suitable", metadata.ProfileComparisonSuitable, ref first);
            AppendBoolField(s_lineBuilder, "profile_promotion_eligible", metadata.ProfilePromotionEligible, ref first);
            AppendBoolField(s_lineBuilder, "profile_intrusive", metadata.ProfileIntrusive, ref first);
            AppendBoolField(s_lineBuilder, "vulkan_command_buffer_labels_enabled", metadata.VulkanCommandBufferLabelsEnabled, ref first);
            AppendBoolField(s_lineBuilder, "p3_logging_enabled", metadata.P3LoggingEnabled, ref first);
            AppendBoolField(s_lineBuilder, "diagnostic_trace_flags_enabled", metadata.DiagnosticTraceFlagsEnabled, ref first);
            AppendStringField(s_lineBuilder, "active_diagnostic_trace_flags", metadata.ActiveDiagnosticTraceFlags, ref first);
            AppendStringField(s_lineBuilder, "profiler_ui_state", metadata.ProfilerUiState, ref first);
            AppendStringField(s_lineBuilder, "editor_ui_state", metadata.EditorUiState, ref first);
            AppendBoolField(s_lineBuilder, "dynamic_text_overlay_enabled", metadata.DynamicTextOverlayEnabled, ref first);
            AppendBoolField(s_lineBuilder, "debug_overlay_enabled", metadata.DebugOverlayEnabled, ref first);
            AppendStringField(s_lineBuilder, "log_verbosity", metadata.LogVerbosity, ref first);
            AppendStringField(s_lineBuilder, "log_session_path", metadata.LogSessionPath, ref first);
            AppendStringField(s_lineBuilder, "xr_runtime", metadata.XrRuntime, ref first);
            AppendStringField(s_lineBuilder, "shader_cache_state", metadata.ShaderCacheState, ref first);
            AppendStringField(s_lineBuilder, "texture_cache_state", metadata.TextureCacheState, ref first);
            AppendBoolField(s_lineBuilder, "render_feature_state_available", metadata.ActiveRenderFeatures.CameraStateAvailable, ref first);
            AppendStringField(s_lineBuilder, "anti_aliasing_mode", metadata.ActiveRenderFeatures.AntiAliasingMode, ref first);
            AppendNumberField(s_lineBuilder, "msaa_sample_count", metadata.ActiveRenderFeatures.MsaaSampleCount, ref first);
            AppendNumberField(s_lineBuilder, "tsr_render_scale", metadata.ActiveRenderFeatures.TsrRenderScale, ref first);
            AppendBoolField(s_lineBuilder, "ambient_occlusion_enabled", metadata.ActiveRenderFeatures.AmbientOcclusionEnabled, ref first);
            AppendStringField(s_lineBuilder, "ambient_occlusion_mode", metadata.ActiveRenderFeatures.AmbientOcclusionMode, ref first);
            AppendBoolField(s_lineBuilder, "auto_exposure_enabled", metadata.ActiveRenderFeatures.AutoExposureEnabled, ref first);
            AppendBoolField(s_lineBuilder, "bloom_enabled", metadata.ActiveRenderFeatures.BloomEnabled, ref first);
            AppendBoolField(s_lineBuilder, "motion_blur_enabled", metadata.ActiveRenderFeatures.MotionBlurEnabled, ref first);
            AppendBoolField(s_lineBuilder, "motion_vectors_requested", metadata.ActiveRenderFeatures.MotionVectorsRequested, ref first);
            AppendBoolField(s_lineBuilder, "validation_layers_enabled", RuntimeEngine.Rendering.Stats.RendererState.ValidationLayersEnabled, ref first);
            AppendBoolField(s_lineBuilder, "debug_output_enabled", RuntimeEngine.Rendering.Stats.RendererState.DebugOutputEnabled, ref first);
            AppendNumberField(s_lineBuilder, "deferred_debug_view", global::XREngine.Rendering.RenderDiagnosticsFlags.DeferredDebugView, ref first);
            AppendStringField(s_lineBuilder, "deferred_debug_env", metadata.DeferredDebugEnv, ref first);
            AppendBoolField(s_lineBuilder, "gpu_timestamps_dense_mode", RuntimeEngine.Rendering.Stats.RendererState.GpuTimestampsDenseMode, ref first);

            AppendNumberField(s_lineBuilder, "render_dispatch_ms", renderMs, ref first);
            AppendNumberField(
                s_lineBuilder,
                "render_outside_vulkan_frame_ms",
                Math.Max(
                    0.0,
                    renderMs -
                    RuntimeEngine.Rendering.Stats.Vulkan.VulkanFrameTotalMs),
                ref first);
            AppendNumberField(s_lineBuilder, "update_ms", updateMs, ref first);
            AppendNumberField(s_lineBuilder, "collect_visible_ms", collectVisibleMs, ref first);
            AppendNumberField(s_lineBuilder, "fixed_update_ms", fixedUpdateMs, ref first);
            AppendStringField(s_lineBuilder, "collect_visible_late_policy", RuntimeEngine.Rendering.Stats.FrameLifecycle.CollectVisibleLatePolicy, ref first);
            AppendNumberField(s_lineBuilder, "collect_generation_requested", RuntimeEngine.Rendering.Stats.FrameLifecycle.RequestedCollectGeneration, ref first);
            AppendNumberField(s_lineBuilder, "collect_generation_completed", RuntimeEngine.Rendering.Stats.FrameLifecycle.CompletedCollectGeneration, ref first);
            AppendNumberField(s_lineBuilder, "collect_generation_published", RuntimeEngine.Rendering.Stats.FrameLifecycle.PublishedCollectGeneration, ref first);
            AppendNumberField(s_lineBuilder, "collect_generation_consumed", RuntimeEngine.Rendering.Stats.FrameLifecycle.ConsumedCollectGeneration, ref first);
            AppendNumberField(s_lineBuilder, "collect_generation_required", RuntimeEngine.Rendering.Stats.FrameLifecycle.RequiredCollectGeneration, ref first);
            AppendNumberField(s_lineBuilder, "collect_wait_for_render_ms", RuntimeEngine.Rendering.Stats.FrameLifecycle.CollectWaitForRenderMs, ref first);
            AppendStringField(s_lineBuilder, "collect_wait_reason", RuntimeEngine.Rendering.Stats.FrameLifecycle.CollectWaitReason, ref first);
            AppendNumberField(s_lineBuilder, "render_wait_for_collect_ms", RuntimeEngine.Rendering.Stats.FrameLifecycle.RenderWaitForCollectMs, ref first);
            AppendStringField(s_lineBuilder, "render_wait_reason", RuntimeEngine.Rendering.Stats.FrameLifecycle.RenderWaitReason, ref first);
            AppendNumberField(s_lineBuilder, "skipped_collect_frames", RuntimeEngine.Rendering.Stats.FrameLifecycle.SkippedCollectFrames, ref first);
            AppendNumberField(s_lineBuilder, "stale_collect_reuse_frames", RuntimeEngine.Rendering.Stats.FrameLifecycle.StaleCollectReuseFrames, ref first);
            AppendNumberField(s_lineBuilder, "frame_package_production_ms", RuntimeEngine.Rendering.Stats.FrameLifecycle.FramePackageProductionMs, ref first);
            AppendNumberField(s_lineBuilder, "frame_package_publication_ms", RuntimeEngine.Rendering.Stats.FrameLifecycle.FramePackagePublicationMs, ref first);
            AppendNumberField(s_lineBuilder, "frame_package_validation_ms", RuntimeEngine.Rendering.Stats.FrameLifecycle.FramePackageValidationMs, ref first);
            AppendNumberField(s_lineBuilder, "frame_package_consumption_ms", RuntimeEngine.Rendering.Stats.FrameLifecycle.FramePackageConsumptionMs, ref first);
            AppendNumberField(s_lineBuilder, "frame_packages_prepared", RuntimeEngine.Rendering.Stats.FrameLifecycle.FramePackagesPrepared, ref first);
            AppendNumberField(s_lineBuilder, "frame_packages_published", RuntimeEngine.Rendering.Stats.FrameLifecycle.FramePackagesPublished, ref first);
            AppendNumberField(s_lineBuilder, "frame_packages_consumed", RuntimeEngine.Rendering.Stats.FrameLifecycle.FramePackagesConsumed, ref first);
            AppendNumberField(s_lineBuilder, "frame_packages_prepared_late", RuntimeEngine.Rendering.Stats.FrameLifecycle.FramePackagesPreparedLate, ref first);
            AppendNumberField(s_lineBuilder, "frame_packages_rejected", RuntimeEngine.Rendering.Stats.FrameLifecycle.FramePackagesRejected, ref first);
            AppendNumberField(s_lineBuilder, "frame_package_generation_age", RuntimeEngine.Rendering.Stats.FrameLifecycle.FramePackageGenerationAge, ref first);
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

            AppendNumberField(s_lineBuilder, "draw_calls", RuntimeEngine.Rendering.Stats.Frame.DrawCalls, ref first);
            AppendNumberField(s_lineBuilder, "multi_draw_calls", RuntimeEngine.Rendering.Stats.Frame.MultiDrawCalls, ref first);
            AppendNumberField(s_lineBuilder, "triangles_rendered", RuntimeEngine.Rendering.Stats.Frame.TrianglesRendered, ref first);
            AppendNumberField(s_lineBuilder, "gpu_mapped_buffers", RuntimeEngine.Rendering.Stats.GpuReadback.GpuMappedBuffers, ref first);
            AppendNumberField(s_lineBuilder, "gpu_readback_bytes", RuntimeEngine.Rendering.Stats.GpuReadback.GpuReadbackBytes, ref first);
            AppendNumberField(s_lineBuilder, "indirect_count_calls", RuntimeEngine.Rendering.Stats.RendererState.IndirectCountCalls, ref first);
            AppendNumberField(s_lineBuilder, "shader_program_switches", RuntimeEngine.Rendering.Stats.RendererState.ShaderProgramSwitches, ref first);
            AppendNumberField(s_lineBuilder, "program_pipeline_switches", RuntimeEngine.Rendering.Stats.RendererState.ProgramPipelineSwitches, ref first);
            AppendNumberField(s_lineBuilder, "vao_binds", RuntimeEngine.Rendering.Stats.RendererState.VaoBinds, ref first);
            AppendNumberField(s_lineBuilder, "vao_bind_skips", RuntimeEngine.Rendering.Stats.RendererState.VaoBindSkips, ref first);
            AppendNumberField(s_lineBuilder, "array_buffer_binds", RuntimeEngine.Rendering.Stats.RendererState.ArrayBufferBinds, ref first);
            AppendNumberField(s_lineBuilder, "element_array_buffer_binds", RuntimeEngine.Rendering.Stats.RendererState.ElementArrayBufferBinds, ref first);
            AppendNumberField(s_lineBuilder, "draw_indirect_buffer_binds", RuntimeEngine.Rendering.Stats.RendererState.DrawIndirectBufferBinds, ref first);
            AppendNumberField(s_lineBuilder, "parameter_buffer_binds", RuntimeEngine.Rendering.Stats.RendererState.ParameterBufferBinds, ref first);
            AppendNumberField(s_lineBuilder, "ssbo_binds", RuntimeEngine.Rendering.Stats.RendererState.SsboBinds, ref first);
            AppendNumberField(s_lineBuilder, "ubo_binds", RuntimeEngine.Rendering.Stats.RendererState.UboBinds, ref first);
            AppendNumberField(s_lineBuilder, "texture_binds", RuntimeEngine.Rendering.Stats.RendererState.TextureBinds, ref first);
            AppendNumberField(s_lineBuilder, "texture_bind_skips", RuntimeEngine.Rendering.Stats.RendererState.TextureBindSkips, ref first);
            AppendNumberField(s_lineBuilder, "texture_unit_switches", RuntimeEngine.Rendering.Stats.RendererState.TextureUnitSwitches, ref first);
            AppendNumberField(s_lineBuilder, "uniform_calls", RuntimeEngine.Rendering.Stats.RendererState.UniformCalls, ref first);
            AppendNumberField(s_lineBuilder, "sampler_uniform_calls", RuntimeEngine.Rendering.Stats.RendererState.SamplerUniformCalls, ref first);
            AppendNumberField(s_lineBuilder, "buffer_upload_bytes", RuntimeEngine.Rendering.Stats.RendererState.BufferUploadBytes, ref first);
            AppendNumberField(s_lineBuilder, "barrier_calls", RuntimeEngine.Rendering.Stats.RendererState.BarrierCalls, ref first);
            AppendNumberField(s_lineBuilder, "barrier_all", RuntimeEngine.Rendering.Stats.RendererState.BarrierAll, ref first);
            AppendNumberField(s_lineBuilder, "barrier_command", RuntimeEngine.Rendering.Stats.RendererState.BarrierCommand, ref first);
            AppendNumberField(s_lineBuilder, "barrier_buffer_update", RuntimeEngine.Rendering.Stats.RendererState.BarrierBufferUpdate, ref first);
            AppendNumberField(s_lineBuilder, "barrier_shader_storage", RuntimeEngine.Rendering.Stats.RendererState.BarrierShaderStorage, ref first);
            AppendNumberField(s_lineBuilder, "barrier_texture_fetch", RuntimeEngine.Rendering.Stats.RendererState.BarrierTextureFetch, ref first);
            AppendNumberField(s_lineBuilder, "barrier_texture_update", RuntimeEngine.Rendering.Stats.RendererState.BarrierTextureUpdate, ref first);
            AppendNumberField(s_lineBuilder, "barrier_framebuffer", RuntimeEngine.Rendering.Stats.RendererState.BarrierFramebuffer, ref first);
            AppendNumberField(s_lineBuilder, "timestamp_query_count", RuntimeEngine.Rendering.Stats.RendererState.TimestampQueryCount, ref first);
            AppendNumberField(s_lineBuilder, "timestamp_query_readback_bytes", RuntimeEngine.Rendering.Stats.RendererState.TimestampQueryReadbackBytes, ref first);
            AppendNumberField(s_lineBuilder, "timestamp_dense_mode_frames", RuntimeEngine.Rendering.Stats.RendererState.TimestampDenseModeFrames, ref first);
            AppendNumberField(s_lineBuilder, "redundant_state_skips", RuntimeEngine.Rendering.Stats.RendererState.RedundantStateSkips, ref first);
            AppendNumberField(s_lineBuilder, "cpu_direct_draw_calls", RuntimeEngine.Rendering.Stats.RendererState.CpuDirectDrawCalls, ref first);
            AppendNumberField(s_lineBuilder, "gpu_indirect_draw_calls", RuntimeEngine.Rendering.Stats.RendererState.GpuIndirectDrawCalls, ref first);
            AppendNumberField(s_lineBuilder, "gpu_meshlet_draw_calls", RuntimeEngine.Rendering.Stats.RendererState.GpuMeshletDrawCalls, ref first);
            AppendNumberField(s_lineBuilder, "unknown_strategy_draw_calls", RuntimeEngine.Rendering.Stats.RendererState.UnknownStrategyDrawCalls, ref first);
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
            AppendNumberField(s_lineBuilder, "cpu_soc_occluders_selected", OcclusionTelemetry.CpuSocOccludersSelected, ref first);
            AppendNumberField(s_lineBuilder, "cpu_soc_occluders_rasterized", OcclusionTelemetry.CpuSocOccludersRasterized, ref first);
            AppendNumberField(s_lineBuilder, "cpu_soc_tiles_closed", OcclusionTelemetry.CpuSocTilesClosed, ref first);
            AppendNumberField(s_lineBuilder, "cpu_soc_begin_ms", OcclusionTelemetry.CpuSocBeginMilliseconds, ref first);
            AppendNumberField(s_lineBuilder, "cpu_soc_selection_ms", OcclusionTelemetry.CpuSocSelectionMilliseconds, ref first);
            AppendNumberField(s_lineBuilder, "cpu_soc_sort_ms", OcclusionTelemetry.CpuSocSortMilliseconds, ref first);
            AppendNumberField(s_lineBuilder, "cpu_soc_raster_ms", OcclusionTelemetry.CpuSocRasterMilliseconds, ref first);
            AppendNumberField(s_lineBuilder, "cpu_soc_test_ms", OcclusionTelemetry.CpuSocTestMilliseconds, ref first);
            AppendBoolField(s_lineBuilder, "cpu_soc_force_visible", OcclusionTelemetry.CpuSocForceVisible, ref first);
            AppendNumberField(s_lineBuilder, "cpu_soc_self_occluder_skipped", OcclusionTelemetry.CpuSocSelfOccluderSkipped, ref first);
            AppendNumberField(s_lineBuilder, "profiler_ui_ingestion_ms", ProfilerObserverTelemetry.IngestionMilliseconds, ref first);
            AppendNumberField(s_lineBuilder, "profiler_ui_aggregation_ms", ProfilerObserverTelemetry.AggregationMilliseconds, ref first);
            AppendNumberField(s_lineBuilder, "profiler_ui_graph_preparation_ms", ProfilerObserverTelemetry.GraphPreparationMilliseconds, ref first);
            AppendNumberField(s_lineBuilder, "profiler_ui_table_preparation_ms", ProfilerObserverTelemetry.TablePreparationMilliseconds, ref first);
            AppendNumberField(s_lineBuilder, "profiler_ui_imgui_draw_ms", ProfilerObserverTelemetry.ImGuiDrawMilliseconds, ref first);
            AppendNumberField(s_lineBuilder, "profiler_ui_visible_rows", ProfilerObserverTelemetry.VisibleRows, ref first);
            AppendNumberField(s_lineBuilder, "profiler_ui_graph_samples", ProfilerObserverTelemetry.GraphSamples, ref first);
            AppendRenderThreadJobFields(s_lineBuilder, ref first);
            AppendNumberField(s_lineBuilder, "directional_cascade_stale_sampled", RuntimeEngine.Rendering.Stats.RendererState.DirectionalCascadeStaleSampled, ref first);
            AppendNumberField(s_lineBuilder, "directional_cascade_mixed_generation_prevented", RuntimeEngine.Rendering.Stats.RendererState.DirectionalCascadeMixedGenerationPrevented, ref first);
            AppendNumberField(s_lineBuilder, "directional_cascade_physical_reprojected", RuntimeEngine.Rendering.Stats.RendererState.DirectionalCascadePhysicalReprojected, ref first);
            AppendNumberField(s_lineBuilder, "directional_cascade_forced_fresh_render", RuntimeEngine.Rendering.Stats.RendererState.DirectionalCascadeForcedFreshRender, ref first);
            AppendNumberField(s_lineBuilder, "visible_renderer_count", RuntimeEngine.Rendering.Stats.SceneAssets.VisibleRendererCount, ref first);
            AppendNumberField(s_lineBuilder, "visible_submesh_count", RuntimeEngine.Rendering.Stats.SceneAssets.VisibleSubmeshCount, ref first);
            AppendNumberField(s_lineBuilder, "visible_triangle_count", RuntimeEngine.Rendering.Stats.SceneAssets.VisibleTriangleCount, ref first);
            AppendNumberField(s_lineBuilder, "material_slot_count", RuntimeEngine.Rendering.Stats.SceneAssets.MaterialSlotCount, ref first);
            AppendNumberField(s_lineBuilder, "active_material_count", RuntimeEngine.Rendering.Stats.SceneAssets.ActiveMaterialCount, ref first);
            AppendNumberField(s_lineBuilder, "texture_count", RuntimeEngine.Rendering.Stats.SceneAssets.TextureCount, ref first);
            AppendNumberField(s_lineBuilder, "resident_texture_memory_bytes", RuntimeEngine.Rendering.Stats.SceneAssets.ResidentTextureMemoryBytes, ref first);
            AppendNumberField(s_lineBuilder, "texture_upload_jobs", RuntimeEngine.Rendering.Stats.SceneAssets.TextureUploadJobs, ref first);
            AppendNumberField(s_lineBuilder, "texture_upload_bytes", RuntimeEngine.Rendering.Stats.SceneAssets.TextureUploadBytes, ref first);
            AppendNumberField(s_lineBuilder, "texture_upload_ms", RuntimeEngine.Rendering.Stats.SceneAssets.TextureUploadMs, ref first);
            AppendNumberField(s_lineBuilder, "shader_variants_requested", RuntimeEngine.Rendering.Stats.SceneAssets.ShaderVariantsRequested, ref first);
            AppendNumberField(s_lineBuilder, "shader_variants_warming", RuntimeEngine.Rendering.Stats.SceneAssets.ShaderVariantsWarming, ref first);
            AppendNumberField(s_lineBuilder, "shader_variants_linked", RuntimeEngine.Rendering.Stats.SceneAssets.ShaderVariantsLinked, ref first);
            AppendNumberField(s_lineBuilder, "shader_variants_failed", RuntimeEngine.Rendering.Stats.SceneAssets.ShaderVariantsFailed, ref first);
            AppendNumberField(s_lineBuilder, "shader_variants_loaded_from_disk_cache", RuntimeEngine.Rendering.Stats.SceneAssets.ShaderVariantsLoadedFromDiskCache, ref first);
            AppendNumberField(s_lineBuilder, "shader_variants_generated_this_run", RuntimeEngine.Rendering.Stats.SceneAssets.ShaderVariantsGeneratedThisRun, ref first);
            AppendNumberField(s_lineBuilder, "skinned_renderer_count", RuntimeEngine.Rendering.Stats.SceneAssets.SkinnedRendererCount, ref first);
            AppendNumberField(s_lineBuilder, "bone_matrix_upload_bytes", RuntimeEngine.Rendering.Stats.SceneAssets.BoneMatrixUploadBytes, ref first);
            AppendNumberField(s_lineBuilder, "blendshape_weight_upload_bytes", RuntimeEngine.Rendering.Stats.SceneAssets.BlendshapeWeightUploadBytes, ref first);
            AppendNumberField(s_lineBuilder, "blendshape_active_list_upload_bytes", RuntimeEngine.Rendering.Stats.SceneAssets.BlendshapeActiveListUploadBytes, ref first);
            AppendNumberField(s_lineBuilder, "blendshape_delta_bytes", RuntimeEngine.Rendering.Stats.SceneAssets.BlendshapeDeltaBytes, ref first);
            AppendNumberField(s_lineBuilder, "skinning_core_influence_bytes", RuntimeEngine.Rendering.Stats.SceneAssets.SkinningCoreInfluenceBytes, ref first);
            AppendNumberField(s_lineBuilder, "skinning_spill_header_bytes", RuntimeEngine.Rendering.Stats.SceneAssets.SkinningSpillHeaderBytes, ref first);
            AppendNumberField(s_lineBuilder, "skinning_spill_entry_bytes", RuntimeEngine.Rendering.Stats.SceneAssets.SkinningSpillEntryBytes, ref first);
            AppendNumberField(s_lineBuilder, "skin_palette_upload_bytes", RuntimeEngine.Rendering.Stats.SceneAssets.SkinPaletteUploadBytes, ref first);
            AppendNumberField(s_lineBuilder, "skinning_compute_dispatch_count", RuntimeEngine.Rendering.Stats.SceneAssets.SkinningComputeDispatchCount, ref first);
            AppendNumberField(s_lineBuilder, "blendshape_compute_dispatch_count", RuntimeEngine.Rendering.Stats.SceneAssets.BlendshapeComputeDispatchCount, ref first);
            AppendNumberField(s_lineBuilder, "skipped_skinning_compute_dispatch_count", RuntimeEngine.Rendering.Stats.SceneAssets.SkippedSkinningComputeDispatchCount, ref first);
            AppendNumberField(s_lineBuilder, "skipped_blendshape_compute_dispatch_count", RuntimeEngine.Rendering.Stats.SceneAssets.SkippedBlendshapeComputeDispatchCount, ref first);
            AppendNumberField(s_lineBuilder, "reused_skinned_output_buffer_count", RuntimeEngine.Rendering.Stats.SceneAssets.ReusedSkinnedOutputBufferCount, ref first);
            AppendNumberField(s_lineBuilder, "live_skinning_shader_permutation_count", RuntimeEngine.Rendering.Stats.SceneAssets.LiveSkinningShaderPermutationCount, ref first);
            AppendNumberField(s_lineBuilder, "blendshape_authored_shape_count", RuntimeEngine.Rendering.Stats.SceneAssets.BlendshapeAuthoredShapeCount, ref first);
            AppendNumberField(s_lineBuilder, "blendshape_active_shape_count", RuntimeEngine.Rendering.Stats.SceneAssets.BlendshapeActiveShapeCount, ref first);
            AppendNumberField(s_lineBuilder, "blendshape_affected_vertex_count", RuntimeEngine.Rendering.Stats.SceneAssets.BlendshapeAffectedVertexCount, ref first);
            AppendNumberField(s_lineBuilder, "compacted_active_blendshape_count", RuntimeEngine.Rendering.Stats.SceneAssets.CompactedActiveBlendshapeCount, ref first);
            AppendNumberField(s_lineBuilder, "live_blendshape_shader_permutation_count", RuntimeEngine.Rendering.Stats.SceneAssets.LiveBlendshapeShaderPermutationCount, ref first);
            AppendNumberField(s_lineBuilder, "avatar_source_mesh_count", RuntimeEngine.Rendering.Stats.SceneAssets.AvatarSourceMeshCount, ref first);
            AppendNumberField(s_lineBuilder, "avatar_optimized_lod_count", RuntimeEngine.Rendering.Stats.SceneAssets.AvatarOptimizedLodCount, ref first);
            AppendNumberField(s_lineBuilder, "avatar_meshlet_count", RuntimeEngine.Rendering.Stats.SceneAssets.AvatarMeshletCount, ref first);
            AppendNumberField(s_lineBuilder, "avatar_visibility_buffer_count", RuntimeEngine.Rendering.Stats.SceneAssets.AvatarVisibilityBufferCount, ref first);
            AppendNumberField(s_lineBuilder, "avatar_cluster_virtualized_count", RuntimeEngine.Rendering.Stats.SceneAssets.AvatarClusterVirtualizedCount, ref first);
            AppendNumberField(s_lineBuilder, "avatar_octahedral_impostor_count", RuntimeEngine.Rendering.Stats.SceneAssets.AvatarOctahedralImpostorCount, ref first);
            AppendNumberField(s_lineBuilder, "avatar_gaussian_splat_count", RuntimeEngine.Rendering.Stats.SceneAssets.AvatarGaussianSplatCount, ref first);
            AppendRawJsonField(s_lineBuilder, "render_asset_cost_rows", JsonSerializer.Serialize(RuntimeEngine.Rendering.Stats.SceneAssets.GetAssetCostRows()), ref first);
            AppendNumberField(s_lineBuilder, "gpu_driven_culled_command_count", RuntimeEngine.Rendering.Stats.GpuDriven.CulledCommandCount, ref first);
            AppendNumberField(s_lineBuilder, "gpu_driven_active_bucket_count", RuntimeEngine.Rendering.Stats.GpuDriven.ActiveBucketCount, ref first);
            AppendNumberField(s_lineBuilder, "gpu_driven_empty_bucket_skips", RuntimeEngine.Rendering.Stats.GpuDriven.EmptyBucketSkips, ref first);
            AppendNumberField(s_lineBuilder, "gpu_driven_full_bucket_scans", RuntimeEngine.Rendering.Stats.GpuDriven.FullBucketScans, ref first);
            AppendNumberField(s_lineBuilder, "gpu_driven_material_scatter_dispatches", RuntimeEngine.Rendering.Stats.GpuDriven.MaterialScatterDispatches, ref first);
            AppendNumberField(s_lineBuilder, "gpu_driven_configured_material_slots", RuntimeEngine.Rendering.Stats.GpuDriven.ConfiguredMaterialSlots, ref first);
            AppendNumberField(s_lineBuilder, "gpu_driven_material_pass_groups", RuntimeEngine.Rendering.Stats.GpuDriven.MaterialPassGroups, ref first);
            AppendNumberField(s_lineBuilder, "gpu_driven_unsupported_compact_passes", RuntimeEngine.Rendering.Stats.GpuDriven.UnsupportedCompactPasses, ref first);
            AppendNumberField(s_lineBuilder, "gpu_driven_unsupported_compact_render_pass", RuntimeEngine.Rendering.Stats.GpuDriven.UnsupportedCompactRenderPass, ref first);
            AppendNumberField(s_lineBuilder, "gpu_scene_command_count", RuntimeEngine.Rendering.Stats.GpuDriven.SceneCommandCount, ref first);
            AppendNumberField(s_lineBuilder, "gpu_driven_command_capacity", RuntimeEngine.Rendering.Stats.GpuDriven.CommandCapacity, ref first);
            AppendNumberField(s_lineBuilder, "gpu_driven_active_command_count", RuntimeEngine.Rendering.Stats.GpuDriven.ActiveCommandCount, ref first);
            AppendNumberField(s_lineBuilder, "gpu_driven_material_lookup_capacity", RuntimeEngine.Rendering.Stats.GpuDriven.MaterialLookupCapacity, ref first);
            AppendNumberField(s_lineBuilder, "gpu_driven_active_material_slots", RuntimeEngine.Rendering.Stats.GpuDriven.ActiveMaterialSlots, ref first);
            AppendNumberField(s_lineBuilder, "gpu_driven_required_material_rows", RuntimeEngine.Rendering.Stats.GpuDriven.RequiredMaterialRows, ref first);
            AppendNumberField(s_lineBuilder, "gpu_driven_ready_material_rows", RuntimeEngine.Rendering.Stats.GpuDriven.ReadyMaterialRows, ref first);
            AppendNumberField(s_lineBuilder, "gpu_driven_non_ready_material_texture_references", RuntimeEngine.Rendering.Stats.GpuDriven.NonReadyMaterialTextureReferences, ref first);
            AppendNumberField(s_lineBuilder, "gpu_driven_invalid_material_ids", RuntimeEngine.Rendering.Stats.GpuDriven.InvalidMaterialIds, ref first);
            AppendNumberField(s_lineBuilder, "gpu_driven_fallback_submitted_material_rows", RuntimeEngine.Rendering.Stats.GpuDriven.FallbackSubmittedMaterialRows, ref first);
            AppendNumberField(s_lineBuilder, "gpu_driven_material_table_publication_generation", RuntimeEngine.Rendering.Stats.GpuDriven.MaterialTablePublicationGeneration, ref first);
            AppendNumberField(s_lineBuilder, "gpu_driven_material_descriptor_publication_generation", RuntimeEngine.Rendering.Stats.GpuDriven.MaterialDescriptorPublicationGeneration, ref first);
            AppendNumberField(s_lineBuilder, "gpu_driven_submission_managed_allocated_bytes", RuntimeEngine.Rendering.Stats.GpuDriven.SubmissionManagedAllocatedBytes, ref first);
            AppendNumberField(s_lineBuilder, "gpu_driven_submission_backend_managed_allocated_bytes", RuntimeEngine.Rendering.Stats.GpuDriven.SubmissionBackendManagedAllocatedBytes, ref first);
            AppendNumberField(s_lineBuilder, "gpu_driven_submission_owned_managed_allocated_bytes", RuntimeEngine.Rendering.Stats.GpuDriven.SubmissionOwnedManagedAllocatedBytes, ref first);
            AppendNumberField(s_lineBuilder, "gpu_driven_validation_capacity_multiplier", GpuDrivenValidationCapacity.Multiplier, ref first);
            AppendNumberField(s_lineBuilder, "gpu_driven_validation_capacity_floor", GpuDrivenValidationCapacity.Floor, ref first);
            AppendNumberField(s_lineBuilder, "gpu_driven_indirect_command_generation_ms", RuntimeEngine.Rendering.Stats.GpuDriven.IndirectCommandGenerationMs, ref first);
            AppendNumberField(s_lineBuilder, "gpu_driven_gpu_cull_ms", RuntimeEngine.Rendering.Stats.GpuDriven.GpuCullMs, ref first);
            AppendNumberField(s_lineBuilder, "gpu_driven_gpu_sort_compact_ms", RuntimeEngine.Rendering.Stats.GpuDriven.GpuSortCompactMs, ref first);
            AppendNumberField(s_lineBuilder, "gpu_driven_delayed_draw_count_buffer_value", RuntimeEngine.Rendering.Stats.GpuDriven.DelayedDrawCountBufferValue, ref first);
            AppendNumberField(s_lineBuilder, "gpu_driven_delayed_diagnostic_readback_bytes", RuntimeEngine.Rendering.Stats.GpuDriven.DelayedDiagnosticReadbackBytes, ref first);
            AppendNumberField(s_lineBuilder, "gpu_driven_delayed_diagnostic_readback_count", RuntimeEngine.Rendering.Stats.GpuDriven.DelayedDiagnosticReadbackCount, ref first);
            AppendStringField(s_lineBuilder, "gpu_material_binding_rung", RuntimeEngine.Rendering.Stats.GpuDriven.MaterialBindingRung, ref first);
            AppendStringField(s_lineBuilder, "gpu_material_binding_rung_reason", RuntimeEngine.Rendering.Stats.GpuDriven.MaterialBindingRungReason, ref first);
            AppendStringField(s_lineBuilder, "gpu_compaction_rung", RuntimeEngine.Rendering.Stats.GpuDriven.GpuCompactionRung, ref first);
            AppendStringField(s_lineBuilder, "gpu_compaction_rung_reason", RuntimeEngine.Rendering.Stats.GpuDriven.GpuCompactionRungReason, ref first);
            AppendNumberField(s_lineBuilder, "gpu_compaction_overflow", RuntimeEngine.Rendering.Stats.GpuDriven.GpuCompactionOverflow, ref first);
            AppendNumberField(s_lineBuilder, "gpu_active_list_overflow", RuntimeEngine.Rendering.Stats.GpuDriven.ActiveListOverflow, ref first);
            AppendNumberField(s_lineBuilder, "gpu_bucket_overflow", RuntimeEngine.Rendering.Stats.GpuDriven.BucketOverflow, ref first);
            AppendNumberField(s_lineBuilder, "gpu_meshlet_overflow", RuntimeEngine.Rendering.Stats.GpuDriven.MeshletOverflow, ref first);
            AppendStringField(s_lineBuilder, "gpu_hiz_mode", RuntimeEngine.Rendering.Stats.GpuDriven.HiZMode, ref first);
            AppendNumberField(s_lineBuilder, "gpu_hiz_one_phase_frames", RuntimeEngine.Rendering.Stats.GpuDriven.HiZOnePhaseFrames, ref first);
            AppendNumberField(s_lineBuilder, "gpu_hiz_two_phase_frames", RuntimeEngine.Rendering.Stats.GpuDriven.HiZTwoPhaseFrames, ref first);
            AppendNumberField(s_lineBuilder, "gpu_hiz_phase_one_draws", RuntimeEngine.Rendering.Stats.GpuDriven.HiZPhaseOneDraws, ref first);
            AppendNumberField(s_lineBuilder, "gpu_hiz_phase_two_draws", RuntimeEngine.Rendering.Stats.GpuDriven.HiZPhaseTwoDraws, ref first);
            AppendNumberField(s_lineBuilder, "visibility_pass_draws", RuntimeEngine.Rendering.Stats.GpuDriven.VisibilityPassDraws, ref first);
            AppendNumberField(s_lineBuilder, "visibility_classified_pixels", RuntimeEngine.Rendering.Stats.GpuDriven.VisibilityClassifiedPixels, ref first);
            AppendNumberField(s_lineBuilder, "visibility_active_material_tiles", RuntimeEngine.Rendering.Stats.GpuDriven.VisibilityActiveMaterialTiles, ref first);
            AppendNumberField(s_lineBuilder, "visibility_classification_overflow", RuntimeEngine.Rendering.Stats.GpuDriven.VisibilityClassificationOverflow, ref first);
            AppendNumberField(s_lineBuilder, "visibility_reconstruction_ms", RuntimeEngine.Rendering.Stats.GpuDriven.VisibilityReconstructionMs, ref first);
            AppendNumberField(s_lineBuilder, "visibility_material_shading_ms", RuntimeEngine.Rendering.Stats.GpuDriven.VisibilityMaterialShadingMs, ref first);
            AppendNumberField(s_lineBuilder, "gpu_cpu_fallback_events", RuntimeEngine.Rendering.Stats.GpuFallback.GpuCpuFallbackEvents, ref first);
            AppendNumberField(s_lineBuilder, "gpu_cpu_fallback_recovered_commands", RuntimeEngine.Rendering.Stats.GpuFallback.GpuCpuFallbackRecoveredCommands, ref first);
            AppendNumberField(s_lineBuilder, "forbidden_gpu_fallback_events", RuntimeEngine.Rendering.Stats.GpuFallback.ForbiddenGpuFallbackEvents, ref first);
            AppendNumberField(s_lineBuilder, "gpu_meshlet_requested_frames", RuntimeEngine.Rendering.Stats.GpuMeshlets.GpuMeshletRequestedFrames, ref first);
            AppendNumberField(s_lineBuilder, "gpu_meshlet_production_frames", RuntimeEngine.Rendering.Stats.GpuMeshlets.GpuMeshletProductionFrames, ref first);
            AppendNumberField(s_lineBuilder, "gpu_meshlet_fallback_frames", RuntimeEngine.Rendering.Stats.GpuMeshlets.GpuMeshletFallbackFrames, ref first);
            AppendNumberField(s_lineBuilder, "gpu_meshlet_dispatch_skipped", RuntimeEngine.Rendering.Stats.GpuMeshlets.GpuMeshletDispatchSkipped, ref first);
            AppendNumberField(s_lineBuilder, "gpu_meshlet_task_records_emitted", RuntimeEngine.Rendering.Stats.GpuMeshlets.GpuMeshletTaskRecordsEmitted, ref first);
            AppendNumberField(s_lineBuilder, "gpu_meshlet_task_records_frustum_culled", RuntimeEngine.Rendering.Stats.GpuMeshlets.GpuMeshletTaskRecordsFrustumCulled, ref first);
            AppendNumberField(s_lineBuilder, "gpu_meshlet_task_records_cone_culled", RuntimeEngine.Rendering.Stats.GpuMeshlets.GpuMeshletTaskRecordsConeCulled, ref first);
            AppendNumberField(s_lineBuilder, "gpu_meshlet_task_records_hiz_culled", RuntimeEngine.Rendering.Stats.GpuMeshlets.GpuMeshletTaskRecordsHiZCulled, ref first);
            AppendNumberField(s_lineBuilder, "gpu_meshlet_expansion_overflow_count", RuntimeEngine.Rendering.Stats.GpuMeshlets.GpuMeshletExpansionOverflowCount, ref first);
            AppendNumberField(s_lineBuilder, "gpu_meshlet_buffer_bytes_resident", RuntimeEngine.Rendering.Stats.GpuMeshlets.GpuMeshletBufferBytesResident, ref first);
            AppendNumberField(s_lineBuilder, "gpu_meshlet_last_visible_meshlet_count", RuntimeEngine.Rendering.Stats.GpuMeshlets.LastVisibleMeshletCount, ref first);
            AppendNumberField(s_lineBuilder, "gpu_meshlet_last_dispatched_meshlet_count", RuntimeEngine.Rendering.Stats.GpuMeshlets.LastDispatchedMeshletCount, ref first);
            AppendNumberField(s_lineBuilder, "gpu_meshlet_last_task_record_overflow_count", RuntimeEngine.Rendering.Stats.GpuMeshlets.LastTaskRecordOverflowCount, ref first);
            AppendNumberField(s_lineBuilder, "gpu_meshlet_last_dispatch_ms", RuntimeEngine.Rendering.Stats.GpuMeshlets.LastDispatchTime.TotalMilliseconds, ref first);
            AppendNumberField(s_lineBuilder, "gpu_meshlet_last_readback_bytes", RuntimeEngine.Rendering.Stats.GpuMeshlets.LastReadbackBytes, ref first);
            AppendNumberField(s_lineBuilder, "gpu_meshlet_cache_hits", RuntimeEngine.Rendering.Stats.GpuMeshlets.GpuMeshletCacheHits, ref first);
            AppendNumberField(s_lineBuilder, "gpu_meshlet_cache_misses", RuntimeEngine.Rendering.Stats.GpuMeshlets.GpuMeshletCacheMisses, ref first);
            AppendNumberField(s_lineBuilder, "gpu_meshlet_cache_stale", RuntimeEngine.Rendering.Stats.GpuMeshlets.GpuMeshletCacheStale, ref first);
            // Lifetime meshlet cook evidence is deliberately emitted alongside every
            // sample: cold import completes before the steady-state capture window.
            AppendNumberField(s_lineBuilder, "meshlet_cold_import_builder_calls", RuntimeEngine.Rendering.Stats.GpuMeshlets.ColdImportBuilderCalls, ref first);
            AppendNumberField(s_lineBuilder, "meshlet_cold_import_build_ms", RuntimeEngine.Rendering.Stats.GpuMeshlets.ColdImportBuildTime.TotalMilliseconds, ref first);
            AppendNumberField(s_lineBuilder, "meshlet_cold_import_allocated_bytes", RuntimeEngine.Rendering.Stats.GpuMeshlets.ColdImportAllocatedBytes, ref first);
            AppendNumberField(s_lineBuilder, "meshlet_generated_lod_count", RuntimeEngine.Rendering.Stats.GpuMeshlets.GeneratedLodCount, ref first);
            AppendNumberField(s_lineBuilder, "meshlet_cooked_payload_count", RuntimeEngine.Rendering.Stats.GpuMeshlets.CookedPayloadCount, ref first);
            AppendNumberField(s_lineBuilder, "meshlet_cooked_meshlet_count", RuntimeEngine.Rendering.Stats.GpuMeshlets.CookedMeshletCount, ref first);
            AppendNumberField(s_lineBuilder, "meshlet_source_parser_calls", RuntimeEngine.Rendering.Stats.GpuMeshlets.SourceParserCalls, ref first);
            AppendNumberField(s_lineBuilder, "meshlet_warm_payload_hydrations", RuntimeEngine.Rendering.Stats.GpuMeshlets.WarmPayloadHydrations, ref first);
            AppendNumberField(s_lineBuilder, "meshlet_render_path_source_hash_calls", RuntimeEngine.Rendering.Stats.GpuMeshlets.RenderPathSourceHashCalls, ref first);
            AppendNumberField(s_lineBuilder, "meshlet_render_path_disk_calls", RuntimeEngine.Rendering.Stats.GpuMeshlets.RenderPathDiskCalls, ref first);
            AppendNumberField(s_lineBuilder, "meshlet_render_path_cooker_calls", RuntimeEngine.Rendering.Stats.GpuMeshlets.RenderPathCookerCalls, ref first);
            AppendStringField(s_lineBuilder, "meshlet_requested_submission", RuntimeEngine.Rendering.Stats.GpuMeshlets.RequestedSubmission, ref first);
            AppendStringField(s_lineBuilder, "meshlet_primitive_preference", RuntimeEngine.Rendering.Stats.GpuMeshlets.PrimitivePreference, ref first);
            AppendStringField(s_lineBuilder, "meshlet_resolved_pass", RuntimeEngine.Rendering.Stats.GpuMeshlets.ResolvedPass, ref first);
            AppendStringField(s_lineBuilder, "meshlet_resolved_route", RuntimeEngine.Rendering.Stats.GpuMeshlets.ResolvedRoute, ref first);
            AppendStringField(s_lineBuilder, "meshlet_primary_route_reason", RuntimeEngine.Rendering.Stats.GpuMeshlets.PrimaryRouteReason, ref first);
            AppendStringField(s_lineBuilder, "meshlet_last_post_seal_failure_pass", RuntimeEngine.Rendering.Stats.GpuMeshlets.LastPostSealFailurePass, ref first);
            AppendStringField(s_lineBuilder, "meshlet_last_post_seal_failure_reason", RuntimeEngine.Rendering.Stats.GpuMeshlets.LastPostSealFailureReason, ref first);
            AppendStringField(s_lineBuilder, "meshlet_eligible_pass_pre_seal_reason", RuntimeEngine.Rendering.Stats.GpuMeshlets.EligiblePassPreSealReason, ref first);
            AppendNumberField(s_lineBuilder, "meshlet_resolved_meshlet_rows", RuntimeEngine.Rendering.Stats.GpuMeshlets.ResolvedMeshletRows, ref first);
            AppendNumberField(s_lineBuilder, "meshlet_resolved_task_groups", RuntimeEngine.Rendering.Stats.GpuMeshlets.ResolvedTaskGroups, ref first);
            AppendNumberField(s_lineBuilder, "meshlet_buffer_live_bytes", RuntimeEngine.Rendering.Stats.GpuMeshlets.BufferLiveBytes, ref first);
            AppendNumberField(s_lineBuilder, "meshlet_buffer_retired_bytes", RuntimeEngine.Rendering.Stats.GpuMeshlets.BufferRetiredBytes, ref first);
            AppendNumberField(s_lineBuilder, "meshlet_buffer_rebuild_count", RuntimeEngine.Rendering.Stats.GpuMeshlets.BufferRebuildCount, ref first);
            AppendNumberField(s_lineBuilder, "meshlet_buffer_retire_count", RuntimeEngine.Rendering.Stats.GpuMeshlets.BufferRetireCount, ref first);
            AppendNumberField(s_lineBuilder, "meshlet_dispatch_calls", RuntimeEngine.Rendering.Stats.GpuMeshlets.DispatchCallCount, ref first);
            AppendNumberField(s_lineBuilder, "meshlet_dispatch_groups", RuntimeEngine.Rendering.Stats.GpuMeshlets.DispatchGroupCount, ref first);
            AppendNumberField(s_lineBuilder, "gpu_meshlet_delayed_dispatch_group_count", RuntimeEngine.Rendering.Stats.GpuMeshlets.DelayedDispatchGroupCount, ref first);
            AppendNumberField(s_lineBuilder, "gpu_meshlet_diagnostic_readback_bytes", RuntimeEngine.Rendering.Stats.GpuMeshlets.DiagnosticReadbackBytes, ref first);
            AppendNumberField(s_lineBuilder, "meshlet_mapped_bytes", RuntimeEngine.Rendering.Stats.GpuMeshlets.MappedBytes, ref first);
            AppendStringField(s_lineBuilder, "meshlet_vulkan_capability_ladder", RuntimeEngine.Rendering.Stats.GpuMeshlets.VulkanCapabilityLadder, ref first);
            AppendStringField(s_lineBuilder, "meshlet_vulkan_capability_failed_rung", RuntimeEngine.Rendering.Stats.GpuMeshlets.VulkanCapabilityFailedRung, ref first);
            AppendNumberField(s_lineBuilder, "fbo_bind_count", RuntimeEngine.Rendering.Stats.Vram.FBOBindCount, ref first);
            AppendNumberField(s_lineBuilder, "fbo_bandwidth_bytes", RuntimeEngine.Rendering.Stats.Vram.FBOBandwidthBytes, ref first);
            AppendNumberField(s_lineBuilder, "allocated_vram_bytes", RuntimeEngine.Rendering.Stats.Vram.AllocatedVRAMBytes, ref first);

            AppendBoolField(s_lineBuilder, "gpu_pipeline_profiling_enabled", RuntimeEngine.Rendering.Stats.GpuPipelineProfiler.GpuRenderPipelineProfilingEnabled, ref first);
            AppendBoolField(s_lineBuilder, "gpu_pipeline_profiling_supported", RuntimeEngine.Rendering.Stats.GpuPipelineProfiler.GpuRenderPipelineProfilingSupported, ref first);
            AppendBoolField(s_lineBuilder, "gpu_pipeline_timings_ready", gpuTimingsReady, ref first);
            AppendStringField(s_lineBuilder, "gpu_pipeline_backend", RuntimeEngine.Rendering.Stats.GpuPipelineProfiler.GpuRenderPipelineBackend, ref first);
            AppendStringField(s_lineBuilder, "gpu_pipeline_status", RuntimeEngine.Rendering.Stats.GpuPipelineProfiler.GpuRenderPipelineStatusMessage, ref first);
            AppendNumberField(s_lineBuilder, "gpu_pipeline_frame_ms", gpuPipelineMs, ref first);

            AppendNumberField(s_lineBuilder, "vulkan_indirect_api_calls", RuntimeEngine.Rendering.Stats.Vulkan.VulkanIndirectApiCalls, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_indirect_submitted_draws", RuntimeEngine.Rendering.Stats.Vulkan.VulkanIndirectSubmittedDraws, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_requested_draws", RuntimeEngine.Rendering.Stats.Vulkan.VulkanRequestedDraws, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_consumed_draws", RuntimeEngine.Rendering.Stats.Vulkan.VulkanConsumedDraws, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_oom_fallback_count", RuntimeEngine.Rendering.Stats.Vulkan.VulkanOomFallbackCount, ref first);
            VulkanFrameTelemetryPublication vulkanFrame = RuntimeEngine.Rendering.Stats.Vulkan.LatestVulkanFrameTelemetry;
            AppendNumberField(s_lineBuilder, "vulkan_frame_authority_id", vulkanFrame.AuthorityId, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_frame_publication_sequence", vulkanFrame.PublicationSequence, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_frame_engine_frame_number", vulkanFrame.Identity.EngineFrameNumber, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_frame_render_frame_number", vulkanFrame.Identity.RenderFrameNumber, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_frame_slot", vulkanFrame.Identity.FrameSlot, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_frame_output_index", vulkanFrame.Identity.Output.OutputIndex, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_frame_output_generation", vulkanFrame.Identity.Output.OutputGeneration, ref first);
            AppendStringField(s_lineBuilder, "vulkan_frame_outcome", vulkanFrame.Outcome.ToString(), ref first);
            AppendNumberField(s_lineBuilder, "vulkan_frame_total_ms", vulkanFrame.TotalElapsed.TotalMilliseconds, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_frame_gpu_command_buffer_ms", RuntimeEngine.Rendering.Stats.Vulkan.VulkanFrameGpuCommandBufferMs, ref first);
            AppendVulkanFrameStageFields(s_lineBuilder, "frame_pacing", vulkanFrame.FramePacing, ref first);
            AppendVulkanFrameStageFields(s_lineBuilder, "snapshot_handoff", vulkanFrame.SnapshotHandoff, ref first);
            AppendVulkanFrameStageFields(s_lineBuilder, "completion_maintenance", vulkanFrame.CompletionMaintenance, ref first);
            AppendVulkanFrameStageFields(s_lineBuilder, "output_acquire", vulkanFrame.OutputAcquire, ref first);
            AppendVulkanFrameStageFields(s_lineBuilder, "plan_build", vulkanFrame.PlanBuild, ref first);
            AppendVulkanFrameStageFields(s_lineBuilder, "resource_prepare", vulkanFrame.ResourcePrepare, ref first);
            AppendVulkanFrameStageFields(s_lineBuilder, "work_schedule", vulkanFrame.WorkSchedule, ref first);
            AppendVulkanFrameStageFields(s_lineBuilder, "command_record", vulkanFrame.CommandRecord, ref first);
            AppendVulkanFrameStageFields(s_lineBuilder, "submit_prepare", vulkanFrame.SubmitPrepare, ref first);
            AppendVulkanFrameStageFields(s_lineBuilder, "queue_submit", vulkanFrame.QueueSubmit, ref first);
            AppendVulkanFrameStageFields(s_lineBuilder, "output_complete", vulkanFrame.OutputComplete, ref first);
            AppendVulkanFrameStageFields(s_lineBuilder, "frame_settlement", vulkanFrame.FrameSettlement, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_frame_wait_fence_ms", vulkanFrame.Detail.WaitFrameSlot.TotalMilliseconds, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_frame_sample_timing_queries_ms", vulkanFrame.Detail.SampleTimingQueries.TotalMilliseconds, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_frame_drain_retired_resources_ms", vulkanFrame.Detail.DrainRetiredResources.TotalMilliseconds, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_frame_acquire_image_ms", vulkanFrame.Detail.AcquireImage.TotalMilliseconds, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_frame_acquire_bridge_submit_ms", vulkanFrame.Detail.AcquireBridgeSubmit.TotalMilliseconds, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_frame_wait_swapchain_image_ms", vulkanFrame.Detail.WaitSwapchainImage.TotalMilliseconds, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_frame_reset_dynamic_uniform_ring_ms", vulkanFrame.Detail.ResetDynamicUniformRing.TotalMilliseconds, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_frame_record_command_buffer_ms", vulkanFrame.Detail.RecordCommandBuffer.TotalMilliseconds, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_frame_snapshot_imgui_overlay_ms", vulkanFrame.Detail.SnapshotImGuiOverlay.TotalMilliseconds, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_frame_record_scene_command_buffer_ms", vulkanFrame.Detail.RecordSceneCommandBuffer.TotalMilliseconds, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_frame_record_imgui_overlay_ms", vulkanFrame.Detail.RecordImGuiOverlay.TotalMilliseconds, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_frame_record_dynamic_ui_text_overlay_ms", vulkanFrame.Detail.RecordDynamicUiTextOverlay.TotalMilliseconds, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_frame_submit_ms", vulkanFrame.Detail.SubmitQueue.TotalMilliseconds, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_frame_trim_ms", vulkanFrame.Detail.TrimStaging.TotalMilliseconds, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_frame_present_ms", vulkanFrame.Detail.PresentQueue.TotalMilliseconds, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_frame_op_total_count", RuntimeEngine.Rendering.Stats.Vulkan.VulkanFrameOpTotalCount, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_frame_op_clear_count", RuntimeEngine.Rendering.Stats.Vulkan.VulkanFrameOpClearCount, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_frame_op_mesh_draw_count", RuntimeEngine.Rendering.Stats.Vulkan.VulkanFrameOpMeshDrawCount, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_frame_op_indirect_draw_count", RuntimeEngine.Rendering.Stats.Vulkan.VulkanFrameOpIndirectDrawCount, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_frame_op_mesh_task_dispatch_count", RuntimeEngine.Rendering.Stats.Vulkan.VulkanFrameOpMeshTaskDispatchCount, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_frame_op_blit_count", RuntimeEngine.Rendering.Stats.Vulkan.VulkanFrameOpBlitCount, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_frame_op_compute_count", RuntimeEngine.Rendering.Stats.Vulkan.VulkanFrameOpComputeCount, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_frame_op_swapchain_write_count", RuntimeEngine.Rendering.Stats.Vulkan.VulkanFrameOpSwapchainWriteCount, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_frame_op_fbo_write_count", RuntimeEngine.Rendering.Stats.Vulkan.VulkanFrameOpFboWriteCount, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_frame_op_unique_pass_count", RuntimeEngine.Rendering.Stats.Vulkan.VulkanFrameOpUniquePassCount, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_frame_op_unique_context_count", RuntimeEngine.Rendering.Stats.Vulkan.VulkanFrameOpUniqueContextCount, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_frame_op_unique_target_count", RuntimeEngine.Rendering.Stats.Vulkan.VulkanFrameOpUniqueTargetCount, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_material_payload_cache_hits", RuntimeEngine.Rendering.Stats.Vulkan.VulkanMaterialPayloadCacheHits, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_material_payload_cache_misses", RuntimeEngine.Rendering.Stats.Vulkan.VulkanMaterialPayloadCacheMisses, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_material_payloads_packed", RuntimeEngine.Rendering.Stats.Vulkan.VulkanMaterialPayloadsPacked, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_material_uniforms_packed", RuntimeEngine.Rendering.Stats.Vulkan.VulkanMaterialUniformsPacked, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_material_parameter_emissions", RuntimeEngine.Rendering.Stats.Vulkan.VulkanMaterialParameterEmissions, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_material_dictionary_writes", RuntimeEngine.Rendering.Stats.Vulkan.VulkanMaterialDictionaryWrites, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_frame_material_snapshot_cache_hits", RuntimeEngine.Rendering.Stats.Vulkan.VulkanFrameMaterialSnapshotCacheHits, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_frame_material_snapshot_cache_misses", RuntimeEngine.Rendering.Stats.Vulkan.VulkanFrameMaterialSnapshotCacheMisses, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_binding_snapshots_captured", RuntimeEngine.Rendering.Stats.Vulkan.VulkanBindingSnapshotsCaptured, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_binding_snapshot_entries", RuntimeEngine.Rendering.Stats.Vulkan.VulkanBindingSnapshotEntries, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_fast_path_binding_snapshots", RuntimeEngine.Rendering.Stats.Vulkan.VulkanFastPathBindingSnapshots, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_legacy_binding_snapshots", RuntimeEngine.Rendering.Stats.Vulkan.VulkanLegacyBindingSnapshots, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_auto_uniform_plan_cache_hits", RuntimeEngine.Rendering.Stats.Vulkan.VulkanAutoUniformPlanCacheHits, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_auto_uniform_plan_cache_misses", RuntimeEngine.Rendering.Stats.Vulkan.VulkanAutoUniformPlanCacheMisses, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_auto_uniform_static_bytes_copied", RuntimeEngine.Rendering.Stats.Vulkan.VulkanAutoUniformStaticBytesCopied, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_auto_uniform_dynamic_bytes_cleared", RuntimeEngine.Rendering.Stats.Vulkan.VulkanAutoUniformDynamicBytesCleared, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_auto_uniform_dynamic_members_patched", RuntimeEngine.Rendering.Stats.Vulkan.VulkanAutoUniformDynamicMembersPatched, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_auto_uniform_reflected_members_scanned", RuntimeEngine.Rendering.Stats.Vulkan.VulkanAutoUniformReflectedMembersScanned, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_auto_uniform_legacy_full_block_bytes", RuntimeEngine.Rendering.Stats.Vulkan.VulkanAutoUniformLegacyFullBlockBytes, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_auto_uniform_fast_path_draws", RuntimeEngine.Rendering.Stats.Vulkan.VulkanAutoUniformFastPathDraws, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_auto_uniform_legacy_fallback_draws", RuntimeEngine.Rendering.Stats.Vulkan.VulkanAutoUniformLegacyFallbackDraws, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_frame_data_draws_visited", RuntimeEngine.Rendering.Stats.Vulkan.VulkanFrameDataDrawsVisited, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_descriptor_records_validated", RuntimeEngine.Rendering.Stats.Vulkan.VulkanDescriptorRecordsValidated, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_descriptor_records_written", RuntimeEngine.Rendering.Stats.Vulkan.VulkanDescriptorRecordsWritten, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_binding_schemas_compiled", RuntimeEngine.Rendering.Stats.Vulkan.VulkanBindingSchemasCompiled, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_binding_schema_value_operations", RuntimeEngine.Rendering.Stats.Vulkan.VulkanBindingSchemaValueOperations, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_binding_schema_descriptor_entries", RuntimeEngine.Rendering.Stats.Vulkan.VulkanBindingSchemaDescriptorEntries, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_binding_schema_fallback_operations", RuntimeEngine.Rendering.Stats.Vulkan.VulkanBindingSchemaFallbackOperations, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_auto_uniform_typed_operations_executed", RuntimeEngine.Rendering.Stats.Vulkan.VulkanAutoUniformTypedOperationsExecuted, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_auto_uniform_reflected_name_lookups", RuntimeEngine.Rendering.Stats.Vulkan.VulkanAutoUniformReflectedNameLookups, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_auto_uniform_generic_conversions", RuntimeEngine.Rendering.Stats.Vulkan.VulkanAutoUniformGenericConversions, ref first);
            AppendVulkanFrequencyPublicationFields(s_lineBuilder, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_auto_uniform_fallback_binding_snapshot_ineligible", RuntimeEngine.Rendering.Stats.Vulkan.GetVulkanAutoUniformFallbackReasonCount(EVulkanAutoUniformFallbackReason.BindingSnapshotIneligible), ref first);
            AppendNumberField(s_lineBuilder, "vulkan_auto_uniform_fallback_program_unavailable", RuntimeEngine.Rendering.Stats.Vulkan.GetVulkanAutoUniformFallbackReasonCount(EVulkanAutoUniformFallbackReason.ProgramUnavailable), ref first);
            AppendNumberField(s_lineBuilder, "vulkan_auto_uniform_fallback_invalid_buffer_size", RuntimeEngine.Rendering.Stats.Vulkan.GetVulkanAutoUniformFallbackReasonCount(EVulkanAutoUniformFallbackReason.InvalidBufferSize), ref first);
            AppendNumberField(s_lineBuilder, "vulkan_auto_uniform_fallback_binding_schema_unavailable", RuntimeEngine.Rendering.Stats.Vulkan.GetVulkanAutoUniformFallbackReasonCount(EVulkanAutoUniformFallbackReason.BindingSchemaUnavailable), ref first);
            AppendNumberField(s_lineBuilder, "vulkan_auto_uniform_fallback_binding_schema_mismatch", RuntimeEngine.Rendering.Stats.Vulkan.GetVulkanAutoUniformFallbackReasonCount(EVulkanAutoUniformFallbackReason.BindingSchemaMismatch), ref first);
            AppendNumberField(s_lineBuilder, "vulkan_auto_uniform_fallback_invalid_member_name", RuntimeEngine.Rendering.Stats.Vulkan.GetVulkanAutoUniformFallbackReasonCount(EVulkanAutoUniformFallbackReason.InvalidMemberName), ref first);
            AppendNumberField(s_lineBuilder, "vulkan_auto_uniform_fallback_unsupported_shader_type", RuntimeEngine.Rendering.Stats.Vulkan.GetVulkanAutoUniformFallbackReasonCount(EVulkanAutoUniformFallbackReason.UnsupportedShaderType), ref first);
            AppendNumberField(s_lineBuilder, "vulkan_auto_uniform_fallback_invalid_destination_range", RuntimeEngine.Rendering.Stats.Vulkan.GetVulkanAutoUniformFallbackReasonCount(EVulkanAutoUniformFallbackReason.InvalidDestinationRange), ref first);
            AppendNumberField(s_lineBuilder, "vulkan_auto_uniform_fallback_invalid_array_layout", RuntimeEngine.Rendering.Stats.Vulkan.GetVulkanAutoUniformFallbackReasonCount(EVulkanAutoUniformFallbackReason.InvalidArrayLayout), ref first);
            AppendNumberField(s_lineBuilder, "vulkan_auto_uniform_fallback_struct_snapshot_required", RuntimeEngine.Rendering.Stats.Vulkan.GetVulkanAutoUniformFallbackReasonCount(EVulkanAutoUniformFallbackReason.StructSnapshotRequired), ref first);
            AppendNumberField(s_lineBuilder, "vulkan_auto_uniform_fallback_engine_source_type_mismatch", RuntimeEngine.Rendering.Stats.Vulkan.GetVulkanAutoUniformFallbackReasonCount(EVulkanAutoUniformFallbackReason.EngineSourceTypeMismatch), ref first);
            AppendNumberField(s_lineBuilder, "vulkan_auto_uniform_fallback_mesh_state_source_type_mismatch", RuntimeEngine.Rendering.Stats.Vulkan.GetVulkanAutoUniformFallbackReasonCount(EVulkanAutoUniformFallbackReason.MeshStateSourceTypeMismatch), ref first);
            AppendNumberField(s_lineBuilder, "vulkan_auto_uniform_fallback_typed_engine_source_unavailable", RuntimeEngine.Rendering.Stats.Vulkan.GetVulkanAutoUniformFallbackReasonCount(EVulkanAutoUniformFallbackReason.TypedEngineSourceUnavailable), ref first);
            AppendNumberField(s_lineBuilder, "vulkan_auto_uniform_fallback_typed_engine_write_failed", RuntimeEngine.Rendering.Stats.Vulkan.GetVulkanAutoUniformFallbackReasonCount(EVulkanAutoUniformFallbackReason.TypedEngineWriteFailed), ref first);
            AppendNumberField(s_lineBuilder, "vulkan_auto_uniform_fallback_typed_temporal_write_failed", RuntimeEngine.Rendering.Stats.Vulkan.GetVulkanAutoUniformFallbackReasonCount(EVulkanAutoUniformFallbackReason.TypedTemporalWriteFailed), ref first);
            AppendNumberField(s_lineBuilder, "vulkan_auto_uniform_fallback_typed_mesh_state_source_unavailable", RuntimeEngine.Rendering.Stats.Vulkan.GetVulkanAutoUniformFallbackReasonCount(EVulkanAutoUniformFallbackReason.TypedMeshStateSourceUnavailable), ref first);
            AppendNumberField(s_lineBuilder, "vulkan_auto_uniform_fallback_typed_mesh_state_write_failed", RuntimeEngine.Rendering.Stats.Vulkan.GetVulkanAutoUniformFallbackReasonCount(EVulkanAutoUniformFallbackReason.TypedMeshStateWriteFailed), ref first);
            AppendNumberField(s_lineBuilder, "vulkan_auto_uniform_fallback_typed_material_or_runtime_write_failed", RuntimeEngine.Rendering.Stats.Vulkan.GetVulkanAutoUniformFallbackReasonCount(EVulkanAutoUniformFallbackReason.TypedMaterialOrRuntimeWriteFailed), ref first);
            AppendNumberField(s_lineBuilder, "vulkan_command_buffer_clean_reuse_count", RuntimeEngine.Rendering.Stats.Vulkan.VulkanCommandBufferCleanReuseCount, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_command_buffer_record_count", RuntimeEngine.Rendering.Stats.Vulkan.VulkanCommandBufferRecordCount, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_command_buffer_forced_dirty_count", RuntimeEngine.Rendering.Stats.Vulkan.VulkanCommandBufferForcedDirtyCount, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_command_buffer_frame_op_signature_dirty_count", RuntimeEngine.Rendering.Stats.Vulkan.VulkanCommandBufferFrameOpSignatureDirtyCount, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_command_buffer_planner_dirty_count", RuntimeEngine.Rendering.Stats.Vulkan.VulkanCommandBufferPlannerDirtyCount, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_command_buffer_profiler_dirty_count", RuntimeEngine.Rendering.Stats.Vulkan.VulkanCommandBufferProfilerDirtyCount, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_command_buffer_decision_reason_mask", (int)RuntimeEngine.Rendering.Stats.Vulkan.VulkanCommandBufferDecisionReasonMask, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_command_buffer_decision_visibility_generation", RuntimeEngine.Rendering.Stats.Vulkan.VulkanCommandBufferDecisionVisibilityGeneration, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_command_buffer_decision_structural_signature", RuntimeEngine.Rendering.Stats.Vulkan.VulkanCommandBufferDecisionStructuralSignature, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_command_buffer_decision_descriptor_generation", RuntimeEngine.Rendering.Stats.Vulkan.VulkanCommandBufferDecisionDescriptorGeneration, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_command_buffer_decision_swapchain_slot", RuntimeEngine.Rendering.Stats.Vulkan.VulkanCommandBufferDecisionSwapchainSlot, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_primary_entry_state_mismatch", (int)RuntimeEngine.Rendering.Stats.Vulkan.VulkanPrimaryEntryStateMismatch, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_primary_entry_state_image_handle", RuntimeEngine.Rendering.Stats.Vulkan.VulkanPrimaryEntryStateImageHandle, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_primary_entry_state_mip_level", RuntimeEngine.Rendering.Stats.Vulkan.VulkanPrimaryEntryStateMipLevel, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_primary_entry_state_array_layer", RuntimeEngine.Rendering.Stats.Vulkan.VulkanPrimaryEntryStateArrayLayer, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_primary_entry_state_aspect", RuntimeEngine.Rendering.Stats.Vulkan.VulkanPrimaryEntryStateAspect, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_primary_entry_state_expected_layout", RuntimeEngine.Rendering.Stats.Vulkan.VulkanPrimaryEntryStateExpectedLayout, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_primary_entry_state_expected_stage_mask", RuntimeEngine.Rendering.Stats.Vulkan.VulkanPrimaryEntryStateExpectedStageMask, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_primary_entry_state_expected_access_mask", RuntimeEngine.Rendering.Stats.Vulkan.VulkanPrimaryEntryStateExpectedAccessMask, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_primary_entry_state_expected_descriptor_layout", RuntimeEngine.Rendering.Stats.Vulkan.VulkanPrimaryEntryStateExpectedDescriptorLayout, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_primary_entry_state_expected_queue_family", RuntimeEngine.Rendering.Stats.Vulkan.VulkanPrimaryEntryStateExpectedQueueFamily, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_primary_entry_state_expected_resource_generation", RuntimeEngine.Rendering.Stats.Vulkan.VulkanPrimaryEntryStateExpectedResourceGeneration, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_primary_entry_state_actual_layout", RuntimeEngine.Rendering.Stats.Vulkan.VulkanPrimaryEntryStateActualLayout, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_primary_entry_state_actual_stage_mask", RuntimeEngine.Rendering.Stats.Vulkan.VulkanPrimaryEntryStateActualStageMask, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_primary_entry_state_actual_access_mask", RuntimeEngine.Rendering.Stats.Vulkan.VulkanPrimaryEntryStateActualAccessMask, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_primary_entry_state_actual_descriptor_layout", RuntimeEngine.Rendering.Stats.Vulkan.VulkanPrimaryEntryStateActualDescriptorLayout, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_primary_entry_state_actual_queue_family", RuntimeEngine.Rendering.Stats.Vulkan.VulkanPrimaryEntryStateActualQueueFamily, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_primary_entry_state_actual_resource_generation", RuntimeEngine.Rendering.Stats.Vulkan.VulkanPrimaryEntryStateActualResourceGeneration, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_exact_variants_dirtied", RuntimeEngine.Rendering.Stats.Vulkan.VulkanExactVariantsDirtied, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_exact_command_chains_dirtied", RuntimeEngine.Rendering.Stats.Vulkan.VulkanExactCommandChainsDirtied, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_unrelated_variants_preserved", RuntimeEngine.Rendering.Stats.Vulkan.VulkanUnrelatedVariantsPreserved, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_global_fallback_invalidations", RuntimeEngine.Rendering.Stats.Vulkan.VulkanGlobalFallbackInvalidations, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_tracking_dependency_binds", RuntimeEngine.Rendering.Stats.Vulkan.VulkanTrackingDependencyBinds, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_tracking_unique_dependencies", RuntimeEngine.Rendering.Stats.Vulkan.VulkanTrackingUniqueDependencies, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_tracking_image_access_writes", RuntimeEngine.Rendering.Stats.Vulkan.VulkanTrackingImageAccessWrites, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_tracking_compact_image_ranges", RuntimeEngine.Rendering.Stats.Vulkan.VulkanTrackingCompactImageRanges, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_descriptor_expansion_cache_hits", RuntimeEngine.Rendering.Stats.Vulkan.VulkanDescriptorExpansionCacheHits, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_descriptor_expansion_cache_misses", RuntimeEngine.Rendering.Stats.Vulkan.VulkanDescriptorExpansionCacheMisses, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_lifetime_lock_contentions", RuntimeEngine.Rendering.Stats.Vulkan.VulkanLifetimeLockContentions, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_descriptor_pool_create_count", RuntimeEngine.Rendering.Stats.Vulkan.VulkanDescriptorPoolCreateCount, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_lifetime_live_resource_count", RuntimeEngine.Rendering.Stats.Vulkan.VulkanLifetimeLiveResourceCount, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_tracked_descriptor_set_count", RuntimeEngine.Rendering.Stats.Vulkan.VulkanTrackedDescriptorSetCount, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_lifetime_pending_retirement_count", RuntimeEngine.Rendering.Stats.Vulkan.VulkanLifetimePendingRetirementCount, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_lifetime_oldest_pending_retirement_age_ms", RuntimeEngine.Rendering.Stats.Vulkan.VulkanLifetimeOldestPendingRetirementAgeMilliseconds, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_mesh_frame_data_arena_chunks", RuntimeEngine.Rendering.Stats.Vulkan.VulkanMeshFrameDataArenaChunkCount, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_mesh_frame_data_mapped_bytes", RuntimeEngine.Rendering.Stats.Vulkan.VulkanMeshFrameDataMappedBytes, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_mesh_frame_data_reserved_bytes", RuntimeEngine.Rendering.Stats.Vulkan.VulkanMeshFrameDataReservedBytes, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_mesh_frame_data_reservations", RuntimeEngine.Rendering.Stats.Vulkan.VulkanMeshFrameDataReservationCount, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_mesh_frame_data_generation", RuntimeEngine.Rendering.Stats.Vulkan.VulkanMeshFrameDataGeneration, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_mesh_frame_data_recording_leases", RuntimeEngine.Rendering.Stats.Vulkan.VulkanMeshFrameDataRecordingLeases, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_mesh_frame_data_cached_leases", RuntimeEngine.Rendering.Stats.Vulkan.VulkanMeshFrameDataCachedLeases, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_mesh_frame_data_submitted_leases", RuntimeEngine.Rendering.Stats.Vulkan.VulkanMeshFrameDataSubmittedLeases, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_mesh_frame_data_active_generations", RuntimeEngine.Rendering.Stats.Vulkan.VulkanMeshFrameDataActiveGenerationCount, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_mesh_frame_data_lease_retained_generations", RuntimeEngine.Rendering.Stats.Vulkan.VulkanMeshFrameDataLeaseRetainedGenerationCount, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_mesh_descriptor_allocation_variants", RuntimeEngine.Rendering.Stats.Vulkan.VulkanMeshDescriptorAllocationVariants, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_mesh_descriptor_pools", RuntimeEngine.Rendering.Stats.Vulkan.VulkanMeshDescriptorPools, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_mesh_descriptor_allocated_sets", RuntimeEngine.Rendering.Stats.Vulkan.VulkanMeshDescriptorAllocatedSets, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_mesh_descriptor_reserved_sets", RuntimeEngine.Rendering.Stats.Vulkan.VulkanMeshDescriptorReservedSets, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_mesh_frame_data_arena_chunk_high_water", RuntimeEngine.Rendering.Stats.Vulkan.VulkanMeshFrameDataArenaChunkHighWater, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_mesh_frame_data_mapped_bytes_high_water", RuntimeEngine.Rendering.Stats.Vulkan.VulkanMeshFrameDataMappedBytesHighWater, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_mesh_frame_data_reserved_bytes_high_water", RuntimeEngine.Rendering.Stats.Vulkan.VulkanMeshFrameDataReservedBytesHighWater, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_mesh_frame_data_reservation_high_water", RuntimeEngine.Rendering.Stats.Vulkan.VulkanMeshFrameDataReservationHighWater, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_mesh_frame_data_lease_high_water", RuntimeEngine.Rendering.Stats.Vulkan.VulkanMeshFrameDataLeaseHighWater, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_mesh_descriptor_allocation_variant_high_water", RuntimeEngine.Rendering.Stats.Vulkan.VulkanMeshDescriptorAllocationVariantHighWater, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_mesh_descriptor_pool_high_water", RuntimeEngine.Rendering.Stats.Vulkan.VulkanMeshDescriptorPoolHighWater, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_mesh_descriptor_set_high_water", RuntimeEngine.Rendering.Stats.Vulkan.VulkanMeshDescriptorSetHighWater, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_layout_lock_contentions", RuntimeEngine.Rendering.Stats.Vulkan.VulkanLayoutLockContentions, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_record_command_buffer_allocated_bytes", RuntimeEngine.Rendering.Stats.Vulkan.VulkanRecordCommandBufferAllocatedBytes, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_reset_command_buffer_calls", RuntimeEngine.Rendering.Stats.Vulkan.VulkanResetCommandBufferCalls, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_reset_command_pool_calls", RuntimeEngine.Rendering.Stats.Vulkan.VulkanResetCommandPoolCalls, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_allocate_command_buffer_calls", RuntimeEngine.Rendering.Stats.Vulkan.VulkanAllocateCommandBufferCalls, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_command_buffers_allocated", RuntimeEngine.Rendering.Stats.Vulkan.VulkanCommandBuffersAllocated, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_execute_secondary_command_buffer_calls", RuntimeEngine.Rendering.Stats.Vulkan.VulkanExecuteSecondaryCommandBufferCalls, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_secondary_command_buffers_invoked", RuntimeEngine.Rendering.Stats.Vulkan.VulkanSecondaryCommandBuffersInvoked, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_process_reset_command_buffer_calls", RuntimeEngine.Rendering.Stats.Vulkan.VulkanProcessResetCommandBufferCalls, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_process_reset_command_pool_calls", RuntimeEngine.Rendering.Stats.Vulkan.VulkanProcessResetCommandPoolCalls, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_process_allocate_command_buffer_calls", RuntimeEngine.Rendering.Stats.Vulkan.VulkanProcessAllocateCommandBufferCalls, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_process_command_buffers_allocated", RuntimeEngine.Rendering.Stats.Vulkan.VulkanProcessCommandBuffersAllocated, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_process_execute_secondary_command_buffer_calls", RuntimeEngine.Rendering.Stats.Vulkan.VulkanProcessExecuteSecondaryCommandBufferCalls, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_process_secondary_command_buffers_invoked", RuntimeEngine.Rendering.Stats.Vulkan.VulkanProcessSecondaryCommandBuffersInvoked, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_process_worker_secondary_command_buffer_reset_calls", RuntimeEngine.Rendering.Stats.Vulkan.VulkanProcessWorkerSecondaryCommandBufferResetCalls, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_process_worker_secondary_command_buffer_allocations", RuntimeEngine.Rendering.Stats.Vulkan.VulkanProcessWorkerSecondaryCommandBufferAllocations, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_process_worker_secondary_replacement_allocations", RuntimeEngine.Rendering.Stats.Vulkan.VulkanProcessWorkerSecondaryReplacementAllocations, ref first);
            AppendNumberField(s_lineBuilder, "vr_openxr_eye_primary_record_span_ms", RuntimeEngine.Rendering.Stats.Vr.VrOpenXrEyePrimaryRecordSpanMs, ref first);
            AppendNumberField(s_lineBuilder, "vr_openxr_eye_primary_record_overlap_ms", RuntimeEngine.Rendering.Stats.Vr.VrOpenXrEyePrimaryRecordOverlapMs, ref first);
            AppendNumberField(s_lineBuilder, "vr_openxr_eye_primary_record_overlap_ratio", RuntimeEngine.Rendering.Stats.Vr.VrOpenXrEyePrimaryRecordOverlapRatio, ref first);
            AppendNumberField(s_lineBuilder, "vr_process_openxr_eye_primary_record_samples", RuntimeEngine.Rendering.Stats.Vr.VrProcessOpenXrEyePrimaryRecordSamples, ref first);
            AppendNumberField(s_lineBuilder, "vr_process_openxr_eye_primary_record_span_ms", RuntimeEngine.Rendering.Stats.Vr.VrProcessOpenXrEyePrimaryRecordSpanMs, ref first);
            AppendNumberField(s_lineBuilder, "vr_process_openxr_eye_primary_record_overlap_ms", RuntimeEngine.Rendering.Stats.Vr.VrProcessOpenXrEyePrimaryRecordOverlapMs, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_visible_mesh_draws", RuntimeEngine.Rendering.Stats.Vulkan.VulkanVisibleMeshDraws, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_unique_visible_materials", RuntimeEngine.Rendering.Stats.Vulkan.VulkanUniqueVisibleMaterials, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_prepared_mesh_draws", RuntimeEngine.Rendering.Stats.Vulkan.VulkanPreparedMeshDraws, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_recorded_command_artifact_retirements", RuntimeEngine.Rendering.Stats.Vulkan.VulkanRecordedCommandArtifactRetirements, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_prepared_mesh_operation_cohort_hits", RuntimeEngine.Rendering.Stats.Vulkan.VulkanPreparedMeshOperationCohortHits, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_prepared_mesh_operation_cohort_builds", RuntimeEngine.Rendering.Stats.Vulkan.VulkanPreparedMeshOperationCohortBuilds, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_prepared_mesh_operation_full_materializations", RuntimeEngine.Rendering.Stats.Vulkan.VulkanPreparedMeshOperationFullMaterializations, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_prepared_mesh_operation_reuses", RuntimeEngine.Rendering.Stats.Vulkan.VulkanPreparedMeshOperationReuses, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_prepared_mesh_operation_legacy_hole_materializations", RuntimeEngine.Rendering.Stats.Vulkan.VulkanPreparedMeshOperationLegacyHoleMaterializations, ref first);
            AppendVulkanCpuStageFields(s_lineBuilder, "frame_op_preparation", EVulkanCpuStage.FrameOpPreparation, ref first);
            AppendVulkanCpuStageFields(s_lineBuilder, "resource_planning", EVulkanCpuStage.ResourcePlanning, ref first);
            AppendVulkanCpuStageFields(s_lineBuilder, "frame_data_refresh", EVulkanCpuStage.FrameDataRefresh, ref first);
            AppendVulkanCpuStageFields(s_lineBuilder, "packet_construction", EVulkanCpuStage.PacketConstruction, ref first);
            AppendVulkanCpuStageFields(s_lineBuilder, "primary_recording", EVulkanCpuStage.PrimaryRecording, ref first);
            AppendVulkanCpuStageFields(s_lineBuilder, "secondary_recording", EVulkanCpuStage.SecondaryRecording, ref first);
            AppendVulkanCpuStageFields(s_lineBuilder, "descriptor_publication", EVulkanCpuStage.DescriptorPublication, ref first);
            AppendVulkanCpuStageFields(s_lineBuilder, "submission", EVulkanCpuStage.Submission, ref first);
            AppendVulkanCpuStageFields(s_lineBuilder, "frame_data_manifest", EVulkanCpuStage.FrameDataManifest, ref first);
            AppendVulkanCpuStageFields(s_lineBuilder, "dependency_snapshot", EVulkanCpuStage.DependencySnapshot, ref first);
            AppendVulkanCpuStageFields(s_lineBuilder, "image_layout_snapshot", EVulkanCpuStage.ImageLayoutSnapshot, ref first);
            AppendVulkanCpuStageFields(s_lineBuilder, "command_buffer_reuse", EVulkanCpuStage.CommandBufferReuse, ref first);
            AppendVulkanCpuStageFields(s_lineBuilder, "submission_preparation", EVulkanCpuStage.SubmissionPreparation, ref first);
            AppendVulkanCpuStageFields(s_lineBuilder, "submission_diagnostics", EVulkanCpuStage.SubmissionDiagnostics, ref first);
            AppendVulkanCpuStageFields(s_lineBuilder, "submission_image_state_validation", EVulkanCpuStage.SubmissionImageStateValidation, ref first);
            AppendVulkanCpuStageFields(s_lineBuilder, "submission_resource_lifetime_validation", EVulkanCpuStage.SubmissionResourceLifetimeValidation, ref first);
            AppendVulkanCpuStageFields(s_lineBuilder, "queue_submit", EVulkanCpuStage.QueueSubmit, ref first);
            AppendVulkanCpuStageFields(s_lineBuilder, "submission_publication", EVulkanCpuStage.SubmissionPublication, ref first);
            AppendVulkanCpuStageFields(s_lineBuilder, "command_chain_fast_signature", EVulkanCpuStage.CommandChainFastSignature, ref first);
            AppendVulkanCpuStageFields(s_lineBuilder, "command_chain_packet_lowering", EVulkanCpuStage.CommandChainPacketLowering, ref first);
            AppendVulkanCpuStageFields(s_lineBuilder, "command_chain_schedule_evaluation", EVulkanCpuStage.CommandChainScheduleEvaluation, ref first);
            AppendVulkanCpuStageFields(s_lineBuilder, "command_chain_compatibility_scan", EVulkanCpuStage.CommandChainCompatibilityScan, ref first);
            AppendVulkanCpuStageFields(s_lineBuilder, "command_chain_capacity_planning", EVulkanCpuStage.CommandChainCapacityPlanning, ref first);
            AppendVulkanCpuStageFields(s_lineBuilder, "command_chain_dependency_aggregation", EVulkanCpuStage.CommandChainDependencyAggregation, ref first);
            AppendVulkanCpuStageFields(s_lineBuilder, "command_chain_recorded_key_capture", EVulkanCpuStage.CommandChainRecordedKeyCapture, ref first);
            AppendVulkanCpuStageFields(s_lineBuilder, "scheduled_secondary_run", EVulkanCpuStage.ScheduledSecondaryRun, ref first);
            AppendVulkanCpuStageFields(s_lineBuilder, "scheduled_secondary_preflight", EVulkanCpuStage.ScheduledSecondaryPreflight, ref first);
            AppendVulkanCpuStageFields(s_lineBuilder, "scheduled_secondary_classification", EVulkanCpuStage.ScheduledSecondaryClassification, ref first);
            AppendVulkanCpuStageFields(s_lineBuilder, "primary_encoding_setup", EVulkanCpuStage.PrimaryEncodingSetup, ref first);
            AppendVulkanCpuStageFields(s_lineBuilder, "primary_operation_loop", EVulkanCpuStage.PrimaryOperationLoop, ref first);
            AppendVulkanCpuStageFields(s_lineBuilder, "primary_operation_preparation", EVulkanCpuStage.PrimaryOperationPreparation, ref first);
            AppendVulkanCpuStageFields(s_lineBuilder, "primary_mesh_operation", EVulkanCpuStage.PrimaryMeshOperation, ref first);
            AppendVulkanCpuStageFields(s_lineBuilder, "primary_non_mesh_operation", EVulkanCpuStage.PrimaryNonMeshOperation, ref first);
            AppendVulkanCpuStageFields(s_lineBuilder, "primary_finalization", EVulkanCpuStage.PrimaryFinalization, ref first);
            AppendVulkanCpuStageFields(s_lineBuilder, "primary_end_command_buffer", EVulkanCpuStage.PrimaryEndCommandBuffer, ref first);
            AppendVulkanCpuStageFields(s_lineBuilder, "primary_frame_data_manifest", EVulkanCpuStage.PrimaryFrameDataManifest, ref first);
            AppendVulkanCpuStageFields(s_lineBuilder, "primary_prewarm", EVulkanCpuStage.PrimaryPrewarm, ref first);
            AppendVulkanCpuStageFields(s_lineBuilder, "primary_command_encoding", EVulkanCpuStage.PrimaryCommandEncoding, ref first);
            AppendVulkanCpuStageFields(s_lineBuilder, "prepared_draw_construction", EVulkanCpuStage.PreparedDrawConstruction, ref first);
            AppendVulkanCpuStageFields(s_lineBuilder, "secondary_merge", EVulkanCpuStage.SecondaryMerge, ref first);
            AppendVulkanCpuStageFields(s_lineBuilder, "command_dependency_comparison", EVulkanCpuStage.CommandDependencyComparison, ref first);
            AppendVulkanCpuStageFields(s_lineBuilder, "command_dirty_propagation", EVulkanCpuStage.CommandDirtyPropagation, ref first);
            AppendVulkanCpuStageFields(s_lineBuilder, "command_cache_scanning", EVulkanCpuStage.CommandCacheScanning, ref first);
            AppendVulkanCpuStageFields(s_lineBuilder, "frame_op_drain", EVulkanCpuStage.FrameOpDrain, ref first);
            AppendVulkanCpuStageFields(s_lineBuilder, "raw_mesh_request_drain", EVulkanCpuStage.RawMeshRequestDrain, ref first);
            AppendVulkanCpuStageFields(s_lineBuilder, "frame_op_scheduling", EVulkanCpuStage.FrameOpScheduling, ref first);
            AppendVulkanCpuStageFields(s_lineBuilder, "frame_op_sort", EVulkanCpuStage.FrameOpSort, ref first);
            AppendVulkanCpuStageFields(s_lineBuilder, "frame_op_cohort", EVulkanCpuStage.FrameOpCohort, ref first);
            AppendVulkanCpuStageFields(s_lineBuilder, "prepared_mesh_binding_validation", EVulkanCpuStage.PreparedMeshBindingValidation, ref first);
            AppendVulkanCpuStageFields(s_lineBuilder, "prepared_mesh_hole_materialization", EVulkanCpuStage.PreparedMeshHoleMaterialization, ref first);
            AppendVulkanCpuStageFields(s_lineBuilder, "frame_op_resource_use_lowering", EVulkanCpuStage.FrameOpResourceUseLowering, ref first);
            AppendVulkanCpuStageFields(s_lineBuilder, "frame_op_split", EVulkanCpuStage.FrameOpSplit, ref first);
            AppendVulkanCpuStageFields(s_lineBuilder, "frame_op_signature", EVulkanCpuStage.FrameOpSignature, ref first);
            AppendVulkanCpuStageFields(s_lineBuilder, "frame_op_plan", EVulkanCpuStage.FrameOpPlan, ref first);
            AppendVulkanCpuStageFields(s_lineBuilder, "mesh_draw_publisher_state", EVulkanCpuStage.MeshDrawPublisherState, ref first);
            AppendVulkanCpuStageFields(s_lineBuilder, "mesh_draw_artifact_eligibility", EVulkanCpuStage.MeshDrawArtifactEligibility, ref first);
            AppendVulkanCpuStageFields(s_lineBuilder, "mesh_draw_artifact_lookup", EVulkanCpuStage.MeshDrawArtifactLookup, ref first);
            AppendVulkanCpuStageFields(s_lineBuilder, "mesh_draw_preparation", EVulkanCpuStage.MeshDrawPreparation, ref first);
            AppendVulkanCpuStageFields(s_lineBuilder, "mesh_draw_resource_preparation", EVulkanCpuStage.MeshDrawResourcePreparation, ref first);
            AppendVulkanCpuStageFields(s_lineBuilder, "mesh_draw_binding_preparation", EVulkanCpuStage.MeshDrawBindingPreparation, ref first);
            AppendVulkanCpuStageFields(s_lineBuilder, "mesh_draw_material_bindings", EVulkanCpuStage.MeshDrawMaterialBindings, ref first);
            AppendVulkanCpuStageFields(s_lineBuilder, "mesh_draw_binding_snapshot_copy", EVulkanCpuStage.MeshDrawBindingSnapshotCopy, ref first);
            AppendVulkanCpuStageFields(s_lineBuilder, "mesh_draw_enqueue", EVulkanCpuStage.MeshDrawEnqueue, ref first);
            AppendVulkanCpuStageFields(s_lineBuilder, "frame_data_descriptor_validation", EVulkanCpuStage.FrameDataDescriptorValidation, ref first);
            AppendVulkanCpuStageFields(s_lineBuilder, "frame_data_engine_uniform_upload", EVulkanCpuStage.FrameDataEngineUniformUpload, ref first);
            AppendVulkanCpuStageFields(s_lineBuilder, "frame_data_auto_uniform_upload", EVulkanCpuStage.FrameDataAutoUniformUpload, ref first);
            AppendVulkanCpuStageFields(s_lineBuilder, "queue_lock_acquisition", EVulkanCpuStage.QueueLockAcquisition, ref first);
            AppendVulkanCpuStageFields(s_lineBuilder, "auxiliary_fence_wait", EVulkanCpuStage.AuxiliaryFenceWait, ref first);
            AppendVulkanCpuStageFields(s_lineBuilder, "worker_wait", EVulkanCpuStage.WorkerWait, ref first);
            AppendVulkanCpuStageFields(s_lineBuilder, "context_pass_transitions", EVulkanCpuStage.ContextPassTransitions, ref first);
            AppendVulkanCpuStageFields(s_lineBuilder, "barrier_planning_emission", EVulkanCpuStage.BarrierPlanningEmission, ref first);
            AppendVulkanCpuStageFields(s_lineBuilder, "op_dispatch", EVulkanCpuStage.OpDispatch, ref first);
            AppendStringField(s_lineBuilder, "vulkan_command_buffer_dirty_summary", RuntimeEngine.Rendering.Stats.Vulkan.VulkanCommandBufferDirtySummary, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_command_chains_scheduled", RuntimeEngine.Rendering.Stats.Vulkan.VulkanCommandChainsScheduled, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_command_chains_recorded", RuntimeEngine.Rendering.Stats.Vulkan.VulkanCommandChainsRecorded, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_command_chains_reused", RuntimeEngine.Rendering.Stats.Vulkan.VulkanCommandChainsReused, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_command_chains_frame_data_refreshed", RuntimeEngine.Rendering.Stats.Vulkan.VulkanCommandChainsFrameDataRefreshed, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_volatile_command_chains_recorded", RuntimeEngine.Rendering.Stats.Vulkan.VulkanVolatileCommandChainsRecorded, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_primary_command_buffers_reused", RuntimeEngine.Rendering.Stats.Vulkan.VulkanPrimaryCommandBuffersReused, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_primary_command_buffers_recorded", RuntimeEngine.Rendering.Stats.Vulkan.VulkanPrimaryCommandBuffersRecorded, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_visibility_packet_count", RuntimeEngine.Rendering.Stats.Vulkan.VulkanVisibilityPacketCount, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_render_packet_count", RuntimeEngine.Rendering.Stats.Vulkan.VulkanRenderPacketCount, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_secondary_command_buffer_count", RuntimeEngine.Rendering.Stats.Vulkan.VulkanSecondaryCommandBufferCount, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_indirect_primary_record_ops", RuntimeEngine.Rendering.Stats.Vulkan.VulkanIndirectPrimaryRecordOps, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_indirect_secondary_record_ops", RuntimeEngine.Rendering.Stats.Vulkan.VulkanIndirectSecondaryRecordOps, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_indirect_parallel_secondary_record_ops", RuntimeEngine.Rendering.Stats.Vulkan.VulkanIndirectParallelSecondaryRecordOps, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_indirect_secondary_eligibility", (int)RuntimeEngine.Rendering.Stats.Vulkan.VulkanLastIndirectSecondaryEligibility, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_indirect_secondary_eligible_producer_complete", RuntimeEngine.Rendering.Stats.Vulkan.GetVulkanIndirectSecondaryEligibilityCount(EVulkanIndirectSecondaryEligibility.EligibleProducerComplete), ref first);
            AppendNumberField(s_lineBuilder, "vulkan_indirect_secondary_mutable_current_frame", RuntimeEngine.Rendering.Stats.Vulkan.GetVulkanIndirectSecondaryEligibilityCount(EVulkanIndirectSecondaryEligibility.MutableCurrentFrame), ref first);
            AppendNumberField(s_lineBuilder, "vulkan_indirect_secondary_producer_incomplete", RuntimeEngine.Rendering.Stats.Vulkan.GetVulkanIndirectSecondaryEligibilityCount(EVulkanIndirectSecondaryEligibility.ProducerIncomplete), ref first);
            AppendNumberField(s_lineBuilder, "vulkan_indirect_secondary_buffer_identity_changed", RuntimeEngine.Rendering.Stats.Vulkan.GetVulkanIndirectSecondaryEligibilityCount(EVulkanIndirectSecondaryEligibility.BufferIdentityChanged), ref first);
            AppendNumberField(s_lineBuilder, "vulkan_indirect_secondary_invalid_range", RuntimeEngine.Rendering.Stats.Vulkan.GetVulkanIndirectSecondaryEligibilityCount(EVulkanIndirectSecondaryEligibility.InvalidRange), ref first);
            AppendNumberField(s_lineBuilder, "vulkan_indirect_secondary_command_chains_disabled", RuntimeEngine.Rendering.Stats.Vulkan.GetVulkanIndirectSecondaryEligibilityCount(EVulkanIndirectSecondaryEligibility.CommandChainsDisabled), ref first);
            AppendNumberField(s_lineBuilder, "vulkan_indirect_secondary_unsupported_inheritance", RuntimeEngine.Rendering.Stats.Vulkan.GetVulkanIndirectSecondaryEligibilityCount(EVulkanIndirectSecondaryEligibility.UnsupportedInheritance), ref first);
            AppendNumberField(s_lineBuilder, "vulkan_indirect_secondary_resource_preparation_failed", RuntimeEngine.Rendering.Stats.Vulkan.GetVulkanIndirectSecondaryEligibilityCount(EVulkanIndirectSecondaryEligibility.ResourcePreparationFailed), ref first);
            AppendVulkanSecondaryRecordingFields(s_lineBuilder, "compute", EVulkanSecondaryCommandFamily.Compute, ref first);
            AppendVulkanSecondaryRecordingFields(s_lineBuilder, "transfer", EVulkanSecondaryCommandFamily.Transfer, ref first);
            AppendVulkanSecondaryRecordingFields(s_lineBuilder, "query", EVulkanSecondaryCommandFamily.Query, ref first);
            AppendBoolField(
                s_lineBuilder,
                "vulkan_command_chain_benchmark_force_rerecord",
                XREnvironment.IsEnabled(XREngineEnvironmentVariables.VulkanCommandChainBenchmarkForceRerecord),
                ref first);
            AppendNumberField(s_lineBuilder, "vulkan_command_chain_worker_queued_chains", RuntimeEngine.Rendering.Stats.Vulkan.VulkanCommandChainWorkerQueuedChains, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_command_chain_workers_started", RuntimeEngine.Rendering.Stats.Vulkan.VulkanCommandChainWorkersStarted, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_command_chain_workers_completed", RuntimeEngine.Rendering.Stats.Vulkan.VulkanCommandChainWorkersCompleted, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_command_chain_serially_recorded", RuntimeEngine.Rendering.Stats.Vulkan.VulkanCommandChainSeriallyRecorded, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_command_chain_worker_reused", RuntimeEngine.Rendering.Stats.Vulkan.VulkanCommandChainWorkerReused, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_command_chain_worker_conflicts", RuntimeEngine.Rendering.Stats.Vulkan.VulkanCommandChainWorkerConflicts, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_command_chain_worker_failures", RuntimeEngine.Rendering.Stats.Vulkan.VulkanCommandChainWorkerFailures, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_command_chain_worker_wait_timeouts", RuntimeEngine.Rendering.Stats.Vulkan.VulkanCommandChainWorkerWaitTimeouts, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_command_chain_worker_eligibility", (int)RuntimeEngine.Rendering.Stats.Vulkan.VulkanLastCommandChainWorkerEligibility, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_command_chain_worker_eligible", RuntimeEngine.Rendering.Stats.Vulkan.GetVulkanCommandChainWorkerEligibilityCount(EVulkanCommandChainWorkerEligibility.Eligible), ref first);
            AppendNumberField(s_lineBuilder, "vulkan_command_chain_worker_too_little_independent_work", RuntimeEngine.Rendering.Stats.Vulkan.GetVulkanCommandChainWorkerEligibilityCount(EVulkanCommandChainWorkerEligibility.TooLittleIndependentWork), ref first);
            AppendNumberField(s_lineBuilder, "vulkan_command_chain_worker_mutable_renderer_conflict", RuntimeEngine.Rendering.Stats.Vulkan.GetVulkanCommandChainWorkerEligibilityCount(EVulkanCommandChainWorkerEligibility.MutableRendererConflict), ref first);
            AppendNumberField(s_lineBuilder, "vulkan_command_chain_worker_unsupported_operation", RuntimeEngine.Rendering.Stats.Vulkan.GetVulkanCommandChainWorkerEligibilityCount(EVulkanCommandChainWorkerEligibility.UnsupportedOperation), ref first);
            AppendNumberField(s_lineBuilder, "vulkan_command_chain_worker_unsupported_inheritance", RuntimeEngine.Rendering.Stats.Vulkan.GetVulkanCommandChainWorkerEligibilityCount(EVulkanCommandChainWorkerEligibility.UnsupportedInheritance), ref first);
            AppendNumberField(s_lineBuilder, "vulkan_command_chain_worker_primary_owned_indirect_stream", RuntimeEngine.Rendering.Stats.Vulkan.GetVulkanCommandChainWorkerEligibilityCount(EVulkanCommandChainWorkerEligibility.PrimaryOwnedIndirectStream), ref first);
            AppendNumberField(s_lineBuilder, "vulkan_command_chain_worker_quarantined", RuntimeEngine.Rendering.Stats.Vulkan.GetVulkanCommandChainWorkerEligibilityCount(EVulkanCommandChainWorkerEligibility.WorkerQuarantined), ref first);
            AppendNumberField(s_lineBuilder, "vulkan_command_chain_worker_resource_preparation_failed", RuntimeEngine.Rendering.Stats.Vulkan.GetVulkanCommandChainWorkerEligibilityCount(EVulkanCommandChainWorkerEligibility.ResourcePreparationFailed), ref first);
            AppendNumberField(s_lineBuilder, "vulkan_command_chain_peak_concurrent_workers", RuntimeEngine.Rendering.Stats.Vulkan.VulkanCommandChainPeakConcurrentWorkers, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_command_chain_worker_queue_delay_ms", RuntimeEngine.Rendering.Stats.Vulkan.VulkanCommandChainWorkerQueueDelayMs, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_command_chain_worker_record_ms", RuntimeEngine.Rendering.Stats.Vulkan.VulkanCommandChainWorkerRecordMs, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_command_chain_worker_active_span_ms", RuntimeEngine.Rendering.Stats.Vulkan.VulkanCommandChainWorkerActiveSpanMs, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_command_chain_worker_overlap_ms", RuntimeEngine.Rendering.Stats.Vulkan.VulkanCommandChainWorkerOverlapMs, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_command_chain_worker_merge_ms", RuntimeEngine.Rendering.Stats.Vulkan.VulkanCommandChainWorkerMergeMs, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_render_thread_wait_for_chain_workers_ms", RuntimeEngine.Rendering.Stats.Vulkan.VulkanRenderThreadWaitForChainWorkersMs, ref first);
            AppendStringField(s_lineBuilder, "vulkan_first_command_chain_structural_dirty_reason", RuntimeEngine.Rendering.Stats.Vulkan.VulkanFirstCommandChainStructuralDirtyReason, ref first);
            AppendStringField(s_lineBuilder, "vulkan_first_command_chain_descriptor_generation_mismatch", RuntimeEngine.Rendering.Stats.Vulkan.VulkanFirstCommandChainDescriptorGenerationMismatch, ref first);
            AppendStringField(s_lineBuilder, "vulkan_first_command_chain_resource_plan_revision_mismatch", RuntimeEngine.Rendering.Stats.Vulkan.VulkanFirstCommandChainResourcePlanRevisionMismatch, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_pipeline_cache_lookup_hits", RuntimeEngine.Rendering.Stats.Vulkan.VulkanPipelineCacheLookupHits, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_pipeline_cache_lookup_misses", RuntimeEngine.Rendering.Stats.Vulkan.VulkanPipelineCacheLookupMisses, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_driver_pipeline_cache_persisted_hits", RuntimeEngine.Rendering.Stats.Vulkan.VulkanDriverPipelineCachePersistedHits, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_driver_pipeline_cache_runtime_hits", RuntimeEngine.Rendering.Stats.Vulkan.VulkanDriverPipelineCacheRuntimeHits, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_driver_pipeline_cache_misses", RuntimeEngine.Rendering.Stats.Vulkan.VulkanDriverPipelineCacheMisses, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_driver_pipeline_cache_unknown", RuntimeEngine.Rendering.Stats.Vulkan.VulkanDriverPipelineCacheUnknown, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_pipeline_compile_required_count", RuntimeEngine.Rendering.Stats.Vulkan.VulkanPipelineCompileRequiredCount, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_pipeline_compile_completed_count", RuntimeEngine.Rendering.Stats.Vulkan.VulkanPipelineCompileCompletedCount, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_pipeline_background_compile_completed_count", RuntimeEngine.Rendering.Stats.Vulkan.VulkanPipelineBackgroundCompileCompletedCount, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_required_pipeline_pending_count", RuntimeEngine.Rendering.Stats.Vulkan.VulkanRequiredPipelinePendingCount, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_pipeline_record_deferred_count", RuntimeEngine.Rendering.Stats.Vulkan.VulkanPipelineRecordDeferredCount, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_render_thread_shader_compile_count", RuntimeEngine.Rendering.Stats.Vulkan.VulkanRenderThreadShaderCompileCount, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_pipeline_compile_total_ms", RuntimeEngine.Rendering.Stats.Vulkan.VulkanPipelineCompileTotalMs, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_pipeline_compile_max_ms", RuntimeEngine.Rendering.Stats.Vulkan.VulkanPipelineCompileMaxMs, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_pipeline_async_queued_count", RuntimeEngine.Rendering.Stats.Vulkan.VulkanPipelineAsyncQueuedCount, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_pipeline_queue_rejected_count", RuntimeEngine.Rendering.Stats.Vulkan.VulkanPipelineQueueRejectedCount, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_pipeline_draw_not_ready_count", RuntimeEngine.Rendering.Stats.Vulkan.VulkanPipelineDrawNotReadyCount, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_pipeline_queue_depth_high_water", RuntimeEngine.Rendering.Stats.Vulkan.VulkanPipelineQueueDepthHighWater, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_pipeline_queue_capacity", RuntimeEngine.Rendering.Stats.Vulkan.VulkanPipelineQueueCapacity, ref first);
            AppendStringField(s_lineBuilder, "vulkan_pipeline_cache_miss_summary", RuntimeEngine.Rendering.Stats.Vulkan.VulkanPipelineCacheMissSummary, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_present_attempt_count", RuntimeEngine.Rendering.Stats.Vulkan.VulkanPresentAttemptCount, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_present_accepted_count", RuntimeEngine.Rendering.Stats.Vulkan.VulkanPresentAcceptedCount, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_last_present_result", RuntimeEngine.Rendering.Stats.Vulkan.VulkanLastPresentResult, ref first);
            AppendBoolField(s_lineBuilder, "vulkan_validation_layers_enabled", RuntimeEngine.Rendering.Stats.Vulkan.VulkanValidationLayersEnabled, ref first);
            AppendBoolField(s_lineBuilder, "vulkan_synchronization_validation_enabled", RuntimeEngine.Rendering.Stats.Vulkan.VulkanSynchronizationValidationEnabled, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_validation_message_count", RuntimeEngine.Rendering.Stats.Vulkan.VulkanValidationMessageCount, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_validation_error_count", RuntimeEngine.Rendering.Stats.Vulkan.VulkanValidationErrorCount, ref first);
            AppendStringField(s_lineBuilder, "vulkan_last_validation_message", RuntimeEngine.Rendering.Stats.Vulkan.VulkanLastValidationMessage, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_retired_resource_plan_replacements", RuntimeEngine.Rendering.Stats.Vulkan.VulkanRetiredResourcePlanReplacements, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_retired_resource_plan_images", RuntimeEngine.Rendering.Stats.Vulkan.VulkanRetiredResourcePlanImages, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_retired_resource_plan_buffers", RuntimeEngine.Rendering.Stats.Vulkan.VulkanRetiredResourcePlanBuffers, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_swapchain_retirement_queued_count", RuntimeEngine.Rendering.Stats.Vulkan.VulkanSwapchainRetirementQueuedCount, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_swapchain_retirement_drained_count", RuntimeEngine.Rendering.Stats.Vulkan.VulkanSwapchainRetirementDrainedCount, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_swapchain_retirement_pending_count", RuntimeEngine.Rendering.Stats.Vulkan.VulkanSwapchainRetirementPendingCount, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_swapchain_retirement_pending_high_water", RuntimeEngine.Rendering.Stats.Vulkan.VulkanSwapchainRetirementPendingHighWater, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_swapchain_retirement_deferred_count", RuntimeEngine.Rendering.Stats.Vulkan.VulkanSwapchainRetirementDeferredCount, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_retired_descriptor_pool_count", RuntimeEngine.Rendering.Stats.Vulkan.VulkanRetiredDescriptorPoolCount, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_retired_descriptor_set_count", RuntimeEngine.Rendering.Stats.Vulkan.VulkanRetiredDescriptorSetCount, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_retired_command_buffer_count", RuntimeEngine.Rendering.Stats.Vulkan.VulkanRetiredCommandBufferCount, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_retired_query_pool_count", RuntimeEngine.Rendering.Stats.Vulkan.VulkanRetiredQueryPoolCount, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_retired_buffer_view_count", RuntimeEngine.Rendering.Stats.Vulkan.VulkanRetiredBufferViewCount, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_retired_pipeline_count", RuntimeEngine.Rendering.Stats.Vulkan.VulkanRetiredPipelineCount, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_retired_framebuffer_count", RuntimeEngine.Rendering.Stats.Vulkan.VulkanRetiredFramebufferCount, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_retired_buffer_count", RuntimeEngine.Rendering.Stats.Vulkan.VulkanRetiredBufferCount, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_retired_buffer_memory_count", RuntimeEngine.Rendering.Stats.Vulkan.VulkanRetiredBufferMemoryCount, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_retired_image_count", RuntimeEngine.Rendering.Stats.Vulkan.VulkanRetiredImageCount, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_retired_image_view_count", RuntimeEngine.Rendering.Stats.Vulkan.VulkanRetiredImageViewCount, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_retired_sampler_count", RuntimeEngine.Rendering.Stats.Vulkan.VulkanRetiredSamplerCount, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_retired_image_memory_count", RuntimeEngine.Rendering.Stats.Vulkan.VulkanRetiredImageMemoryCount, ref first);
            AppendNumberField(s_lineBuilder, "vulkan_retired_image_bytes", RuntimeEngine.Rendering.Stats.Vulkan.VulkanRetiredImageBytes, ref first);

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
            s_snapshotCount = 0;
            s_sampleIntervalFrames = 0;
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

        private static int GetSampleIntervalFramesNoLock()
        {
            if (s_sampleIntervalFrames > 0)
                return s_sampleIntervalFrames;

            string? value = Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.ProfileSampleIntervalFrames);
            s_sampleIntervalFrames = int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int intervalFrames)
                ? Math.Clamp(intervalFrames, 1, 10_000)
                : 1;
            return s_sampleIntervalFrames;
        }

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

        private static object CreateFrameOutputCaptureManifest(RuntimeEngine.Rendering.Stats.FrameOutputManifestSnapshot snapshot)
        {
            RuntimeEngine.Rendering.Stats.FrameOutputEntrySnapshot[] outputs = snapshot.Outputs ?? [];
            object[] rows = new object[outputs.Length];
            for (int i = 0; i < outputs.Length; i++)
            {
                RuntimeEngine.Rendering.Stats.FrameOutputEntrySnapshot output = outputs[i];
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
                    anti_aliasing_mode = output.AntiAliasingMode,
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
            VulkanCpuStageTelemetry telemetry = RuntimeEngine.Rendering.Stats.Vulkan.GetVulkanCpuStageTelemetry(stage);
            AppendNumberField(builder, $"vulkan_cpu_{name}_ms", telemetry.Elapsed.TotalMilliseconds, ref first);
            AppendNumberField(builder, $"vulkan_cpu_{name}_allocated_bytes", telemetry.AllocatedBytes, ref first);
            AppendNumberField(builder, $"vulkan_cpu_{name}_allocation_high_water_bytes", telemetry.AllocationHighWaterBytes, ref first);
            AppendNumberField(builder, $"vulkan_cpu_{name}_boundary_allocated_bytes", telemetry.BoundaryAllocatedBytes, ref first);
            AppendNumberField(builder, $"vulkan_cpu_{name}_boundary_allocation_high_water_bytes", telemetry.BoundaryAllocationHighWaterBytes, ref first);
            AppendNumberField(builder, $"vulkan_cpu_{name}_process_invocation_count", telemetry.InvocationCount, ref first);
            AppendNumberField(builder, $"vulkan_cpu_{name}_process_elapsed_ms", telemetry.CumulativeElapsed.TotalMilliseconds, ref first);
            AppendNumberField(builder, $"vulkan_cpu_{name}_process_peak_ms", telemetry.PeakElapsed.TotalMilliseconds, ref first);
        }

        private static void AppendVulkanFrameStageFields(
            StringBuilder builder,
            string name,
            VulkanFrameStageTiming stage,
            ref bool first)
        {
            string prefix = $"vulkan_frame_stage_{name}";
            AppendNumberField(builder, $"{prefix}_ms", stage.Elapsed.TotalMilliseconds, ref first);
            AppendNumberField(builder, $"{prefix}_interval_count", stage.IntervalCount, ref first);
            AppendStringField(builder, $"{prefix}_interval_class", stage.IntervalClass.ToString(), ref first);
            AppendStringField(builder, $"{prefix}_outcome", stage.Outcome.ToString(), ref first);
            AppendStringField(builder, $"{prefix}_wait_reason", stage.WaitReason.ToString(), ref first);
        }

        private static void AppendVulkanSecondaryRecordingFields(
            StringBuilder builder,
            string familyName,
            EVulkanSecondaryCommandFamily family,
            ref bool first)
        {
            string prefix = $"vulkan_{familyName}_secondary";
            AppendNumberField(
                builder,
                $"{prefix}_eligibility",
                (int)RuntimeEngine.Rendering.Stats.Vulkan
                    .GetVulkanLastSecondaryRecordingEligibility(family),
                ref first);
            AppendVulkanSecondaryRecordingReason(
                builder,
                prefix,
                family,
                "eligible",
                EVulkanSecondaryRecordingEligibility.Eligible,
                ref first);
            AppendVulkanSecondaryRecordingReason(
                builder,
                prefix,
                family,
                "family_disabled",
                EVulkanSecondaryRecordingEligibility.FamilyDisabled,
                ref first);
            AppendVulkanSecondaryRecordingReason(
                builder,
                prefix,
                family,
                "command_buffers_disabled",
                EVulkanSecondaryRecordingEligibility
                    .SecondaryCommandBuffersDisabled,
                ref first);
            AppendVulkanSecondaryRecordingReason(
                builder,
                prefix,
                family,
                "empty_range",
                EVulkanSecondaryRecordingEligibility.EmptyRange,
                ref first);
            AppendVulkanSecondaryRecordingReason(
                builder,
                prefix,
                family,
                "queue_family_unsupported",
                EVulkanSecondaryRecordingEligibility
                    .QueueFamilyUnsupported,
                ref first);
            AppendVulkanSecondaryRecordingReason(
                builder,
                prefix,
                family,
                "active_render_scope",
                EVulkanSecondaryRecordingEligibility.ActiveRenderScope,
                ref first);
            AppendVulkanSecondaryRecordingReason(
                builder,
                prefix,
                family,
                "query_inheritance_unsupported",
                EVulkanSecondaryRecordingEligibility
                    .QueryInheritanceUnsupported,
                ref first);
            AppendVulkanSecondaryRecordingReason(
                builder,
                prefix,
                family,
                "barrier_plan_unavailable",
                EVulkanSecondaryRecordingEligibility
                    .BarrierPlanUnavailable,
                ref first);
            AppendVulkanSecondaryRecordingReason(
                builder,
                prefix,
                family,
                "query_reset_primary_owned",
                EVulkanSecondaryRecordingEligibility
                    .QueryResetPrimaryOwned,
                ref first);
            AppendVulkanSecondaryRecordingReason(
                builder,
                prefix,
                family,
                "query_pair_primary_owned",
                EVulkanSecondaryRecordingEligibility
                    .QueryPairPrimaryOwned,
                ref first);
            AppendVulkanSecondaryRecordingReason(
                builder,
                prefix,
                family,
                "query_timestamp_primary_owned",
                EVulkanSecondaryRecordingEligibility
                    .QueryTimestampPrimaryOwned,
                ref first);
            AppendVulkanSecondaryRecordingReason(
                builder,
                prefix,
                family,
                "query_properties_primary_owned",
                EVulkanSecondaryRecordingEligibility
                    .QueryPropertiesPrimaryOwned,
                ref first);
            AppendVulkanSecondaryRecordingReason(
                builder,
                prefix,
                family,
                "query_result_ordering_unavailable",
                EVulkanSecondaryRecordingEligibility
                    .QueryResultOrderingUnavailable,
                ref first);
            AppendVulkanSecondaryRecordingReason(
                builder,
                prefix,
                family,
                "invalid_operation_state",
                EVulkanSecondaryRecordingEligibility
                    .InvalidOperationState,
                ref first);
        }

        private static void AppendVulkanSecondaryRecordingReason(
            StringBuilder builder,
            string prefix,
            EVulkanSecondaryCommandFamily family,
            string reasonName,
            EVulkanSecondaryRecordingEligibility reason,
            ref bool first)
            => AppendNumberField(
                builder,
                $"{prefix}_{reasonName}",
                RuntimeEngine.Rendering.Stats.Vulkan
                    .GetVulkanSecondaryRecordingEligibilityCount(
                        family,
                        reason),
                ref first);

        private static void AppendVulkanFrequencyPublicationFields(
            StringBuilder builder,
            ref bool first)
        {
            AppendVulkanFrequencyPublicationFields(
                builder,
                "frame",
                RuntimeEngine.Rendering.Stats.Vulkan.VulkanBindingFrequencyFrameIndex,
                ref first);
            AppendVulkanFrequencyPublicationFields(
                builder,
                "view",
                RuntimeEngine.Rendering.Stats.Vulkan.VulkanBindingFrequencyViewIndex,
                ref first);
            AppendVulkanFrequencyPublicationFields(
                builder,
                "pass",
                RuntimeEngine.Rendering.Stats.Vulkan.VulkanBindingFrequencyPassIndex,
                ref first);
            AppendVulkanFrequencyPublicationFields(
                builder,
                "material",
                RuntimeEngine.Rendering.Stats.Vulkan.VulkanBindingFrequencyMaterialIndex,
                ref first);
            AppendVulkanFrequencyPublicationFields(
                builder,
                "object",
                RuntimeEngine.Rendering.Stats.Vulkan.VulkanBindingFrequencyObjectIndex,
                ref first);
            AppendVulkanFrequencyPublicationFields(
                builder,
                "instance",
                RuntimeEngine.Rendering.Stats.Vulkan.VulkanBindingFrequencyInstanceIndex,
                ref first);
            AppendVulkanFrequencyPublicationFields(
                builder,
                "runtime_callback",
                RuntimeEngine.Rendering.Stats.Vulkan.VulkanBindingFrequencyRuntimeCallbackIndex,
                ref first);
        }

        private static void AppendVulkanFrequencyPublicationFields(
            StringBuilder builder,
            string name,
            int frequency,
            ref bool first)
        {
            AppendNumberField(
                builder,
                $"vulkan_auto_uniform_{name}_publications",
                RuntimeEngine.Rendering.Stats.Vulkan
                    .GetVulkanAutoUniformFrequencyPublicationCount(frequency),
                ref first);
            AppendNumberField(
                builder,
                $"vulkan_auto_uniform_{name}_reuses",
                RuntimeEngine.Rendering.Stats.Vulkan
                    .GetVulkanAutoUniformFrequencyReuseCount(frequency),
                ref first);
            AppendNumberField(
                builder,
                $"vulkan_auto_uniform_{name}_published_bytes",
                RuntimeEngine.Rendering.Stats.Vulkan
                    .GetVulkanAutoUniformFrequencyPublishedBytes(frequency),
                ref first);
        }

        private static void AppendRenderThreadJobFields(StringBuilder builder, ref bool first)
        {
            long totalCount = 0L;
            long totalDurationMicros = 0L;
            long totalQueueDelayMicros = 0L;
            long totalOverBudgetMicros = 0L;

            for (int index = 0; index < _lastRenderThreadJobCountByKind.Length; index++)
            {
                string source = ((RenderThreadJobKind)index).ToString().ToLowerInvariant();
                long count = Volatile.Read(ref _lastRenderThreadJobCountByKind[index]);
                long durationMicros = Volatile.Read(ref _lastRenderThreadJobDurationMicrosByKind[index]);
                long queueDelayMicros = Volatile.Read(ref _lastRenderThreadJobQueueDelayMicrosByKind[index]);
                long overBudgetMicros = Volatile.Read(ref _lastRenderThreadJobOverBudgetMicrosByKind[index]);

                AppendNumberField(builder, $"render_thread_jobs_{source}_count", count, ref first);
                AppendNumberField(builder, $"render_thread_jobs_{source}_duration_ms", durationMicros / 1000.0, ref first);
                AppendNumberField(builder, $"render_thread_jobs_{source}_queue_delay_ms", queueDelayMicros / 1000.0, ref first);
                AppendNumberField(builder, $"render_thread_jobs_{source}_over_budget_ms", overBudgetMicros / 1000.0, ref first);

                totalCount += count;
                totalDurationMicros += durationMicros;
                totalQueueDelayMicros += queueDelayMicros;
                totalOverBudgetMicros += overBudgetMicros;
            }

            AppendNumberField(builder, "render_thread_jobs_total_count", totalCount, ref first);
            AppendNumberField(builder, "render_thread_jobs_total_duration_ms", totalDurationMicros / 1000.0, ref first);
            AppendNumberField(builder, "render_thread_jobs_total_queue_delay_ms", totalQueueDelayMicros / 1000.0, ref first);
            AppendNumberField(builder, "render_thread_jobs_total_over_budget_ms", totalOverBudgetMicros / 1000.0, ref first);
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
                "FullBucketScanDiagnostic",
                "ActiveBucketListReadbackDiagnostic",
                "MaterialTable",
                "BindlessMaterialTable",
                "FullBucketScan",
                "ActiveBucketList");
            ValidateEnvEnum(errors, XREngineEnvironmentVariables.ProfileCacheMode, "Cold", "Warm");
            ValidateEnvEnum(
                errors,
                XREngineEnvironmentVariables.ProfileMode,
                "Diagnostics",
                "DevelopmentProfile",
                "CleanProfile",
                "ReleaseBenchmark");
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
            => XREnvironment.IsEnabled(name);

        private static void SetEnvironmentFlag(string name, bool enabled)
            => Environment.SetEnvironmentVariable(name, enabled ? "1" : "0");

        private static string ResolvePerformanceProfileMode()
        {
            string? raw = Environment.GetEnvironmentVariable(
                XREngineEnvironmentVariables.ProfileMode);
            if (string.IsNullOrWhiteSpace(raw))
                return "DevelopmentProfile";

            string value = raw.Trim();
            string[] knownModes =
            [
                "Diagnostics",
                "DevelopmentProfile",
                "CleanProfile",
                "ReleaseBenchmark",
            ];
            for (int i = 0; i < knownModes.Length; i++)
            {
                if (value.Equals(
                        knownModes[i],
                        StringComparison.OrdinalIgnoreCase))
                {
                    return knownModes[i];
                }
            }

            return value;
        }

        private static bool IsCleanPerformanceProfile(string profileMode)
            => profileMode.Equals(
                    "CleanProfile",
                    StringComparison.OrdinalIgnoreCase)
               || profileMode.Equals(
                    "ReleaseBenchmark",
                    StringComparison.OrdinalIgnoreCase);

        private static PerformanceObserverMetadata CapturePerformanceObserverMetadata(
            string profileMode,
            RuntimeEngine.Rendering.Stats.FrameOutputManifestSnapshot outputManifest)
        {
            bool validationLayersEnabled = CaptureBoolean(
                () => RuntimeEngine.Rendering.Stats.RendererState.ValidationLayersEnabled);
            bool debugOutputEnabled = CaptureBoolean(
                () => RuntimeEngine.Rendering.Stats.RendererState.DebugOutputEnabled);
            bool commandBufferLabelsEnabled =
                ResolveOptionalBooleanOverride(
                    Environment.GetEnvironmentVariable(
                        XREngineEnvironmentVariables.VulkanCommandBufferLabels)) ??
                CaptureBoolean(
                    () => Engine.EffectiveSettings.VulkanDiagnosticFlags.HasFlag(
                        EVulkanDiagnosticFlags.CommandBufferLabels));
            bool denseGpuTimestamps = IsEnvFlagEnabled(
                XREngineEnvironmentVariables.GpuTimestampDense);
            bool p3LoggingEnabled = IsEnvFlagEnabled(
                XREngineEnvironmentVariables.P3Logging);
            string activeDiagnosticTraceFlags =
                CaptureActiveDiagnosticTraceFlags();
            bool diagnosticTraceFlagsEnabled =
                activeDiagnosticTraceFlags.Length > 0;
            bool skipImGui = IsEnvFlagEnabled(
                XREngineEnvironmentVariables.VkSkipImGui);
            bool profilerUiActive =
                ProfilerObserverTelemetry.VisibleRows > 0;
            bool dynamicTextOverlayEnabled = HasOutputWork(
                outputManifest,
                "DynamicTextOverlay");
            bool debugOverlayEnabled =
                dynamicTextOverlayEnabled ||
                CaptureBoolean(
                    () => RenderDiagnosticsFlags.DeferredDebugView != 0);
            bool verboseLogging =
                (int)RuntimeDebugHostServices.Current.OutputVerbosity >
                (int)EOutputVerbosity.Normal;
            bool intrusive =
                validationLayersEnabled ||
                debugOutputEnabled ||
                commandBufferLabelsEnabled ||
                denseGpuTimestamps ||
                p3LoggingEnabled ||
                diagnosticTraceFlagsEnabled ||
                !skipImGui ||
                profilerUiActive ||
                dynamicTextOverlayEnabled ||
                debugOverlayEnabled ||
                verboseLogging;
            bool warmCaches =
                IsWarmCacheState(XREngineEnvironmentVariables.ProfileCacheMode) &&
                IsWarmCacheState(XREngineEnvironmentVariables.ShaderCacheMode) &&
                IsWarmCacheState(XREngineEnvironmentVariables.TextureCacheMode);
            bool cleanMode = IsCleanPerformanceProfile(profileMode);
            bool comparisonSuitable = cleanMode && !intrusive && warmCaches;
            bool promotionEligible =
                profileMode.Equals(
                    "ReleaseBenchmark",
                    StringComparison.OrdinalIgnoreCase) &&
                comparisonSuitable;
            string suitability = profileMode switch
            {
                "Diagnostics" => "IntrusiveDiagnostics",
                "DevelopmentProfile" => "DevelopmentTrendOnly",
                "CleanProfile" when comparisonSuitable => "CleanComparison",
                "ReleaseBenchmark" when comparisonSuitable => "PromotionEligible",
                "CleanProfile" or "ReleaseBenchmark" => "IntrusiveConfiguration",
                _ => "InvalidProfileMode",
            };

            return new PerformanceObserverMetadata(
                suitability,
                comparisonSuitable,
                promotionEligible,
                intrusive,
                commandBufferLabelsEnabled,
                p3LoggingEnabled,
                diagnosticTraceFlagsEnabled,
                activeDiagnosticTraceFlags,
                skipImGui
                    ? "Disabled"
                    : profilerUiActive
                        ? "Active"
                        : "Inactive",
                skipImGui ? "Disabled" : "Enabled",
                dynamicTextOverlayEnabled,
                debugOverlayEnabled);
        }

        private static string CaptureActiveDiagnosticTraceFlags()
        {
            StringBuilder? enabled = null;
            for (int i = 0; i < s_diagnosticTraceEnvironmentVariables.Length; i++)
            {
                string variableName = s_diagnosticTraceEnvironmentVariables[i];
                if (!IsEnvFlagEnabled(variableName))
                    continue;

                enabled ??= new StringBuilder();
                if (enabled.Length > 0)
                    enabled.Append(',');
                enabled.Append(variableName);
            }

            return enabled?.ToString() ?? string.Empty;
        }

        private static bool IsWarmCacheState(string environmentVariable)
            => string.Equals(
                Environment.GetEnvironmentVariable(environmentVariable),
                "Warm",
                StringComparison.OrdinalIgnoreCase);

        private static bool HasOutputWork(
            RuntimeEngine.Rendering.Stats.FrameOutputManifestSnapshot snapshot,
            string outputKind)
        {
            RuntimeEngine.Rendering.Stats.FrameOutputEntrySnapshot[] outputs =
                snapshot.Outputs ?? [];
            for (int i = 0; i < outputs.Length; i++)
            {
                RuntimeEngine.Rendering.Stats.FrameOutputEntrySnapshot output =
                    outputs[i];
                if (output.CommandCount > 0 &&
                    output.OutputKindName.Equals(
                        outputKind,
                        StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        private static ActiveRenderFeaturesMetadata CaptureActiveRenderFeatures(
            RuntimeEngine.Rendering.Stats.FrameOutputManifestSnapshot outputManifest)
        {
            XRRenderPipelineInstance? pipeline =
                RuntimeEngine.Rendering.State.CurrentRenderingPipeline;
            XRCamera? camera =
                RuntimeEngine.Rendering.State.RenderingCamera ??
                pipeline?.RenderState.SceneCamera ??
                pipeline?.LastSceneCamera ??
                pipeline?.LastRenderingCamera;
            EAntiAliasingMode antiAliasingMode =
                camera?.AntiAliasingModeOverride ??
                Engine.EffectiveSettings.AntiAliasingMode;
            uint msaaSampleCount =
                camera?.MsaaSampleCountOverride ??
                Engine.EffectiveSettings.MsaaSampleCount;
            float tsrRenderScale =
                camera?.TsrRenderScaleOverride ??
                RuntimeEngine.Rendering.Settings.TsrRenderScale;

            AmbientOcclusionSettings? ambientOcclusion =
                TryGetPostProcessSettings<AmbientOcclusionSettings>(camera);
            ColorGradingSettings? colorGrading =
                TryGetPostProcessSettings<ColorGradingSettings>(camera);
            BloomSettings? bloom =
                TryGetPostProcessSettings<BloomSettings>(camera);
            MotionBlurSettings? motionBlur =
                TryGetPostProcessSettings<MotionBlurSettings>(camera);
            bool motionBlurEnabled = motionBlur?.Enabled ?? false;
            bool motionVectorsRequested =
                antiAliasingMode is EAntiAliasingMode.Taa
                    or EAntiAliasingMode.Tsr
                    or EAntiAliasingMode.Dlaa ||
                Engine.EffectiveSettings.EnableNvidiaDlss ||
                Engine.EffectiveSettings.EnableIntelXess ||
                motionBlurEnabled;

            return new ActiveRenderFeaturesMetadata(
                CameraStateAvailable: camera is not null,
                AntiAliasingMode: antiAliasingMode.ToString(),
                MsaaSampleCount: msaaSampleCount,
                TsrRenderScale: tsrRenderScale,
                AmbientOcclusionEnabled: ambientOcclusion?.Enabled ?? false,
                AmbientOcclusionMode:
                    ambientOcclusion?.Type.ToString() ?? "Unavailable",
                AutoExposureEnabled: colorGrading?.AutoExposure ?? false,
                BloomEnabled: bloom?.Enabled ?? false,
                MotionBlurEnabled: motionBlurEnabled,
                MotionVectorsRequested: motionVectorsRequested,
                ImGuiOverlayEnabled: HasOutputWork(
                    outputManifest,
                    "ImGuiOverlay"),
                DynamicTextOverlayEnabled: HasOutputWork(
                    outputManifest,
                    "DynamicTextOverlay"));
        }

        private static TSettings? TryGetPostProcessSettings<TSettings>(
            XRCamera? camera)
            where TSettings : class
        {
            if (camera?.GetPostProcessStageState<TSettings>() is not
                { } stage ||
                !stage.TryGetBacking(out TSettings? settings))
            {
                return null;
            }

            return settings;
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
                "backend=" + CaptureString(() => RuntimeEngine.Rendering.Stats.RendererState.ActiveRenderBackend),
                "renderTargetEnv=" + renderTargetModeEnv,
                "renderTargetSetting=" + renderTargetModeSetting,
                "renderScale=" + CaptureString(() => RuntimeEngine.Rendering.Settings.TsrRenderScale.ToString(CultureInfo.InvariantCulture)),
                "strategy=" + CaptureString(() => RuntimeEngine.Rendering.LastResolvedMeshSubmissionStrategy.ToString()),
                "vrMode=" + CaptureString(() => RuntimeEngine.Rendering.Settings.VrViewRenderMode.ToString()),
                "foveation=" + CaptureString(() => RuntimeEngine.Rendering.Settings.VrFoveationMode.ToString()),
                "mirror=" + CaptureString(() => RuntimeEngine.Rendering.Settings.VrMirrorMode.ToString()),
                "renderWindowsInVr=" + CaptureString(() => RuntimeEngine.Rendering.Settings.RenderWindowsWhileInVR ? "1" : "0"),
                "primaryReuse=" + ((ResolveOptionalBooleanOverride(Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.VulkanPrimaryCommandBufferReuse)) ??
                    CaptureBoolean(() => RuntimeEngine.Rendering.Settings.EnableVulkanPrimaryCommandBufferReuse)) ? "1" : "0"),
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
            RuntimeEngine.Rendering.Stats.FrameOutputManifestSnapshot snapshot)
        {
            RuntimeEngine.Rendering.Stats.FrameOutputEntrySnapshot[] outputs = snapshot.Outputs ?? [];
            FrameOutputInventoryMetadata[] inventory = new FrameOutputInventoryMetadata[outputs.Length];
            for (int i = 0; i < outputs.Length; i++)
            {
                RuntimeEngine.Rendering.Stats.FrameOutputEntrySnapshot output = outputs[i];
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
                    output.AntiAliasingMode,
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
            string VrFoveationMode,
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
            string ProfileMode,
            string ProfileSuitability,
            bool ProfileComparisonSuitable,
            bool ProfilePromotionEligible,
            bool ProfileIntrusive,
            bool VulkanCommandBufferLabelsEnabled,
            bool P3LoggingEnabled,
            bool DiagnosticTraceFlagsEnabled,
            string ActiveDiagnosticTraceFlags,
            string ProfilerUiState,
            string EditorUiState,
            bool DynamicTextOverlayEnabled,
            bool DebugOverlayEnabled,
            string LogVerbosity,
            bool LogOutputToFile,
            string LogSessionPath,
            string XrRuntime,
            string XrRuntimeManifest,
            ActiveRenderFeaturesMetadata ActiveRenderFeatures,
            string VulkanGpuDrivenProfile,
            string GpuClockPolicy,
            double? TargetRefreshHz,
            double? XrFrameBudgetMs,
            string BenchmarkPhase,
            double? WarmupSeconds,
            double? CaptureSeconds,
            int SampleIntervalFrames,
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
            string AntiAliasingMode,
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

        private sealed record PerformanceObserverMetadata(
            string Suitability,
            bool ComparisonSuitable,
            bool PromotionEligible,
            bool Intrusive,
            bool CommandBufferLabelsEnabled,
            bool P3LoggingEnabled,
            bool DiagnosticTraceFlagsEnabled,
            string ActiveDiagnosticTraceFlags,
            string ProfilerUiState,
            string EditorUiState,
            bool DynamicTextOverlayEnabled,
            bool DebugOverlayEnabled);

        private sealed record ActiveRenderFeaturesMetadata(
            bool CameraStateAvailable,
            string AntiAliasingMode,
            uint MsaaSampleCount,
            float TsrRenderScale,
            bool AmbientOcclusionEnabled,
            string AmbientOcclusionMode,
            bool AutoExposureEnabled,
            bool BloomEnabled,
            bool MotionBlurEnabled,
            bool MotionVectorsRequested,
            bool ImGuiOverlayEnabled,
            bool DynamicTextOverlayEnabled);

        private sealed record CaptureCompletion(
            RunMetadata Metadata,
            int SampleCount,
            string OutputDirectory,
            bool CaptureEnabled,
            bool AutoDumpGpuTimings);
    }
#endif
}
