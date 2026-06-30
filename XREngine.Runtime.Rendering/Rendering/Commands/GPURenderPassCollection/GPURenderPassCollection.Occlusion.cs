using Silk.NET.OpenGL;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Data.Vectors;
using XREngine.Rendering;
using XREngine.Rendering.Pipelines.Commands;
using XREngine.Rendering.Occlusion;
using XREngine.Rendering.OpenGL;
using XREngine.Rendering.Vulkan;
using static XREngine.Rendering.GpuDispatchLogger;

namespace XREngine.Rendering.Commands
{
    public sealed partial class GPURenderPassCollection
    {
        private sealed class HiZSharedState
        {
            public XRTexture2D? Pyramid;
            public int MaxMip;
            public ulong LastBuiltFrameId;
            public uint Width;
            public uint Height;
        }

        private static readonly ConditionalWeakTable<XRRenderPipelineInstance, HiZSharedState> _hiZSharedCache = new();

        // --- HiZ per-stage timing aggregator (env-gated: XRE_HIZ_STAGE_LOGGING=1).
        // Bypasses the engine profiler so nested BeginTiming scopes (which only surface
        // as drop-log HotPath leaves) cannot hide cost. Flushes a summary line once per
        // second to Build/Logs/hiz-stage-stats.log.
        private static class HiZStageStats
        {
            private static readonly object _lock = new();
            private static readonly Dictionary<string, (long Calls, double TotalMs, double MaxMs)> _stats = new();
            private static long _lastFlushTicks;
            private static string? _logPath;

            public static bool IsEnabled()
            {
                // Always-on for the duration of this investigation. Cheap enough; flushes only once/sec.
                return true;
            }

            public static void Record(string stage, double ms)
            {
                if (!IsEnabled())
                    return;
                
                lock (_lock)
                {
                    _stats[stage] = _stats.TryGetValue(stage, out var s) 
                        ? (s.Calls + 1, s.TotalMs + ms, ms > s.MaxMs ? ms : s.MaxMs) 
                        : (1, ms, ms);

                    long now = Stopwatch.GetTimestamp();
                    if (_lastFlushTicks == 0)
                        _lastFlushTicks = now;
                    
                    double sinceFlushSec = (now - _lastFlushTicks) / (double)Stopwatch.Frequency;
                    if (sinceFlushSec >= 1.0)
                        FlushLocked(sinceFlushSec, now);
                }
            }

            private static void FlushLocked(double windowSec, long nowTicks)
            {
                _lastFlushTicks = nowTicks;
                if (_stats.Count == 0)
                    return;
                
                _logPath ??= System.IO.Path.Combine(System.IO.Directory.GetCurrentDirectory(), "Build", "Logs", "hiz-stage-stats.log");
                try
                { 
                    System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_logPath)!);
                }
                catch
                {
                    
                }

                var sb = new System.Text.StringBuilder(256);
                sb
                    .Append('[')
                    .Append(DateTime.Now.ToString("HH:mm:ss.fff"))
                    .Append("] window=")
                    .Append(windowSec.ToString("F2"))
                    .Append('s');
                
                foreach (var kv in _stats)
                {
                    double avg = kv.Value.TotalMs / Math.Max(1, kv.Value.Calls);
                    //double perFrameMs = kv.Value.TotalMs / windowSec / 60.0 * 60.0; // = TotalMs/windowSec (per second). Expose as ms/s.
                    sb
                        .Append(' ')
                        .Append(kv.Key)
                        .Append('=')
                        .Append(kv.Value.Calls)
                        .Append("c,")
                        .Append(kv.Value.TotalMs.ToString("F2"))
                        .Append("ms,avg=")
                        .Append(avg.ToString("F3"))
                        .Append(",max=")
                        .Append(kv.Value.MaxMs.ToString("F3"));
                }
                sb.AppendLine();
                try 
                {
                    System.IO.File.AppendAllText(_logPath, sb.ToString());
                }
                catch
                {
                    
                }

