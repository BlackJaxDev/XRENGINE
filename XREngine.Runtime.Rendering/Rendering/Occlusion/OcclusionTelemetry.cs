using System.Threading;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.Occlusion
{
    /// <summary>
    /// Lightweight per-frame occlusion-culling observability.
    /// Counts are accumulated atomically as render passes execute, snapshotted to
    /// last-frame fields by <see cref="BeginFrame"/> (called from Engine.Rendering.Stats.BeginFrame),
    /// and exposed to debug UI so the user can see whether occlusion is actually doing work.
    ///
    /// Design rules:
    /// - No GPU readbacks are introduced here. CPU-query path produces fully accurate counts
    ///   because the cull decision is made on the CPU. GPU Hi-Z path can only report
    ///   accurate "culled" counts when the active submission strategy already performs
    ///   count readback (i.e. GpuIndirectInstrumented). For GpuIndirectZeroReadback,
    ///   <see cref="LastFrameGpuOcclusionAvailable"/> is false and the UI explains why.
    /// </summary>
    public static class OcclusionTelemetry
    {
        // CPU-query path (hardware AnySamplesPassedConservative, decision on CPU).
        private static int _cpuTested;
        private static int _cpuCulled;
        private static int _cpuPassesActive;
        private static int _cpuPassesSkippedNoCamera;
        private static int _cpuPassesSkippedShadow;
        private static int _cpuPassesSkippedModeOff;
        private static int _lastFrameCpuTested;
        private static int _lastFrameCpuCulled;
        private static int _lastFrameCpuPassesActive;
        private static int _lastFrameCpuPassesSkippedNoCamera;
        private static int _lastFrameCpuPassesSkippedShadow;
        private static int _lastFrameCpuPassesSkippedModeOff;

        // GPU Hi-Z path (compute cull on GPU; counts only available when readback is permitted).
        private static int _gpuCandidates;
        private static int _gpuOccluded;
        private static int _gpuPassesActive;
        private static int _gpuPassesWithReadback;
        private static int _lastFrameGpuCandidates;
        private static int _lastFrameGpuOccluded;
        private static int _lastFrameGpuPassesActive;
        private static int _lastFrameGpuPassesWithReadback;

        // Most recently observed effective modes (set every frame; sticky for UI).
        private static EOcclusionCullingMode _lastEffectiveMode = EOcclusionCullingMode.Disabled;
        private static EMeshSubmissionStrategy _lastSubmissionStrategy;

        // C-CPU-3 scaffold telemetry: SOC pass observability.
        private static int _cpuSocTested;
        private static int _cpuSocCulled;
        private static int _lastFrameCpuSocTested;
        private static int _lastFrameCpuSocCulled;

        // Per-decision distribution for the CPU-query path. This is the diagnostic that
        // tells us whether occlusion is failing at the *query* level (hardware reports
        // samples passing on a mesh that shouldn't be visible) or at the *decision* level
        // (hysteresis / retest / cache reuse keeping it Visible).
        private static int _cpuDecisionSeed;             // First-seen command (no query yet)
        private static int _cpuDecisionCached;           // Same-frame cache hit (prepass+color sharing)
        private static int _cpuDecisionVisibleQuery;    // Query last reported samples-passed
        private static int _cpuDecisionVisibleHyst;     // Query reported zero, hysteresis still visible
        private static int _cpuDecisionProbe;            // ProbeOnly (retest / camera-jump seed)
        private static int _cpuDecisionSkip;             // Skip (fully occluded)
        private static int _lastFrameCpuDecisionSeed;
        private static int _lastFrameCpuDecisionCached;
        private static int _lastFrameCpuDecisionVisibleQuery;
        private static int _lastFrameCpuDecisionVisibleHyst;
        private static int _lastFrameCpuDecisionProbe;
        private static int _lastFrameCpuDecisionSkip;

        /// <summary>Last completed frame: commands fed to CPU-query occlusion.</summary>
        public static int CpuTested => _lastFrameCpuTested;
        /// <summary>Last completed frame: commands skipped by CPU-query occlusion.</summary>
        public static int CpuCulled => _lastFrameCpuCulled;
        /// <summary>Last completed frame: commands drawn after CPU-query occlusion.</summary>
        public static int CpuRendered => _lastFrameCpuTested - _lastFrameCpuCulled;
        /// <summary>Last completed frame: how many render passes engaged the CPU-query path.</summary>
        public static int CpuPassesActive => _lastFrameCpuPassesActive;
        /// <summary>Last completed frame: RenderCPU calls that skipped occlusion because no camera was supplied.</summary>
        public static int CpuPassesSkippedNoCamera => _lastFrameCpuPassesSkippedNoCamera;
        /// <summary>Last completed frame: RenderCPU calls that skipped occlusion because the pass was a shadow pass.</summary>
        public static int CpuPassesSkippedShadow => _lastFrameCpuPassesSkippedShadow;
        /// <summary>Last completed frame: RenderCPU calls that skipped occlusion because the effective mode wasn't CpuQueryAsync.</summary>
        public static int CpuPassesSkippedModeOff => _lastFrameCpuPassesSkippedModeOff;

        /// <summary>Last completed frame: candidates fed to GPU Hi-Z occlusion (pre-occlusion, post-frustum).</summary>
        public static int GpuCandidates => _lastFrameGpuCandidates;
        /// <summary>Last completed frame: candidates removed by GPU Hi-Z occlusion. Zero if readback disabled.</summary>
        public static int GpuOccluded => _lastFrameGpuOccluded;
        /// <summary>Last completed frame: how many render passes engaged the GPU Hi-Z path.</summary>
        public static int GpuPassesActive => _lastFrameGpuPassesActive;
        /// <summary>Last completed frame: how many GPU Hi-Z passes were able to read back accurate counts.</summary>
        public static int GpuPassesWithReadback => _lastFrameGpuPassesWithReadback;
        /// <summary>True when at least one GPU Hi-Z pass produced an accurate count this frame.</summary>
        public static bool LastFrameGpuOcclusionAvailable => _lastFrameGpuPassesWithReadback > 0;

        public static EOcclusionCullingMode LastEffectiveMode => _lastEffectiveMode;
        public static EMeshSubmissionStrategy LastSubmissionStrategy => _lastSubmissionStrategy;

        /// <summary>Last completed frame: SOC visibility tests run.</summary>
        public static int CpuSocTested => _lastFrameCpuSocTested;
        /// <summary>Last completed frame: SOC visibility tests that reported occluded.</summary>
        public static int CpuSocCulled => _lastFrameCpuSocCulled;

        /// <summary>Last completed frame: first-seen commands (no prior query — forced Visible).</summary>
        public static int CpuDecisionSeed => _lastFrameCpuDecisionSeed;
        /// <summary>Last completed frame: same-frame cached decisions (prepass+color sharing one decision).</summary>
        public static int CpuDecisionCached => _lastFrameCpuDecisionCached;
        /// <summary>Last completed frame: Visible because the hardware query last reported samples-passed.</summary>
        public static int CpuDecisionVisibleQuery => _lastFrameCpuDecisionVisibleQuery;
        /// <summary>Last completed frame: Visible because of hysteresis (query was zero but we still drew).</summary>
        public static int CpuDecisionVisibleHysteresis => _lastFrameCpuDecisionVisibleHyst;
        /// <summary>Last completed frame: ProbeOnly decisions (depth-only retest, no color contribution).</summary>
        public static int CpuDecisionProbe => _lastFrameCpuDecisionProbe;
        /// <summary>Last completed frame: Skip decisions (fully occluded, no draw, no probe).</summary>
        public static int CpuDecisionSkip => _lastFrameCpuDecisionSkip;

        /// <summary>Called by Engine.Rendering.Stats.BeginFrame to snapshot and reset counters.</summary>
        public static void BeginFrame()
        {
            _lastFrameCpuTested = _cpuTested;
            _lastFrameCpuCulled = _cpuCulled;
            _lastFrameCpuPassesActive = _cpuPassesActive;
            _lastFrameCpuPassesSkippedNoCamera = _cpuPassesSkippedNoCamera;
            _lastFrameCpuPassesSkippedShadow = _cpuPassesSkippedShadow;
            _lastFrameCpuPassesSkippedModeOff = _cpuPassesSkippedModeOff;
            _lastFrameGpuCandidates = _gpuCandidates;
            _lastFrameGpuOccluded = _gpuOccluded;
            _lastFrameGpuPassesActive = _gpuPassesActive;
            _lastFrameGpuPassesWithReadback = _gpuPassesWithReadback;
            _lastFrameCpuSocTested = _cpuSocTested;
            _lastFrameCpuSocCulled = _cpuSocCulled;

            _lastFrameCpuDecisionSeed = _cpuDecisionSeed;
            _lastFrameCpuDecisionCached = _cpuDecisionCached;
            _lastFrameCpuDecisionVisibleQuery = _cpuDecisionVisibleQuery;
            _lastFrameCpuDecisionVisibleHyst = _cpuDecisionVisibleHyst;
            _lastFrameCpuDecisionProbe = _cpuDecisionProbe;
            _lastFrameCpuDecisionSkip = _cpuDecisionSkip;

            _cpuTested = 0;
            _cpuCulled = 0;
            _cpuPassesActive = 0;
            _cpuPassesSkippedNoCamera = 0;
            _cpuPassesSkippedShadow = 0;
            _cpuPassesSkippedModeOff = 0;
            _gpuCandidates = 0;
            _gpuOccluded = 0;
            _gpuPassesActive = 0;
            _gpuPassesWithReadback = 0;
            _cpuSocTested = 0;
            _cpuSocCulled = 0;
            _cpuDecisionSeed = 0;
            _cpuDecisionCached = 0;
            _cpuDecisionVisibleQuery = 0;
            _cpuDecisionVisibleHyst = 0;
            _cpuDecisionProbe = 0;
            _cpuDecisionSkip = 0;
        }

        /// <summary>Records one CPU-query pass with its candidate count.</summary>
        public static void RecordCpuPassBegin(int candidateCount)
        {
            Interlocked.Increment(ref _cpuPassesActive);
            if (candidateCount > 0)
                Interlocked.Add(ref _cpuTested, candidateCount);
        }

        /// <summary>Records that the CPU-query path skipped one command.</summary>
        public static void RecordCpuCulledOne()
        {
            Interlocked.Increment(ref _cpuCulled);
        }

        /// <summary>Records a RenderCPU invocation that was eligible for CPU-query occlusion but bypassed it.</summary>
        public static void RecordCpuPassSkipped(bool noCamera, bool shadowPass, bool modeOff)
        {
            if (noCamera)
                Interlocked.Increment(ref _cpuPassesSkippedNoCamera);
            if (shadowPass)
                Interlocked.Increment(ref _cpuPassesSkippedShadow);
            if (modeOff)
                Interlocked.Increment(ref _cpuPassesSkippedModeOff);
        }

        /// <summary>
        /// Records one GPU Hi-Z pass result.
        /// <paramref name="readbackAvailable"/> indicates whether <paramref name="occluded"/> is accurate
        /// (false for zero-readback strategies; in that case only candidates are meaningful).
        /// </summary>
        public static void RecordGpuPass(int candidates, int occluded, bool readbackAvailable)
        {
            Interlocked.Increment(ref _gpuPassesActive);
            if (candidates > 0)
                Interlocked.Add(ref _gpuCandidates, candidates);
            if (readbackAvailable)
            {
                Interlocked.Increment(ref _gpuPassesWithReadback);
                if (occluded > 0)
                    Interlocked.Add(ref _gpuOccluded, occluded);
            }
        }

        /// <summary>Records the active occlusion mode and submission strategy this frame (latest wins).</summary>
        public static void RecordActiveMode(EOcclusionCullingMode mode, EMeshSubmissionStrategy strategy)
        {
            _lastEffectiveMode = mode;
            _lastSubmissionStrategy = strategy;
        }

        /// <summary>C-CPU-3 scaffold: records one CPU SOC visibility test.</summary>
        public static void RecordCpuSocTested() => Interlocked.Increment(ref _cpuSocTested);

        /// <summary>C-CPU-3 scaffold: records one CPU SOC visibility test that reported occluded.</summary>
        public static void RecordCpuSocCulled() => Interlocked.Increment(ref _cpuSocCulled);

        /// <summary>Records a CPU-query decision by category for the diagnostic distribution.</summary>
        public static void RecordCpuDecision(ECpuDecisionKind kind)
        {
            switch (kind)
            {
                case ECpuDecisionKind.Seed: Interlocked.Increment(ref _cpuDecisionSeed); break;
                case ECpuDecisionKind.Cached: Interlocked.Increment(ref _cpuDecisionCached); break;
                case ECpuDecisionKind.VisibleQuery: Interlocked.Increment(ref _cpuDecisionVisibleQuery); break;
                case ECpuDecisionKind.VisibleHysteresis: Interlocked.Increment(ref _cpuDecisionVisibleHyst); break;
                case ECpuDecisionKind.Probe: Interlocked.Increment(ref _cpuDecisionProbe); break;
                case ECpuDecisionKind.Skip: Interlocked.Increment(ref _cpuDecisionSkip); break;
            }
        }
    }

    /// <summary>Per-decision category for <see cref="OcclusionTelemetry.RecordCpuDecision"/>.</summary>
    public enum ECpuDecisionKind
    {
        Seed,
        Cached,
        VisibleQuery,
        VisibleHysteresis,
        Probe,
        Skip,
    }
}