                _stats.Clear();
            }
        }

        private EOcclusionCullingMode _lastLoggedOcclusionMode = (EOcclusionCullingMode)(-1);
        private bool _loggedGpuHiZOcclusionScaffold;
        private bool _loggedCpuQueryAsyncScaffold;
        private bool _loggedCpuQueryModeSuppressedByProfile;

        private static readonly AsyncOcclusionQueryManager s_cpuOcclusionQueryManager = new();
        private readonly List<(uint SourceCommandIndex, XRRenderQuery Query)> _cpuOcclusionPending = [];
        private readonly Dictionary<uint, bool> _cpuOcclusionLastResolved = new();
        // Pooled across submissions to avoid per-frame allocation in the hot occlusion path.
        private readonly HashSet<uint> _cpuOcclusionPendingScratch = new();
        private ulong _cpuOcclusionLastResolveFrameId;
        private uint _cpuOcclusionLastSceneCommandCount;
        private Vector3 _cpuOcclusionLastCameraPosition;
        private Matrix4x4 _cpuOcclusionLastProjection;
        private bool _cpuOcclusionHasCameraState;
        private uint _gpuHiZLastSceneCommandCount;
        private Vector3 _gpuHiZLastCameraPosition;
        private Matrix4x4 _gpuHiZLastProjection;
        private bool _gpuHiZHasCameraState;

        private readonly Dictionary<uint, TemporalOcclusionState> _temporalOcclusion = [];

        private struct TemporalOcclusionState
        {
            public int ConsecutiveOccludedFrames;
            public ulong LastTouchedFrame;
        }

        private readonly struct GpuHiZDepthInput(
            XRTexture sampler,
            uint width,
            uint height,
            Matrix4x4 viewProjection,
            string textureName,
            bool history)
        {
            public XRTexture Sampler { get; } = sampler;
            public uint Width { get; } = width;
            public uint Height { get; } = height;
            public Matrix4x4 ViewProjection { get; } = viewProjection;
            public string TextureName { get; } = textureName;
            public bool History { get; } = history;
        }

        private const int TemporalOcclusionHysteresisFrames = 2;
        private const float TemporalCameraMotionDistanceEpsilon = 0.0001f;
        private const float TemporalCameraJumpDistance = 2.0f;
        private const float TemporalProjectionDeltaThreshold = 0.125f;
        private const int CpuOcclusionMaxQueriesPerFrame = 64;

        private bool _hiZDepthPyramidReadyForMeshlets;
        private bool _hiZDepthPyramidUsesReversedZ;
        private Matrix4x4 _hiZDepthPyramidViewProjection = Matrix4x4.Identity;

        private uint _occlusionCandidatesTested;
        private uint _occlusionAccepted;
        private uint _occlusionFalsePositiveRecoveries;
        private uint _occlusionTemporalOverrides;

        public EOcclusionCullingMode ActiveOcclusionMode => ResolveActiveOcclusionMode();
        public uint OcclusionCandidatesTested => _occlusionCandidatesTested;
        public uint OcclusionAccepted => _occlusionAccepted;
        public uint OcclusionFalsePositiveRecoveries => _occlusionFalsePositiveRecoveries;
        public uint OcclusionTemporalOverrides => _occlusionTemporalOverrides;

        // CPU Hi-Z visibility snapshot machinery removed (C-CPU-2). The CPU path now
        // owns its own occlusion via CpuRenderOcclusionCoordinator hardware queries;
        // CpuDirect never consumes the GPU compute cull output. See
        // docs/work/design/rendering/render-submission-perf-debug-plan.md section 10.

        private void ResetOcclusionFrameStats()
        {
            _occlusionCandidatesTested = 0u;
            _occlusionAccepted = 0u;
            _occlusionFalsePositiveRecoveries = 0u;
            _occlusionTemporalOverrides = 0u;
            _hiZDepthPyramidReadyForMeshlets = false;
        }

        public bool TryGetHiZDepthPyramidForMeshlets(
            out XRTexture2D pyramid,
            out int maxMip,
            out Matrix4x4 viewProjection,
            out bool usesReversedZ)
        {
            if (_hiZDepthPyramidReadyForMeshlets &&
                _hiZDepthPyramid is not null &&
                _hiZDepthPyramid.Mipmaps.Length != 0)
            {
                pyramid = _hiZDepthPyramid;
                maxMip = _hiZMaxMip;
                viewProjection = _hiZDepthPyramidViewProjection;
                usesReversedZ = _hiZDepthPyramidUsesReversedZ;
                return true;
            }

            pyramid = null!;
            maxMip = 0;
            viewProjection = Matrix4x4.Identity;
            usesReversedZ = false;
            return false;
        }

        private void RecordOcclusionFrameStats(
            uint candidatesTested,
            uint occludedAccepted,
            uint falsePositiveRecoveries,
            uint temporalOverrides)
        {
            _occlusionCandidatesTested = candidatesTested;
            _occlusionAccepted = occludedAccepted;
            _occlusionFalsePositiveRecoveries = falsePositiveRecoveries;
            _occlusionTemporalOverrides = temporalOverrides;
        }

        // C-GPU-3 dirty-bypass kill switch. Default ON: when the temporal Hi-Z state
        // is dirty (scene mutated, camera jumped) we skip the refine compute for this
        // frame and let every frustum/BVH-passing candidate through unchanged, while
        // still building the pyramid so the next frame can refine normally.
        //
        // Phase 4 contract (docs/work/todo/rendering/occlusion-and-meshlet-execution-todo.md):
        // the bypass branch now issues an explicit
        // EMemoryBarrierMask.ShaderStorage | EMemoryBarrierMask.Command before
        // returning, which serializes the upstream cull-pass writes against the
        // downstream MultiDrawElementsIndirectCount read. That was the missing
        // handoff that previously crashed nvoglv64 (STATUS_STACK_BUFFER_OVERRUN)
        // under sustained dirty conditions on the GpuIndirectZeroReadback path: the
        // refine compute had been the implicit barrier, and skipping it left the
        // indirect-count read racing the cull-pass writes. The cull pass already
        // writes the final count into _culledCountBuffer and the commands into
        // _culledSceneToRenderBuffer (same buffer downstream consumers read on the
        // refine path post-swap), so no buffer-pointer swap is required in bypass —
        // only the explicit memory barrier.
        //
        // Set XRE_GPU_HIZ_DIRTY_BYPASS=0 to force refine-always (legacy behavior,
        // may over-cull for one frame after a dirty event because the pyramid still
        // reflects pre-mutation depth).
        private static bool IsGpuHiZDirtyBypassEnabled() => RenderDiagnosticsFlags.GpuHiZDirtyBypass;

        // Crash breadcrumbs: synchronous Console.Error/Trace writes around suspect GL calls.
        // Toggled via XRE_CRASH_BREADCRUMBS=1 or the editor preference Debug → Crash
        // Breadcrumbs. Each call also issues glFinish so the GPU catches up before the
        // next breadcrumb prints; the last [CRUMB] line on stderr before a fastfail
        // identifies which GL call killed the driver. Heavy — diagnostic only.
        internal static bool AreCrashBreadcrumbsEnabled()
            => RenderDiagnosticsFlags.CrashBreadcrumbs;

        // Direct file-append transport. Console.Error and Trace listeners are unreliable
        // for WinExe processes (no console attached) and may be torn down before a fastfail
        // flushes; appending straight to a file with FlushAsync survives a hard crash.
        private static readonly object _crumbFileLock = new();
        private static string? _crumbFilePath;
        private static string GetCrumbFilePath()
        {
            if (_crumbFilePath is not null)
                return _crumbFilePath;
            string logsRoot = System.IO.Path.Combine(System.IO.Directory.GetCurrentDirectory(), "Build", "Logs");
            try { System.IO.Directory.CreateDirectory(logsRoot); } catch { }
            _crumbFilePath = System.IO.Path.Combine(logsRoot, "crumbs.log");
            return _crumbFilePath;
        }

        internal static void Crumb(string label)
        {
            if (!AreCrashBreadcrumbsEnabled())
                return;
            // No GPU sync: WaitForGpu may itself stall or throw on a corrupted context,
            // hiding the breadcrumb we are trying to capture.
            string line = "[" + DateTime.Now.ToString("HH:mm:ss.fff") + "] [CRUMB] " + label + Environment.NewLine;
            lock (_crumbFileLock)
            {
                try { System.IO.File.AppendAllText(GetCrumbFilePath(), line); } catch { }
            }
        }

        private EOcclusionCullingMode ResolveActiveOcclusionMode()
        {
            // Passthrough mode is a debug-only escape hatch; keep it behaviorally stable.
            if (ForcePassthroughCulling)
                return EOcclusionCullingMode.Disabled;

            EOcclusionCullingMode mode = VulkanFeatureProfile.ResolveOcclusionCullingMode(RuntimeEngine.EffectiveSettings.GpuOcclusionCullingMode);
            if (!VulkanFeatureProfile.IsActive)
                return mode;

            if (mode == EOcclusionCullingMode.CpuQueryAsync && VulkanFeatureProfile.ActiveProfile != EVulkanGpuDrivenProfile.Diagnostics)
            {
                if (!_loggedCpuQueryModeSuppressedByProfile)
                {
                    _loggedCpuQueryModeSuppressedByProfile = true;
                    Log(LogCategory.Culling, LogLevel.Warning,
                        "Occlusion mode {0} suppressed for profile {1}; using {2} canonical path.",
                        EOcclusionCullingMode.CpuQueryAsync,
                        VulkanFeatureProfile.ActiveProfile,
                        EOcclusionCullingMode.GpuHiZ);
                }

                return EOcclusionCullingMode.GpuHiZ;
            }

            return mode;
        }

        private void ApplyOcclusionCulling(GPUScene scene, XRCamera? camera)
        {
            HiZStageStats.Record("Entry", 0.0);
            Stopwatch occlusionStopwatch = Stopwatch.StartNew();
            void RecordOcclusionTiming()
            {
                if (!occlusionStopwatch.IsRunning)
                    return;

                occlusionStopwatch.Stop();
                RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanGpuDrivenStageTiming(
                    RuntimeEngine.Rendering.Stats.Vulkan.EVulkanGpuDrivenStageTiming.Occlusion,
                    occlusionStopwatch.Elapsed);
            }

            EOcclusionCullingMode mode = ResolveActiveOcclusionMode();
            LogOcclusionModeActivation(mode);
            XREngine.Rendering.Occlusion.OcclusionTelemetry.RecordActiveMode(
                mode, MeshSubmissionStrategy);

            if (_lastLoggedOcclusionMode != mode)
                ResetTemporalOcclusionState();

            _lastLoggedOcclusionMode = mode;

            if (mode == EOcclusionCullingMode.Disabled)
            {
                HiZStageStats.Record("Exit.Disabled", 0.0);
                RecordOcclusionTiming();
                return;
            }

            // Pass-awareness: keep shadow/depth contributors out of occlusion hiding to avoid missing required passes.
            if (RuntimeEngine.Rendering.State.IsShadowPass ||
                (RuntimeEngine.Rendering.State.CurrentRenderingPipeline?.RenderState?.UseDepthNormalMaterialVariants ?? false))
            {
                HiZStageStats.Record("Exit.ShadowOrDepthPass", 0.0);
                RecordOcclusionFrameStats(0u, 0u, 0u, 0u);
                RecordOcclusionTiming();
                return;
            }

            uint candidates = VisibleCommandCount;

            if (candidates == 0u)
            {
                HiZStageStats.Record("Exit.NoCandidates", 0.0);
                RecordOcclusionTiming();
                return;
            }

            switch (mode)
            {
                case EOcclusionCullingMode.GpuHiZ:
                    HiZStageStats.Record("Dispatch.GpuHiZ", 0.0);
                    ApplyGpuHiZOcclusionScaffold(scene, camera, candidates);
                    break;

                case EOcclusionCullingMode.CpuQueryAsync:
                    HiZStageStats.Record("Dispatch.CpuQueryAsync", 0.0);
                    ApplyCpuQueryAsyncOcclusionScaffold(scene, camera, candidates);
                    break;

                case EOcclusionCullingMode.CpuSoftwareOcclusion:
                    HiZStageStats.Record("Dispatch.CpuSoftwareOcclusion.CpuOnly", 0.0);
                    ApplyCpuSoftwareOcclusionToGpuCulledCommands(scene, candidates);
                    break;
            }

            RecordOcclusionTiming();
        }

        private void LogOcclusionModeActivation(EOcclusionCullingMode mode)
        {
            if (_lastLoggedOcclusionMode == mode)
                return;

            bool isFirstObservation = _lastLoggedOcclusionMode == (EOcclusionCullingMode)(-1);
            if (isFirstObservation && mode == EOcclusionCullingMode.Disabled)
                return;

            Log(LogCategory.Culling, LogLevel.Info, "Occlusion mode active: {0} (pass={1})", mode, RenderPass);
        }

        private void ApplyGpuHiZOcclusionScaffold(GPUScene scene, XRCamera? camera, uint candidates)
        {
            ApplyGpuHiZOcclusion(scene, camera, candidates);
        }

        private void ApplyGpuHiZOcclusion(GPUScene scene, XRCamera? camera, uint candidates)
        {
            if (camera is null)
            {
                RecordOcclusionFrameStats(candidates, 0u, 0u, 0u);
                XREngine.Rendering.Occlusion.OcclusionTelemetry.RecordGpuHiZSkipped(
                    XREngine.Rendering.Occlusion.EGpuHiZSkipReason.NoCamera);
                return;
            }

            // Need: shaders + buffers + a depth texture from the active pipeline.
            if (_hiZInitProgram is null || _hiZGenProgram is null || _hiZOcclusionProgram is null)
            {
                HiZStageStats.Record("GpuHiZ.Exit.MissingShaders", 0.0);
                RecordOcclusionFrameStats(candidates, 0u, 0u, 0u);
                XREngine.Rendering.Occlusion.OcclusionTelemetry.RecordGpuHiZSkipped(
                    XREngine.Rendering.Occlusion.EGpuHiZSkipReason.MissingShaders);
                if (!_loggedGpuHiZOcclusionScaffold)
                {
                    _loggedGpuHiZOcclusionScaffold = true;
                    Log(LogCategory.Culling, LogLevel.Warning,
                        "Occlusion mode {0} missing shader programs for pass {1}; keeping {2} candidates visible.",
                        EOcclusionCullingMode.GpuHiZ,
                        RenderPass,
                        candidates);
                }
                return;
            }

            var pipeline = RuntimeEngine.Rendering.State.CurrentRenderingPipeline;
            if (pipeline is null)
            {
                RecordOcclusionFrameStats(candidates, 0u, 0u, 0u);
                XREngine.Rendering.Occlusion.OcclusionTelemetry.RecordGpuHiZSkipped(
                    XREngine.Rendering.Occlusion.EGpuHiZSkipReason.MissingPipeline);
                return;
            }

            if (!TryResolveGpuHiZDepthInput(pipeline, camera, out GpuHiZDepthInput depthInput, out string missingDepthName))
            {
                HiZStageStats.Record("GpuHiZ.Exit.NoDepthTexture", 0.0);
                RecordOcclusionFrameStats(candidates, 0u, 0u, 0u);
                XREngine.Rendering.Occlusion.OcclusionTelemetry.RecordGpuHiZSkipped(
                    XREngine.Rendering.Occlusion.EGpuHiZSkipReason.NoDepthTexture);
                if (!_loggedGpuHiZOcclusionScaffold)
                {
                    _loggedGpuHiZOcclusionScaffold = true;
                    Log(LogCategory.Culling, LogLevel.Warning,
                        "Occlusion mode {0} missing depth texture '{1}' for pass {2}; keeping {3} candidates visible.",
                        EOcclusionCullingMode.GpuHiZ,
                        missingDepthName,
                        RenderPass,
                        candidates);
                }
                return;
            }

            XRTexture depthSampler = depthInput.Sampler;
            uint depthWidth = depthInput.Width;
            uint depthHeight = depthInput.Height;

            if (depthInput.History)
                HiZStageStats.Record("GpuHiZ.DepthSource.History", 0.0);
            else
                HiZStageStats.Record("GpuHiZ.DepthSource.Current", 0.0);
            XREngine.Rendering.Occlusion.OcclusionTelemetry.RecordGpuDepthSource(depthInput.History);

            // Forward depth-normal prepass depth already contains these same forward
            // candidates. Command-level current-depth Hi-Z can therefore self-cull an
            // entire mesh command before color or meshlet expansion gets a chance to run.
            if (ShouldBypassCurrentDepthGpuHiZRefine(depthInput))
            {
                AbstractRenderer.Current?.MemoryBarrier(
                    EMemoryBarrierMask.ShaderStorage |
                    EMemoryBarrierMask.Command |
                    EMemoryBarrierMask.ClientMappedBuffer);

                bool readbackAvailableSelfRisk = !IsCpuReadbackCountDisabledForPass();
                RecordOcclusionFrameStats(candidates, 0u, 0u, 0u);
                XREngine.Rendering.Occlusion.OcclusionTelemetry.RecordGpuPass(
                    (int)candidates, 0, readbackAvailableSelfRisk);
                XREngine.Rendering.Occlusion.OcclusionTelemetry.RecordActiveMode(
                    EOcclusionCullingMode.GpuHiZ,
                    MeshSubmissionStrategy);
                return;
            }

            if (depthWidth == 0u || depthHeight == 0u)
            {
                HiZStageStats.Record("GpuHiZ.Exit.DepthUnsupportedView", 0.0);
                RecordOcclusionFrameStats(candidates, 0u, 0u, 0u);
                XREngine.Rendering.Occlusion.OcclusionTelemetry.RecordGpuHiZSkipped(
                    XREngine.Rendering.Occlusion.EGpuHiZSkipReason.UnsupportedDepthView);
                if (!_loggedGpuHiZOcclusionScaffold)
                {
                    _loggedGpuHiZOcclusionScaffold = true;
                    Log(LogCategory.Culling, LogLevel.Warning,
                        "Occlusion mode {0}: depth texture '{1}' is an unsupported view kind ({2}) for pass {3}; keeping {4} candidates visible.",
                        EOcclusionCullingMode.GpuHiZ,
                        depthInput.TextureName,
                        depthSampler.GetType().Name,
                        RenderPass,
                        candidates);
                }
                return;
            }

            if (_cullCountScratchBuffer is null || _culledCountBuffer is null || _occlusionCulledBuffer is null || _occlusionOverflowFlagBuffer is null)
            {
                HiZStageStats.Record("GpuHiZ.Exit.MissingBuffers", 0.0);
                RecordOcclusionFrameStats(candidates, 0u, 0u, 0u);
                XREngine.Rendering.Occlusion.OcclusionTelemetry.RecordGpuHiZSkipped(
                    XREngine.Rendering.Occlusion.EGpuHiZSkipReason.MissingBuffers);
                return;
            }

            if (CulledSceneToRenderBuffer is null)
            {
                RecordOcclusionFrameStats(candidates, 0u, 0u, 0u);
                XREngine.Rendering.Occlusion.OcclusionTelemetry.RecordGpuHiZSkipped(
                    XREngine.Rendering.Occlusion.EGpuHiZSkipReason.MissingBuffers);
                return;
            }

            bool isReverseZ = camera.IsReversedDepth;
            bool cacheOncePerFrame = RuntimeEngine.Rendering.Settings.CacheGpuHiZOcclusionOncePerFrame;
            bool invalidateTemporalHiZ = ShouldInvalidateGpuHiZTemporalState(scene, camera);
            uint temporalInvalidations = invalidateTemporalHiZ ? 1u : 0u;
            RuntimeEngine.Rendering.Stats.RecordGpuDrivenHiZMode(depthInput.History ? "two-phase-history-depth" : "two-phase-current-depth");
            if (cacheOncePerFrame)
            {
                var shared = _hiZSharedCache.GetValue(pipeline, static _ => new HiZSharedState());
                EnsureSharedHiZDepthPyramid(shared, depthWidth, depthHeight);
                _hiZDepthPyramid = shared.Pyramid;
                _hiZMaxMip = shared.MaxMip;

                if (invalidateTemporalHiZ)
                    shared.LastBuiltFrameId = ulong.MaxValue;

                if (_hiZDepthPyramid is null)
                {
                    RecordOcclusionFrameStats(candidates, 0u, 0u, 0u);
                    return;
                }

                ulong frameId = RuntimeEngine.Rendering.State.RenderFrameId;
                if (shared.LastBuiltFrameId != frameId)
                {
                    Crumb($"HiZ.BuildPyramid.SHARED.BEGIN pass={RenderPass} mip={_hiZMaxMip}");
                    long _bpStart = Stopwatch.GetTimestamp();
                    BuildHiZPyramid(depthSampler, isReverseZ);
                    HiZStageStats.Record("BuildPyramid.Shared", (Stopwatch.GetTimestamp() - _bpStart) * 1000.0 / Stopwatch.Frequency);
                    Crumb("HiZ.BuildPyramid.SHARED.END");
                    shared.LastBuiltFrameId = frameId;
                }
            }
            else
            {
                // Per-pass: ensure Hi-Z pyramid exists and matches depth size, then build each pass.
                EnsureHiZDepthPyramid(depthWidth, depthHeight);
                if (_hiZDepthPyramid is null)
                {
                    RecordOcclusionFrameStats(candidates, 0u, 0u, 0u);
                    return;
                }

                long _bpStart2 = Stopwatch.GetTimestamp();
                BuildHiZPyramid(depthSampler, isReverseZ);
                HiZStageStats.Record("BuildPyramid.PerPass", (Stopwatch.GetTimestamp() - _bpStart2) * 1000.0 / Stopwatch.Frequency);
            }

            _hiZDepthPyramidReadyForMeshlets = _hiZDepthPyramid is not null;
            _hiZDepthPyramidViewProjection = depthInput.ViewProjection;
            _hiZDepthPyramidUsesReversedZ = isReverseZ;

            // C-GPU-3: when temporal state is dirty (scene mutated or camera jumped this
            // frame), the depth feeding the pyramid we just built does not contain newly
            // added meshes (or the right view of existing meshes). Consuming that
            // pyramid would over-cull — the symptom that previously kept
            // XRE_CPU_HIZ_OCCLUSION default-off. The bypass below builds the pyramid
            // anyway (so the *next* frame can cull normally) and skips refine for this
            // pass: every frustum/BVH candidate passes through unchanged.
            //
            // Phase 4 (Build/.../occlusion-and-meshlet-execution-todo.md): default ON.
            // The cull pass writes _culledSceneToRenderBuffer + _culledCountBuffer
            // directly. The refine pass would normally read those, write the refined
            // commands into _occlusionCulledBuffer, and SwapCulledBufferAfterOcclusion()
            // — leaving _culledSceneToRenderBuffer pointing at the refined output.
            // On bypass the cull pass output already lives in _culledSceneToRenderBuffer
            // (same name and shape downstream consumers expect post-swap on the refine
            // path), so we deliberately do NOT swap. The only invariant the refine pass
            // would have provided implicitly is a memory-ordering barrier between the
            // cull SSBO writes and the downstream MultiDrawElementsIndirectCount read;
            // missing that handoff is what crashed nvoglv64 (STATUS_STACK_BUFFER_OVERRUN)
            // under GpuIndirectZeroReadback. We issue it explicitly here.
            if (invalidateTemporalHiZ && IsGpuHiZDirtyBypassEnabled())
            {
                AbstractRenderer.Current?.MemoryBarrier(
                    EMemoryBarrierMask.ShaderStorage |
                    EMemoryBarrierMask.Command |
                    EMemoryBarrierMask.ClientMappedBuffer);

                bool readbackAvailableDirty = !IsCpuReadbackCountDisabledForPass();
                RecordOcclusionFrameStats(candidates, 0u, 0u, temporalInvalidations);
                RuntimeEngine.Rendering.Stats.RecordGpuDrivenHiZPhase(twoPhase: false, phaseOneDraws: candidates, phaseTwoDraws: 0L);
                XREngine.Rendering.Occlusion.OcclusionTelemetry.RecordGpuPass(
                    (int)candidates, 0, readbackAvailableDirty);
                XREngine.Rendering.Occlusion.OcclusionTelemetry.RecordGpuPassthroughDirty();
                XREngine.Rendering.Occlusion.OcclusionTelemetry.RecordActiveMode(
                    EOcclusionCullingMode.GpuHiZ,
                    MeshSubmissionStrategy);
                return;
            }

            // Occlusion refinement: read candidates from CulledSceneToRenderBuffer and count from scratch.
            // Write refined visible commands into the ping-pong buffer and final counts into _culledCountBuffer.
            Crumb($"HiZ.Refine.BEGIN pass={RenderPass} cand={candidates}");
            long _refineStart = Stopwatch.GetTimestamp();
            ApplyHiZOcclusionRefine(scene, camera, depthInput.ViewProjection);
            long refineTicks = Stopwatch.GetTimestamp() - _refineStart;
            HiZStageStats.Record("Refine", refineTicks * 1000.0 / Stopwatch.Frequency);
            RuntimeEngine.Rendering.Stats.RecordGpuDrivenStageTiming(
                TimeSpan.Zero,
                TimeSpan.FromSeconds((double)refineTicks / Stopwatch.Frequency),
                TimeSpan.Zero);
            Crumb($"HiZ.Refine.END pass={RenderPass}");

            // Swap in refined buffer for subsequent indirect build.
            Crumb($"HiZ.Swap.BEGIN pass={RenderPass}");
            long _swapStart = Stopwatch.GetTimestamp();
            SwapCulledBufferAfterOcclusion();
            HiZStageStats.Record("Swap", (Stopwatch.GetTimestamp() - _swapStart) * 1000.0 / Stopwatch.Frequency);
            Crumb($"HiZ.Swap.END pass={RenderPass}");

            // Stats: we conservatively report all candidates tested; accepted is the number removed.
            // Avoid CPU readbacks here; in shipping mode we may not have a CPU-visible count.
            uint occluded = 0u;
            bool readbackAvailable = !IsCpuReadbackCountDisabledForPass();
            if (readbackAvailable)
            {
                uint visibleAfter = VisibleCommandCount;
                occluded = candidates > visibleAfter ? (candidates - visibleAfter) : 0u;
            }
            RuntimeEngine.Rendering.Stats.RecordGpuDrivenHiZPhase(twoPhase: true, phaseOneDraws: candidates, phaseTwoDraws: readbackAvailable ? Math.Max(0u, VisibleCommandCount) : 0L);
            RecordOcclusionFrameStats(candidates, occluded, 0u, temporalInvalidations);
            XREngine.Rendering.Occlusion.OcclusionTelemetry.RecordGpuPass(
                (int)candidates, (int)occluded, readbackAvailable);
            XREngine.Rendering.Occlusion.OcclusionTelemetry.RecordActiveMode(
                EOcclusionCullingMode.GpuHiZ,
                MeshSubmissionStrategy);
        }

        private bool ShouldBypassCurrentDepthGpuHiZRefine(in GpuHiZDepthInput depthInput)
        {
            if (depthInput.History)
                return false;

            if (RuntimeEngine.Rendering.State.CurrentRenderingPipeline?.Pipeline is not IForwardDepthNormalPrePassSettings { ForwardDepthPrePassEnabled: true })
                return false;

            return RenderPass == (int)EDefaultRenderPass.OpaqueForward ||
                   RenderPass == (int)EDefaultRenderPass.MaskedForward;
        }

        private void ApplyCpuSoftwareOcclusionToGpuCulledCommands(GPUScene scene, uint candidates)
        {
            if (CulledSceneToRenderBuffer is null || _culledCountBuffer is null)
            {
                RecordOcclusionFrameStats(candidates, 0u, 0u, 0u);
                return;
            }

            if (IsCpuReadbackCountDisabledForPass())
            {
                RecordOcclusionFrameStats(candidates, 0u, 0u, 0u);
                XREngine.Debug.RenderingWarningEvery(
                    $"RenderDispatch.CpuSocGpuReadbackDisabled.{RenderPass}",
                    TimeSpan.FromSeconds(2),
                    "[RenderDispatch] CPU SOC mode needs GpuIndirectInstrumented count readback for traditional GPU indirect pass {0}; passing candidates through.",
                    RenderPass);
                return;
            }

            if (!RenderCommandCollection.CpuSoftwareOcclusion.IsFrameOpen)
            {
                RecordOcclusionFrameStats(candidates, 0u, 0u, 0u);
                return;
            }

            uint inputCount = ReadUIntAt(_culledCountBuffer, GPUScene.VisibleCountDrawIndex);
            if (inputCount == 0u)
            {
                WriteVisibleCounters(0u, 0u);
                RecordOcclusionFrameStats(candidates, 0u, 0u, 0u);
                return;
            }

            inputCount = Math.Min(inputCount, CulledSceneToRenderBuffer.ElementCount);
            AbstractRenderer.Current?.MemoryBarrier(
                EMemoryBarrierMask.ShaderStorage |
                EMemoryBarrierMask.Command |
                EMemoryBarrierMask.ClientMappedBuffer);

            uint writeIndex = 0u;
            uint visibleInstances = 0u;
            uint culled = 0u;

            for (uint readIndex = 0; readIndex < inputCount; ++readIndex)
            {
                GPUIndirectRenderCommand command = CulledSceneToRenderBuffer.GetDataRawAtIndex<GPUIndirectRenderCommand>(readIndex);
                bool keepVisible = true;
                uint sourceIndex = command.Reserved1;

                if (scene.TryGetSourceCommand(sourceIndex, out IRenderCommandMesh? sourceCommand) &&
                    sourceCommand is RenderCommand renderCommand &&
                    !CpuSoftwareOcclusionCuller.IsCpuOcclusionExcluded(sourceCommand) &&
                    renderCommand.CullingVolume is AABB bounds)
                {
                    keepVisible = RenderCommandCollection.CpuSoftwareOcclusion.TestVisible(renderCommand.StableQueryKey, bounds);
                }

                if (!keepVisible)
                {
                    culled++;
                    continue;
                }

                if (writeIndex != readIndex)
                    CulledSceneToRenderBuffer.SetDataRawAtIndex(writeIndex, command);

                visibleInstances += Math.Max(command.InstanceCount, 1u);
                writeIndex++;
            }

            if (writeIndex < inputCount)
            {
                uint byteCount = writeIndex * CulledSceneToRenderBuffer.ElementSize;
                if (byteCount > 0u)
                    CulledSceneToRenderBuffer.PushSubData(0, byteCount);
            }

            WriteVisibleCounters(writeIndex, visibleInstances);
            RecordOcclusionFrameStats(candidates, culled, 0u, 0u);
        }

        private bool ShouldInvalidateGpuHiZTemporalState(GPUScene scene, XRCamera camera)
        {
            bool sceneChanged = scene.TotalCommandCount != _gpuHiZLastSceneCommandCount;
            if (sceneChanged)
                _gpuHiZLastSceneCommandCount = scene.TotalCommandCount;

            bool cameraChanged = HasSignificantCameraChange(
                camera,
                ref _gpuHiZHasCameraState,
                ref _gpuHiZLastCameraPosition,
                ref _gpuHiZLastProjection,
                out _);

            return sceneChanged || cameraChanged;
        }

        private static bool TryResolveGpuHiZDepthInput(
            XRRenderPipelineInstance pipeline,
            XRCamera camera,
            out GpuHiZDepthInput input,
            out string missingTextureName)
        {
            const string currentDepthName = DefaultRenderPipeline.DepthViewTextureName;
            if (pipeline.TryGetTexture(currentDepthName, out XRTexture? depthTex) && depthTex is not null)
            {
                if (!TryResolveHiZDepthSource(depthTex, out XRTexture sampler, out uint width, out uint height))
                {
                    input = new GpuHiZDepthInput(depthTex, 0u, 0u, camera.ViewProjectionMatrix, currentDepthName, history: false);
                    missingTextureName = string.Empty;
                    return true;
                }

                input = new GpuHiZDepthInput(sampler, width, height, camera.ViewProjectionMatrix, currentDepthName, history: false);
                missingTextureName = string.Empty;
                return true;
            }

            // Fall back to temporal history only when no current-frame depth view is available.
            // History depth can be valid for temporal post effects, but it is too aggressive as
            // the default occlusion source for command-level culling: a small camera move can
            // turn previous-frame occluders into false negatives that remove whole mesh commands
            // before meshlet expansion has a chance to refine them.
            if (TryResolveGpuHiZHistoryDepthInput(pipeline, out input))
            {
                missingTextureName = string.Empty;
                return true;
            }

            input = default;
            missingTextureName = currentDepthName;
            return false;
        }

        private static bool TryResolveGpuHiZHistoryDepthInput(
            XRRenderPipelineInstance pipeline,
            out GpuHiZDepthInput input)
        {
            input = default;

            if (!VPRC_TemporalAccumulationPass.TryGetTemporalUniformData(out var temporalData) ||
                !temporalData.HistoryReady)
                return false;

            const string historyDepthName = DefaultRenderPipeline.HistoryDepthViewTextureName;
            if (!pipeline.TryGetTexture(historyDepthName, out XRTexture? historyDepthTex) || historyDepthTex is null)
                return false;

            if (!TryResolveHiZDepthSource(historyDepthTex, out XRTexture sampler, out uint width, out uint height))
                return false;

            input = new GpuHiZDepthInput(
                sampler,
                width,
                height,
                temporalData.PrevViewProjection,
                historyDepthName,
                history: true);
            return true;
        }

        /// <summary>
        /// Resolves the actual sample-able 2D depth texture and its size from the
        /// pipeline-exposed depth view. The pipeline's <see cref="DefaultRenderPipeline.DepthViewTextureName"/>
        /// is constructed as either <see cref="XRTexture2D"/>, <see cref="XRTexture2DView"/>
        /// (mono), or <see cref="XRTexture2DArrayView"/> (stereo, NumLayers=2). The HiZ
        /// init/gen shaders sample a plain <c>sampler2D</c>, so we accept the first two
        /// and the single-layer array case; multi-layer stereo views need a parallel
        /// sampler2DArray HiZ path which is not yet wired (C-DRP-2, deferred).
        /// </summary>
        private static bool TryResolveHiZDepthSource(XRTexture depthTex, out XRTexture sampler, out uint width, out uint height)
        {
            switch (depthTex)
            {
                case XRTexture2D plain:
                    sampler = plain;
                    width = Math.Max(1u, plain.Width);
                    height = Math.Max(1u, plain.Height);
                    return true;

                case XRTexture2DView view2d when !view2d.Array && !view2d.Multisample:
                    sampler = view2d;
                    width = Math.Max(1u, view2d.Width);
                    height = Math.Max(1u, view2d.Height);
                    return true;

                case XRTexture2DArrayView arrayView when arrayView.NumLayers == 1u && !arrayView.Multisample:
                    sampler = arrayView;
                    width = Math.Max(1u, arrayView.Width);
                    height = Math.Max(1u, arrayView.Height);
                    return true;

                default:
                    sampler = null!;
                    width = 0u;
                    height = 0u;
                    return false;
            }
        }

        private void EnsureHiZDepthPyramid(uint width, uint height)
        {
            width = Math.Max(1u, width);
            height = Math.Max(1u, height);

            int smallestMip = XRTexture.GetSmallestMipmapLevel(width, height);
            _hiZMaxMip = Math.Max(0, smallestMip);

            bool needsRecreate = _hiZDepthPyramid is null ||
                                 _hiZDepthPyramid.Mipmaps.Length < (_hiZMaxMip + 1) ||
                                 _hiZDepthPyramid.Mipmaps[0].Width != width ||
                                 _hiZDepthPyramid.Mipmaps[0].Height != height;

            if (!needsRecreate)
                return;

            // Only destroy the per-pass owned pyramid here.
            _hiZDepthPyramidOwned?.Destroy();
            _hiZDepthPyramidOwned = null;
            _hiZDepthPyramid = null;

            // Allocate an RGBA32F mip chain; only .r is used.
            // IMPORTANT: avoid allocating CPU-side pixel data for the mip chain.
            var mips = new Mipmap2D[_hiZMaxMip + 1];
            uint w = width;
            uint h = height;
            for (int i = 0; i < mips.Length; ++i)
            {
                mips[i] = new Mipmap2D(w, h, EPixelInternalFormat.Rgba32f, EPixelFormat.Rgba, EPixelType.Float, allocateData: false);
                w = Math.Max(1u, w >> 1);
                h = Math.Max(1u, h >> 1);
            }

            _hiZDepthPyramidOwned = new XRTexture2D
            {
                Name = "HiZDepthPyramid",
                Mipmaps = mips,
                SizedInternalFormat = ESizedInternalFormat.Rgba32f,
                MinFilter = ETexMinFilter.NearestMipmapNearest,
                MagFilter = ETexMagFilter.Nearest,
                UWrap = ETexWrapMode.ClampToEdge,
                VWrap = ETexWrapMode.ClampToEdge,
                AutoGenerateMipmaps = false,
                Resizable = false,
            };

            // Ensure GPU object is created.
            _hiZDepthPyramidOwned.PushData();
            _hiZDepthPyramid = _hiZDepthPyramidOwned;
        }

        private void EnsureSharedHiZDepthPyramid(HiZSharedState shared, uint width, uint height)
        {
            width = Math.Max(1u, width);
            height = Math.Max(1u, height);

            int smallestMip = XRTexture.GetSmallestMipmapLevel(width, height);
            int maxMip = Math.Max(0, smallestMip);

            bool needsRecreate = shared.Pyramid is null ||
                                 shared.MaxMip != maxMip ||
                                 shared.Width != width ||
                                 shared.Height != height ||
                                 shared.Pyramid.Mipmaps.Length < (maxMip + 1);

            if (!needsRecreate)
                return;

            shared.Pyramid?.Destroy();
            shared.Pyramid = null;

            shared.Width = width;
            shared.Height = height;
            shared.MaxMip = maxMip;
            shared.LastBuiltFrameId = ulong.MaxValue;

            var mips = new Mipmap2D[maxMip + 1];
            uint w = width;
            uint h = height;
            for (int i = 0; i < mips.Length; ++i)
            {
                mips[i] = new Mipmap2D(w, h, EPixelInternalFormat.Rgba32f, EPixelFormat.Rgba, EPixelType.Float, allocateData: false);
                w = Math.Max(1u, w >> 1);
                h = Math.Max(1u, h >> 1);
            }

            shared.Pyramid = new XRTexture2D
            {
                Name = "HiZDepthPyramid(shared)",
                Mipmaps = mips,
                SizedInternalFormat = ESizedInternalFormat.Rgba32f,
                MinFilter = ETexMinFilter.NearestMipmapNearest,
                MagFilter = ETexMagFilter.Nearest,
                UWrap = ETexWrapMode.ClampToEdge,
                VWrap = ETexWrapMode.ClampToEdge,
                AutoGenerateMipmaps = false,
                Resizable = false,
            };

            shared.Pyramid.PushData();
        }

        private void BuildHiZPyramid(XRTexture depthSamplerTexture, bool isReverseZ)
        {
            if (_hiZDepthPyramid is null)
                return;

            // Mip 0 init.
            _hiZInitProgram!.Use();
            _hiZInitProgram.Uniform("mipLevelSize", new IVector2((int)_hiZDepthPyramid.Mipmaps[0].Width, (int)_hiZDepthPyramid.Mipmaps[0].Height));
            _hiZInitProgram.Sampler("depthTexture", depthSamplerTexture, 0);
            _hiZInitProgram.BindImageTexture(1u, _hiZDepthPyramid, 0, false, 0, XRRenderProgram.EImageAccess.WriteOnly, XRRenderProgram.EImageFormat.RGBA32F);

            uint gx = (uint)Math.Max(1, ((int)_hiZDepthPyramid.Mipmaps[0].Width + 15) / 16);
            uint gy = (uint)Math.Max(1, ((int)_hiZDepthPyramid.Mipmaps[0].Height + 15) / 16);
            _hiZInitProgram.DispatchCompute(gx, gy, 1, EMemoryBarrierMask.ShaderImageAccess | EMemoryBarrierMask.TextureFetch);

            // Generate remaining mips using reduction.
            // Normal Z: depth increases with distance -> Hi-Z should store MAX depth per region.
            // Reversed Z: depth decreases with distance -> Hi-Z should store MIN depth per region.
            uint useMinReduction = isReverseZ ? 1u : 0u;

            _hiZGenProgram!.Use();
            _hiZGenProgram.Sampler("depthTexture", _hiZDepthPyramid, 0);
            _hiZGenProgram.Uniform("UseMinReduction", useMinReduction);

            for (int dstMip = 1; dstMip <= _hiZMaxMip; ++dstMip)
            {
                var mip = _hiZDepthPyramid.Mipmaps[dstMip];
                _hiZGenProgram.Uniform("SrcMip", dstMip - 1);
                _hiZGenProgram.Uniform("mipLevelSize", new IVector2((int)mip.Width, (int)mip.Height));
                _hiZGenProgram.BindImageTexture(1u, _hiZDepthPyramid, dstMip, false, 0, XRRenderProgram.EImageAccess.WriteOnly, XRRenderProgram.EImageFormat.RGBA32F);

                uint mx = (uint)Math.Max(1, ((int)mip.Width + 15) / 16);
                uint my = (uint)Math.Max(1, ((int)mip.Height + 15) / 16);
                _hiZGenProgram.DispatchCompute(mx, my, 1, EMemoryBarrierMask.ShaderImageAccess | EMemoryBarrierMask.TextureFetch);
            }
        }

        private void ApplyHiZOcclusionRefine(GPUScene scene, XRCamera camera, in Matrix4x4 viewProjection)
        {
            if (_hiZDepthPyramid is null)
                return;

            if (_copyCount3Program is null)
                return;

            // Reset output counters (scratch output for occlusion stage)
            WriteUints(_cullCountScratchBuffer!, 0u, 0u, 0u);
            WriteUInt(_occlusionOverflowFlagBuffer!, 0u);

            // Prepare uniforms
            uint reversed = camera.IsReversedDepth ? 1u : 0u;

            _hiZOcclusionProgram!.Use();
            _hiZOcclusionProgram.Uniform("ViewProj", viewProjection);
            _hiZOcclusionProgram.Uniform("HiZMaxMip", _hiZMaxMip);
            _hiZOcclusionProgram.Uniform("IsReversedDepth", reversed);
            _hiZOcclusionProgram.Uniform("MaxOutputCommands", (int)CulledSceneToRenderBuffer!.ElementCount);

            bool requireHotCommands = IsHotCommandLayoutRequired();
            bool useHotCommands = _culledHotCommandsValid &&
                _culledHotCommandBuffer is not null &&
                _occlusionCulledHotBuffer is not null;

            if (requireHotCommands && !useHotCommands)
            {
                Debug.MeshesWarning($"{FormatDebugPrefix("Culling")} ShippingFast profile requires hot-command layout for Hi-Z occlusion refine; refine pass skipped.");
                return;
            }

            _hiZOcclusionProgram.Uniform("UseHotCommands", useHotCommands ? 1 : 0);

            // Bind pyramid and buffers
            _hiZOcclusionProgram.Sampler("HiZDepth", _hiZDepthPyramid, 0);
            _hiZOcclusionProgram.BindBuffer(CulledSceneToRenderBuffer!, 0);
            _hiZOcclusionProgram.BindBuffer(_occlusionCulledBuffer!, 1);
            BindStorageBuffer(_hiZOcclusionProgram, _culledCountBuffer!, 2);
            BindStorageBuffer(_hiZOcclusionProgram, _cullCountScratchBuffer!, 3);
            _hiZOcclusionProgram.BindBuffer(_occlusionOverflowFlagBuffer!, 4);
            scene.BoundsBuffer.BindTo(_hiZOcclusionProgram, 5);
            if (useHotCommands)
            {
                _hiZOcclusionProgram.BindBuffer(_culledHotCommandBuffer!, 9);
                _hiZOcclusionProgram.BindBuffer(_occlusionCulledHotBuffer!, 10);
            }
            if (_statsBuffer is not null)
                _hiZOcclusionProgram.BindBuffer(_statsBuffer, 8);

            // Dispatch sizing.
            //
            // The shader (Compute/Occlusion/GPURenderOcclusionHiZ.comp) reads the actual
            // input count from binding 2 (InCount in _culledCountBuffer) on the GPU and
            // bounds-checks `idx >= inputCount` per thread, so the dispatch count only
            // has to be an upper bound on threads, not exact.
            //
            // When CPU readback is available, VisibleCommandCount is a fresh mirror of
            // the cull-pass count and gives a tight dispatch. When CPU readback is
            // DISABLED (GpuIndirectZeroReadback), VisibleCommandCount is stale —
            // possibly zero (never updated this run) or possibly larger than the actual
            // candidates this frame. The previous floor `Math.Max(VisibleCommandCount, 1u)`
            // could collapse to one workgroup (256 threads), false-occluding everything
            // past index 256. Dispatch the full buffer ElementCount in zero-readback;
            // the shader's `idx >= InCount` early-return makes excess threads near-free.
            uint dispatchCount = IsCpuReadbackCountDisabledForPass()
                ? CulledSceneToRenderBuffer!.ElementCount
                : VisibleCommandCount;

            uint groups = Math.Max(1u, (dispatchCount + 255u) / 256u);
            _hiZOcclusionProgram.DispatchCompute(groups, 1, 1, EMemoryBarrierMask.ShaderStorage | EMemoryBarrierMask.Command);

            // Forward occlusion output counts into the primary count buffer for indirect build.
            _copyCount3Program.Use();
            BindStorageBuffer(_copyCount3Program, _cullCountScratchBuffer!, 0);
            BindStorageBuffer(_copyCount3Program, _culledCountBuffer!, 1);
            _copyCount3Program.DispatchCompute(1, 1, 1, EMemoryBarrierMask.ShaderStorage | EMemoryBarrierMask.Command);

            _culledHotCommandsValid = useHotCommands;

            // Update VisibleCommandCount/InstanceCount from final count buffer in debug/readback mode.
            UpdateVisibleCountersFromBuffer(_culledCountBuffer);
        }

        private void SwapCulledBufferAfterOcclusion()
        {
            if (_occlusionCulledBuffer is null || _culledSceneToRenderBuffer is null)
                return;

            // After occlusion, the refined buffer becomes the active culled buffer.
            (_culledSceneToRenderBuffer, _occlusionCulledBuffer) = (_occlusionCulledBuffer, _culledSceneToRenderBuffer);

            if (_culledHotCommandsValid && _culledHotCommandBuffer is not null && _occlusionCulledHotBuffer is not null)
                (_culledHotCommandBuffer, _occlusionCulledHotBuffer) = (_occlusionCulledHotBuffer, _culledHotCommandBuffer);
        }

        private void ApplyCpuQueryAsyncOcclusionScaffold(GPUScene scene, XRCamera? camera, uint candidates)
        {
            ApplyCpuQueryAsyncOcclusion(scene, camera, candidates);
        }

        private void ApplyCpuQueryAsyncOcclusion(GPUScene scene, XRCamera? camera, uint candidates)
        {
            if (camera is null)
            {
                RecordOcclusionFrameStats(candidates, 0u, 0u, 0u);
                return;
            }

            uint temporalOverrides = 0u;
            if (scene.TotalCommandCount != _cpuOcclusionLastSceneCommandCount)
            {
                _cpuOcclusionLastSceneCommandCount = scene.TotalCommandCount;
                temporalOverrides += (uint)_temporalOcclusion.Count;
                ResetTemporalOcclusionState();
            }

            bool cameraMoved;
            if (HasSignificantCameraChange(
                camera,
                ref _cpuOcclusionHasCameraState,
                ref _cpuOcclusionLastCameraPosition,
                ref _cpuOcclusionLastProjection,
                out cameraMoved))
            {
                temporalOverrides += (uint)_temporalOcclusion.Count;
                ResetTemporalOcclusionState();
            }

            ResolveCpuOcclusionQueryResults();
            SubmitCpuOcclusionQueryBatch(scene, camera, candidates, cameraMoved);

            uint falsePositiveRecoveries = 0u;
            uint occluded = ApplyTemporalCpuOcclusionFilter(candidates, cameraMoved, ref temporalOverrides, ref falsePositiveRecoveries);
            RecordOcclusionFrameStats(candidates, occluded, falsePositiveRecoveries, temporalOverrides);

            if (occluded > 0u)
                OcclusionTelemetry.RecordCpuQueryAsyncOccluded((int)occluded);

            if (!_loggedCpuQueryAsyncScaffold)
            {
                _loggedCpuQueryAsyncScaffold = true;
                Log(LogCategory.Culling, LogLevel.Info,
                    "Occlusion mode {0} active on GPU dispatch path for pass {1}: proxy-AABB queries are " +
                    "submitted at up to {2}/frame, resolved next frame, and fed through a {3}-frame hysteresis filter.",
                    EOcclusionCullingMode.CpuQueryAsync,
                    RenderPass,
                    CpuOcclusionMaxQueriesPerFrame,
                    TemporalOcclusionHysteresisFrames);
            }
        }

        private static bool HasSignificantCameraChange(
            XRCamera camera,
            ref bool hasCameraState,
            ref Vector3 lastCameraPosition,
            ref Matrix4x4 lastProjection,
            out bool cameraMoved)
        {
            Vector3 position = camera.Transform.RenderTranslation;
            Matrix4x4 projection = camera.ProjectionMatrix;

            if (!hasCameraState)
            {
                hasCameraState = true;
                lastCameraPosition = position;
                lastProjection = projection;
                cameraMoved = false;
                return false;
            }

            float distanceSq = Vector3.DistanceSquared(lastCameraPosition, position);
            bool movedAny = distanceSq > (TemporalCameraMotionDistanceEpsilon * TemporalCameraMotionDistanceEpsilon);
            bool movedFar = distanceSq > (TemporalCameraJumpDistance * TemporalCameraJumpDistance);

            float projDelta =
                MathF.Abs(lastProjection.M11 - projection.M11) +
                MathF.Abs(lastProjection.M22 - projection.M22);
            bool projectionChanged = projDelta > TemporalProjectionDeltaThreshold;

            lastCameraPosition = position;
            lastProjection = projection;
            cameraMoved = movedAny || projectionChanged;
            return movedFar || projectionChanged;
        }

        private void ResetTemporalOcclusionState()
        {
            _temporalOcclusion.Clear();
            _cpuOcclusionLastResolved.Clear();
        }

        private void SubmitCpuOcclusionQueryBatch(GPUScene scene, XRCamera camera, uint candidates, bool cameraMoved)
        {
            _ = candidates;

            // Proxy-AABB submission requires synchronous draws bracketed by hardware queries.
            // The pool path is implemented for OpenGL; the Vulkan backend has its own query
            // lifecycle and would need an equivalent renderer wired before submitting here.
            if (AbstractRenderer.Current is not OpenGLRenderer)
                return;

            if (CulledSceneToRenderBuffer is null || _culledCountBuffer is null)
                return;

            // The temporal filter below requires the visible count readback to know how many
            // candidates to iterate. When readback is disabled, we cannot enumerate the
            // CulledSceneToRenderBuffer safely — fall through and let the filter pass
            // everything through. (Matches ApplyTemporalCpuOcclusionFilter's own bail.)
            if (IsCpuReadbackCountDisabledForPass())
                return;

            uint inputCount = ReadUIntAt(_culledCountBuffer, GPUScene.VisibleCountDrawIndex);
            if (inputCount == 0u)
                return;

            // Track which sourceIndexes already have an in-flight query so we don't queue
            // duplicates from the same frame (cheap O(N) for small N — we cap at
            // CpuOcclusionMaxQueriesPerFrame total queries in flight). Pool the set
            // across frames; this is a per-frame hot path on the GPU-dispatch occlusion
            // stage.
            HashSet<uint>? pendingSet = null;
            if (_cpuOcclusionPending.Count > 0)
            {
                _cpuOcclusionPendingScratch.Clear();
                for (int i = 0; i < _cpuOcclusionPending.Count; ++i)
                    _cpuOcclusionPendingScratch.Add(_cpuOcclusionPending[i].SourceCommandIndex);
                pendingSet = _cpuOcclusionPendingScratch;
            }

            ulong frameId = RuntimeEngine.Rendering.State.RenderFrameId;
            int retestPeriod = cameraMoved ? 1 : Math.Max(1, TemporalOcclusionHysteresisFrames * 3);
            int submissionBudget = Math.Max(0, CpuOcclusionMaxQueriesPerFrame - _cpuOcclusionPending.Count);
            if (submissionBudget == 0)
                return;

            int submitted = 0;
            for (uint i = 0; i < inputCount && submitted < submissionBudget; ++i)
            {
                GPUIndirectRenderCommand cmd = CulledSceneToRenderBuffer.GetDataRawAtIndex<GPUIndirectRenderCommand>(i);
                uint sourceIndex = cmd.Reserved1;

                // Skip if already in flight this frame.
                if (pendingSet is not null && pendingSet.Contains(sourceIndex))
                    continue;

                // LRU-stagger: a sourceIndex with a recent result only requeries every
                // retestPeriod frames so the per-frame submission cost stays bounded.
                if (_cpuOcclusionLastResolved.ContainsKey(sourceIndex))
                {
                    if (((frameId + sourceIndex) % (ulong)retestPeriod) != 0UL)
                        continue;
                }

                if (!scene.TryGetSourceCommand(sourceIndex, out IRenderCommandMesh? sourceCommand) ||
                    sourceCommand is not RenderCommand renderCommand ||
                    renderCommand.CullingVolume is not AABB bounds)
                    continue;

                if (CpuSoftwareOcclusionCuller.IsCpuOcclusionExcluded(sourceCommand))
                    continue;

                Vector3 size = bounds.Max - bounds.Min;
                if (size.X <= 0f || size.Y <= 0f || size.Z <= 0f)
                    continue;

                XRRenderQuery query = s_cpuOcclusionQueryManager.Acquire(EQueryTarget.AnySamplesPassedConservative);
                OpenGLRenderer? gl = AbstractRenderer.Current as OpenGLRenderer;
                GLRenderQuery? glQuery = gl?.GenericToAPI<GLRenderQuery>(query);
                if (glQuery is null)
                {
                    s_cpuOcclusionQueryManager.Release(query);
                    continue;
                }

                glQuery.BeginQuery(EQueryTarget.AnySamplesPassedConservative);
                CpuOcclusionProxyRenderer.Draw(bounds);
                glQuery.EndQuery();

                _cpuOcclusionPending.Add((sourceIndex, query));
                submitted++;
            }

            if (submitted > 0)
                OcclusionTelemetry.RecordCpuQueryAsyncSubmitted(submitted);

            _ = camera; // Camera state is observed via the caller's HasSignificantCameraChange bookkeeping.
        }

        private uint ApplyTemporalCpuOcclusionFilter(uint candidates, bool cameraMoved, ref uint temporalOverrides, ref uint falsePositiveRecoveries)
        {
            if (CulledSceneToRenderBuffer is null || _culledCountBuffer is null)
                return 0u;

            // CPU temporal filter requires GPU count readback — pass through all candidates when disabled.
            if (IsCpuReadbackCountDisabledForPass())
                return candidates;

            uint inputCount = ReadUIntAt(_culledCountBuffer, GPUScene.VisibleCountDrawIndex);
            if (inputCount == 0u)
                return 0u;

            ulong frameId = RuntimeEngine.Rendering.State.RenderFrameId;
            uint writeIndex = 0u;
            uint visibleInstances = 0u;
            uint occludedAccepted = 0u;

            for (uint i = 0; i < inputCount; ++i)
            {
                var cmd = CulledSceneToRenderBuffer.GetDataRawAtIndex<GPUIndirectRenderCommand>(i);
                uint sourceIndex = cmd.Reserved1;

                bool resolved = _cpuOcclusionLastResolved.TryGetValue(sourceIndex, out bool anySamplesPassed);
                bool keepVisible = true;

                if (resolved)
                {
                    ref TemporalOcclusionState state = ref CollectionsMarshal.GetValueRefOrAddDefault(_temporalOcclusion, sourceIndex, out bool exists);
                    if (!exists)
                        state = default;

                    state.LastTouchedFrame = frameId;

                    if (anySamplesPassed)
                    {
                        if (state.ConsecutiveOccludedFrames > 0)
                        {
                            temporalOverrides++;
                            falsePositiveRecoveries++;
                        }
                        state.ConsecutiveOccludedFrames = 0;
                    }
                    else
                    {
                        state.ConsecutiveOccludedFrames++;
                        if (cameraMoved)
                        {
                            temporalOverrides++;
                        }
                        else if (state.ConsecutiveOccludedFrames >= TemporalOcclusionHysteresisFrames)
                        {
                            keepVisible = false;
                            occludedAccepted++;
                        }
                        else
                        {
                            temporalOverrides++;
                        }
                    }
                }

                if (!keepVisible)
                    continue;

                if (writeIndex != i)
                    CulledSceneToRenderBuffer.SetDataRawAtIndex(writeIndex, cmd);
                writeIndex++;
                visibleInstances += cmd.InstanceCount;
            }

            if (writeIndex != inputCount)
            {
                uint byteCount = writeIndex * CulledSceneToRenderBuffer.ElementSize;
                CulledSceneToRenderBuffer.PushSubData(0, byteCount);
            }

            WriteUints(_culledCountBuffer, writeIndex, visibleInstances, 0u);
            UpdateVisibleCountersFromBuffer(_culledCountBuffer);
            return occludedAccepted;
        }

        private void ResolveCpuOcclusionQueryResults()
        {
            ulong frameId = RuntimeEngine.Rendering.State.RenderFrameId;
            if (_cpuOcclusionLastResolveFrameId == frameId)
                return;

            _cpuOcclusionLastResolveFrameId = frameId;

            int resolved = 0;
            for (int i = _cpuOcclusionPending.Count - 1; i >= 0; --i)
            {
                (uint sourceIndex, XRRenderQuery query) = _cpuOcclusionPending[i];
                if (!s_cpuOcclusionQueryManager.TryGetAnySamplesPassed(query, out bool anySamplesPassed))
                    continue;

                _cpuOcclusionLastResolved[sourceIndex] = anySamplesPassed;
                s_cpuOcclusionQueryManager.Release(query);
                _cpuOcclusionPending.RemoveAt(i);
                resolved++;
            }

            if (resolved > 0)
                OcclusionTelemetry.RecordCpuQueryAsyncResolved(resolved);
        }
    }
}
