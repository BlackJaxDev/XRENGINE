using System;
using System.Collections.Generic;
using System.Threading;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.Occlusion
{
    /// <summary>
    /// Lightweight per-frame occlusion-culling observability.
    /// Counts are accumulated atomically as render passes execute, snapshotted to
    /// last-frame fields by <see cref="BeginFrame"/> (called from RuntimeEngine.Rendering.Stats.BeginFrame),
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
        internal sealed class CpuViewCounters
        {
            public CpuViewCounters(OcclusionViewKey key)
                => Key = key;

            public OcclusionViewKey Key { get; }
            public int CandidateCount;
            public int Submissions;
            public int Resolutions;
            public int Skips;
            public int BudgetSkipped;
            public int ForcedVisible;
            public int CurrentResultAgeFrames;
            public int MaxResultAgeFrames;
            public int RecoveryLatencyFrames;
            public int LastTouchedEpoch;
            public CpuOcclusionViewTelemetrySnapshot LastSnapshot;
            public CpuOcclusionViewTelemetrySnapshot LastActivitySnapshot;

            public void SnapshotAndReset()
            {
                CpuOcclusionViewTelemetrySnapshot snapshot = new(
                    Key,
                    Interlocked.Exchange(ref CandidateCount, 0),
                    Interlocked.Exchange(ref Submissions, 0),
                    Interlocked.Exchange(ref Resolutions, 0),
                    Interlocked.Exchange(ref Skips, 0),
                    Interlocked.Exchange(ref BudgetSkipped, 0),
                    Interlocked.Exchange(ref ForcedVisible, 0),
                    Interlocked.Exchange(ref CurrentResultAgeFrames, 0),
                    Interlocked.Exchange(ref MaxResultAgeFrames, 0),
                    Interlocked.Exchange(ref RecoveryLatencyFrames, 0));
                LastSnapshot = snapshot;
                if (snapshot.CandidateCount != 0 || snapshot.Submissions != 0 ||
                    snapshot.Resolutions != 0 || snapshot.Skips != 0 ||
                    snapshot.BudgetSkipped != 0 || snapshot.ForcedVisible != 0)
                {
                    LastActivitySnapshot = snapshot;
                }
            }
        }

        internal readonly struct CpuViewTelemetryHandle
        {
            private readonly CpuViewCounters? _counters;

            internal CpuViewTelemetryHandle(CpuViewCounters counters)
                => _counters = counters;

            public void RecordPassBegin(int candidateCount)
            {
                CpuViewCounters? counters = Touch();
                if (counters is not null && candidateCount > 0)
                    Interlocked.Add(ref counters.CandidateCount, candidateCount);
            }

            public void RecordSubmission()
            {
                CpuViewCounters? counters = Touch();
                if (counters is not null)
                    Interlocked.Increment(ref counters.Submissions);
            }

            public void RecordResolution(ulong submittedFrame, ulong resolvedFrame)
            {
                CpuViewCounters? counters = Touch();
                if (counters is null)
                    return;
                Interlocked.Increment(ref counters.Resolutions);
                ulong latency = resolvedFrame >= submittedFrame ? resolvedFrame - submittedFrame : 0UL;
                int boundedLatency = latency > int.MaxValue ? int.MaxValue : (int)latency;
                UpdateMax(ref counters.RecoveryLatencyFrames, boundedLatency);
            }

            public void RecordSkip()
            {
                CpuViewCounters? counters = Touch();
                if (counters is not null)
                    Interlocked.Increment(ref counters.Skips);
            }

            public void RecordBudgetSkipped(int count)
            {
                CpuViewCounters? counters = Touch();
                if (counters is not null && count > 0)
                    Interlocked.Add(ref counters.BudgetSkipped, count);
            }

            public void RecordForcedVisible()
            {
                CpuViewCounters? counters = Touch();
                if (counters is not null)
                    Interlocked.Increment(ref counters.ForcedVisible);
            }

            public void RecordResultAge(int ageFrames)
            {
                CpuViewCounters? counters = Touch();
                if (counters is null)
                    return;
                int boundedAge = Math.Clamp(ageFrames, 0, 1_000_000);
                Interlocked.Exchange(ref counters.CurrentResultAgeFrames, boundedAge);
                UpdateMax(ref counters.MaxResultAgeFrames, boundedAge);
            }

            private CpuViewCounters? Touch()
            {
                CpuViewCounters? counters = _counters;
                if (counters is not null)
                    Volatile.Write(ref counters.LastTouchedEpoch, Volatile.Read(ref _cpuViewTelemetryEpoch));
                return counters;
            }

        }

        private static readonly object CpuViewCountersLock = new();
        private static readonly Dictionary<OcclusionViewKey, CpuViewCounters> CpuViewCountersByKey = new();
        private static readonly List<OcclusionViewKey> CpuViewStaleKeys = new(16);
        private static int _cpuViewTelemetryEpoch;

        // CPU-query path (hardware AnySamplesPassedConservative, decision on CPU).
        private static int _cpuTested;
        private static int _cpuCulled;
        private static int _cpuPassesActive;
        private static int _cpuPassesSkippedNoCamera;
        private static int _cpuPassesSkippedShadow;
        private static int _cpuPassesSkippedDepthNormalPrePass;
        private static int _cpuPassesSkippedModeOff;
        private static int _lastFrameCpuTested;
        private static int _lastFrameCpuCulled;
        private static int _lastFrameCpuPassesActive;
        private static int _lastFrameCpuPassesSkippedNoCamera;
        private static int _lastFrameCpuPassesSkippedShadow;
        private static int _lastFrameCpuPassesSkippedDepthNormalPrePass;
        private static int _lastFrameCpuPassesSkippedModeOff;

        // GPU Hi-Z path (compute cull on GPU; counts only available when readback is permitted).
        private static int _gpuCandidates;
        private static int _gpuOccluded;
        private static int _gpuPassesActive;
        private static int _gpuPassesWithReadback;
        // C-GPU-3 passthrough: passes that bypassed the Hi-Z cull because temporal
        // state was invalidated this frame (scene mutation or large camera jump).
        // The pyramid built from stale depth is unsafe to consume on those frames
        // because newly added meshes have no depth representation yet, so we let
        // every candidate through. Next frame's pyramid is built from the freshly
        // rendered depth and the cull resumes normally.
        private static int _gpuPassesPassthroughDirty;
        private static int _gpuDepthSourceHistory;
        private static int _gpuDepthSourceCurrent;
        // GPU Hi-Z passes that bailed out before producing a cull decision. Indexed by
        // EGpuHiZSkipReason ordinal; addressed via Interlocked for thread safety. When
        // this bucket is non-zero, HiZ is configured but not running, and the user has
        // no other way to tell from telemetry. The OcclusionPanel surfaces the breakdown.
        private static readonly int[] _gpuHiZSkipReasons = new int[(int)EGpuHiZSkipReason.Count];
        private static readonly int[] _lastFrameGpuHiZSkipReasons = new int[(int)EGpuHiZSkipReason.Count];
        private static int _lastFrameGpuCandidates;
        private static int _lastFrameGpuOccluded;
        private static int _lastFrameGpuPassesActive;
        private static int _lastFrameGpuPassesWithReadback;
        private static int _lastFrameGpuPassesPassthroughDirty;
        private static int _lastFrameGpuDepthSourceHistory;
        private static int _lastFrameGpuDepthSourceCurrent;

        // Effective mode observed during the completed frame. RenderCPU can be
        // called again later for no-camera passes; aggregate the frame so those
        // disabled passes do not hide an earlier active path in the debug UI.
        private static readonly object ActiveModeLock = new();
        private static EOcclusionCullingMode _currentEffectiveMode = EOcclusionCullingMode.Disabled;
        private static EMeshSubmissionStrategy _currentSubmissionStrategy;
        private static EOcclusionCullingMode _lastEffectiveMode = EOcclusionCullingMode.Disabled;
        private static EMeshSubmissionStrategy _lastSubmissionStrategy;

        // CPU SOC pass observability.
        private static int _cpuSocTested;
        private static int _cpuSocCulled;
        private static int _cpuSocOccludersSelected;
        private static int _cpuSocOccludersRasterized;
        private static int _cpuSocTilesClosed;
        private static long _cpuSocBeginMicros;
        private static long _cpuSocRasterMicros;
        private static long _cpuSocTestMicros;
        private static int _cpuSocForceVisible;
        private static int _cpuSocSelfOccluderSkipped;
        private static int _lastFrameCpuSocTested;
        private static int _lastFrameCpuSocCulled;
        private static int _lastFrameCpuSocOccludersSelected;
        private static int _lastFrameCpuSocOccludersRasterized;
        private static int _lastFrameCpuSocTilesClosed;
        private static long _lastFrameCpuSocBeginMicros;
        private static long _lastFrameCpuSocRasterMicros;
        private static long _lastFrameCpuSocTestMicros;
        private static int _lastFrameCpuSocForceVisible;
        private static int _lastFrameCpuSocSelfOccluderSkipped;

        // Per-decision distribution for the CPU-query path. This is the diagnostic that
        // tells us whether occlusion is failing at the *query* level (hardware reports
        // samples passing on a mesh that shouldn't be visible) or at the *decision* level
        // (hysteresis / retest / cache reuse keeping it Visible).
        private static int _cpuDecisionSeed;             // First-seen command (no query yet)
        private static int _cpuDecisionCached;           // Same-frame cache hit (prepass+color sharing)
        private static int _cpuDecisionVisibleQuery;    // Query last reported samples-passed
        private static int _cpuDecisionVisibleHyst;     // Query reported zero, hysteresis still visible
        private static int _cpuDecisionProbe;            // ProbeOnly (periodic depth-proxy retest)
        private static int _cpuDecisionSkip;             // Skip (fully occluded)
        private static int _cpuDecisionForcedVisible;    // Conservative-visible policy forced the draw

        // CpuQueryAsync (GPU dispatch path) — counts proxy-AABB hardware queries submitted
        // and resolved per frame. These are the canonical "is CpuQueryAsync actually doing
        // anything?" signals on the GPU dispatch path, where the temporal filter alone
        // doesn't tell you whether queries were ever submitted.
        private static int _cpuQueryAsyncSubmitted;
        private static int _cpuQueryAsyncResolved;
        private static int _cpuQueryAsyncOccluded;
        private static int _lastFrameCpuQueryAsyncSubmitted;
        private static int _lastFrameCpuQueryAsyncResolved;
        private static int _lastFrameCpuQueryAsyncOccluded;

        private static int _lastFrameCpuDecisionSeed;
        private static int _lastFrameCpuDecisionCached;
        private static int _lastFrameCpuDecisionVisibleQuery;
        private static int _lastFrameCpuDecisionVisibleHyst;
        private static int _lastFrameCpuDecisionProbe;
        private static int _lastFrameCpuDecisionSkip;
        private static int _lastFrameCpuDecisionForcedVisible;

        private static int _cpuGlobalConservativeFrames;
        private static int _lastFrameCpuGlobalConservativeFrames;
        private static int _cpuPendingQueries;
        private static int _lastFrameCpuPendingQueries;
        private static int _cpuQueryLatencySamples;
        private static int _cpuQueryLatencyTotalFrames;
        private static int _cpuQueryLatencyMaxFrames;
        private static int _lastFrameCpuQueryLatencySamples;
        private static int _lastFrameCpuQueryLatencyTotalFrames;
        private static int _lastFrameCpuQueryLatencyMaxFrames;
        private static int _cpuUnsupportedStereoQueryMode;
        private static int _lastFrameCpuUnsupportedStereoQueryMode;
        private static int _currentCpuMotionTier = (int)ECpuOcclusionMotionTier.Stable;
        private static int _lastCpuMotionTier = (int)ECpuOcclusionMotionTier.Stable;
        private static int _currentCpuViewScope = (int)EOcclusionViewScope.MonoDesktop;
        private static int _lastCpuViewScope = (int)EOcclusionViewScope.MonoDesktop;
        private static readonly int[] _cpuForcedVisibleReasons = new int[Enum.GetValues<ECpuOcclusionForceVisibleReason>().Length];
        private static readonly int[] _lastFrameCpuForcedVisibleReasons = new int[Enum.GetValues<ECpuOcclusionForceVisibleReason>().Length];
        private static readonly int[] _cpuQuerySubmittedReasons = new int[Enum.GetValues<ECpuOcclusionQueryReason>().Length];
        private static readonly int[] _lastFrameCpuQuerySubmittedReasons = new int[Enum.GetValues<ECpuOcclusionQueryReason>().Length];
        private static readonly int[] _cpuQueryResolvedReasons = new int[Enum.GetValues<ECpuOcclusionQueryReason>().Length];
        private static readonly int[] _lastFrameCpuQueryResolvedReasons = new int[Enum.GetValues<ECpuOcclusionQueryReason>().Length];
        private static readonly int[] _cpuBudgetSkippedReasons = new int[Enum.GetValues<ECpuOcclusionQueryReason>().Length];
        private static readonly int[] _lastFrameCpuBudgetSkippedReasons = new int[Enum.GetValues<ECpuOcclusionQueryReason>().Length];

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
        /// <summary>Last completed frame: RenderCPU calls that skipped occlusion because the pass was a depth-normal prepass.</summary>
        public static int CpuPassesSkippedDepthNormalPrePass => _lastFrameCpuPassesSkippedDepthNormalPrePass;
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
        /// <summary>Last completed frame: GPU Hi-Z passes that bypassed cull due to dirty temporal state (C-GPU-3).</summary>
        public static int GpuPassesPassthroughDirty => _lastFrameGpuPassesPassthroughDirty;
        /// <summary>Last completed frame: GPU Hi-Z passes that sampled previous-frame history depth.</summary>
        public static int GpuDepthSourceHistory => _lastFrameGpuDepthSourceHistory;
        /// <summary>Last completed frame: GPU Hi-Z passes that sampled current-frame depth.</summary>
        public static int GpuDepthSourceCurrent => _lastFrameGpuDepthSourceCurrent;
        /// <summary>True when at least one GPU Hi-Z pass produced an accurate count this frame.</summary>
        public static bool LastFrameGpuOcclusionAvailable => _lastFrameGpuPassesWithReadback > 0;
        /// <summary>Last completed frame: GPU Hi-Z passes that exited before producing a cull decision, by reason.</summary>
        public static int GetGpuHiZSkippedCount(EGpuHiZSkipReason reason)
            => _lastFrameGpuHiZSkipReasons[(int)reason];
        /// <summary>Total GPU Hi-Z bail-outs this completed frame across all reasons.</summary>
        public static int GpuHiZSkippedTotal
        {
            get
            {
                int total = 0;
                for (int i = 0; i < _lastFrameGpuHiZSkipReasons.Length; ++i)
                    total += _lastFrameGpuHiZSkipReasons[i];
                return total;
            }
        }

        public static EOcclusionCullingMode LastEffectiveMode => _lastEffectiveMode;
        public static EMeshSubmissionStrategy LastSubmissionStrategy => _lastSubmissionStrategy;

        /// <summary>Last completed frame: SOC visibility tests run.</summary>
        public static int CpuSocTested => _lastFrameCpuSocTested;
        /// <summary>Last completed frame: SOC visibility tests that reported occluded.</summary>
        public static int CpuSocCulled => _lastFrameCpuSocCulled;
        /// <summary>Last completed frame: SOC occluder candidates selected for rasterization.</summary>
        public static int CpuSocOccludersSelected => _lastFrameCpuSocOccludersSelected;
        /// <summary>Last completed frame: SOC occluders that wrote at least one covered pixel.</summary>
        public static int CpuSocOccludersRasterized => _lastFrameCpuSocOccludersRasterized;
        /// <summary>Last completed frame: SOC tiles that reached full coverage.</summary>
        public static int CpuSocTilesClosed => _lastFrameCpuSocTilesClosed;
        /// <summary>Last completed frame: SOC frame setup time in milliseconds.</summary>
        public static double CpuSocBeginMilliseconds => _lastFrameCpuSocBeginMicros / 1000.0;
        /// <summary>Last completed frame: SOC occluder selection plus rasterization time in milliseconds.</summary>
        public static double CpuSocRasterMilliseconds => _lastFrameCpuSocRasterMicros / 1000.0;
        /// <summary>Last completed frame: SOC AABB test time in milliseconds.</summary>
        public static double CpuSocTestMilliseconds => _lastFrameCpuSocTestMicros / 1000.0;
        /// <summary>Last completed frame: true when SOC was forced visible for diagnostics.</summary>
        public static bool CpuSocForceVisible => _lastFrameCpuSocForceVisible != 0;
        /// <summary>Last completed frame: SOC tests skipped because the command supplied its own occluder.</summary>
        public static int CpuSocSelfOccluderSkipped => _lastFrameCpuSocSelfOccluderSkipped;

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
        /// <summary>Last completed frame: visible decisions forced by conservative policy.</summary>
        public static int CpuDecisionForcedVisible => _lastFrameCpuDecisionForcedVisible;

        public static ECpuOcclusionMotionTier CpuMotionTier => (ECpuOcclusionMotionTier)_lastCpuMotionTier;
        public static EOcclusionViewScope CpuActiveViewScope => (EOcclusionViewScope)_lastCpuViewScope;
        public static int CpuGlobalConservativeFrames => _lastFrameCpuGlobalConservativeFrames;
        public static int CpuPendingQueries => _lastFrameCpuPendingQueries;
        public static int CpuQueryLatencySamples => _lastFrameCpuQueryLatencySamples;
        public static int CpuQueryLatencyMaxFrames => _lastFrameCpuQueryLatencyMaxFrames;
        public static double CpuQueryLatencyAverageFrames
            => _lastFrameCpuQueryLatencySamples > 0
                ? (double)_lastFrameCpuQueryLatencyTotalFrames / _lastFrameCpuQueryLatencySamples
                : 0.0;
        public static int CpuUnsupportedStereoQueryMode => _lastFrameCpuUnsupportedStereoQueryMode;

        public static int GetCpuForcedVisibleCount(ECpuOcclusionForceVisibleReason reason)
            => GetCount(_lastFrameCpuForcedVisibleReasons, (int)reason);

        public static int GetCpuQuerySubmittedCount(ECpuOcclusionQueryReason reason)
            => GetCount(_lastFrameCpuQuerySubmittedReasons, (int)reason);

        public static int GetCpuQueryResolvedCount(ECpuOcclusionQueryReason reason)
            => GetCount(_lastFrameCpuQueryResolvedReasons, (int)reason);

        public static int GetCpuBudgetSkippedCount(ECpuOcclusionQueryReason reason)
            => GetCount(_lastFrameCpuBudgetSkippedReasons, (int)reason);

        public static int CpuForcedVisibleTotal => Sum(_lastFrameCpuForcedVisibleReasons);
        public static int CpuQuerySubmittedTotal => Sum(_lastFrameCpuQuerySubmittedReasons);
        public static int CpuQueryResolvedTotal => Sum(_lastFrameCpuQueryResolvedReasons);
        public static int CpuBudgetSkippedTotal => Sum(_lastFrameCpuBudgetSkippedReasons);

        /// <summary>
        /// Returns a diagnostic copy of the last completed frame's per-output
        /// hardware-query telemetry. This is intentionally an on-demand allocation;
        /// render-path recording updates cached counters without allocating.
        /// </summary>
        public static CpuOcclusionViewTelemetrySnapshot[] GetCpuViewSnapshots()
        {
            lock (CpuViewCountersLock)
            {
                var snapshots = new CpuOcclusionViewTelemetrySnapshot[CpuViewCountersByKey.Count];
                int index = 0;
                foreach (CpuViewCounters counters in CpuViewCountersByKey.Values)
                    snapshots[index++] = counters.LastSnapshot;
                return snapshots;
            }
        }

        /// <summary>
        /// Copies last-frame keyed telemetry into caller-owned storage without
        /// allocating. Returns the number copied; <see cref="CpuViewSnapshotCount"/>
        /// reports the capacity required for a complete copy.
        /// </summary>
        public static int CopyLastFrameCpuViewSnapshots(Span<CpuOcclusionViewTelemetrySnapshot> destination)
        {
            lock (CpuViewCountersLock)
            {
                int copied = 0;
                foreach (CpuViewCounters counters in CpuViewCountersByKey.Values)
                {
                    if (copied >= destination.Length)
                        break;
                    destination[copied++] = counters.LastSnapshot;
                }
                return copied;
            }
        }

        /// <summary>
        /// Copies the most recent active sample for every keyed POV. This is used
        /// by asynchronous output ledgers whose capture boundary may follow a
        /// different render-frame boundary than the queried output.
        /// </summary>
        public static int CopyLastActiveCpuViewSnapshots(Span<CpuOcclusionViewTelemetrySnapshot> destination)
        {
            lock (CpuViewCountersLock)
            {
                int copied = 0;
                foreach (CpuViewCounters counters in CpuViewCountersByKey.Values)
                {
                    if (copied >= destination.Length)
                        break;
                    destination[copied++] = counters.LastActivitySnapshot;
                }
                return copied;
            }
        }

        public static int CpuViewSnapshotCount
        {
            get
            {
                lock (CpuViewCountersLock)
                    return CpuViewCountersByKey.Count;
            }
        }

        internal static CpuViewTelemetryHandle GetCpuViewTelemetryHandle(OcclusionViewKey key)
            => new(GetCpuViewCounters(key));

        /// <summary>Last completed frame: CpuQueryAsync GPU-dispatch proxy-AABB queries submitted.</summary>
        public static int CpuQueryAsyncSubmitted => _lastFrameCpuQueryAsyncSubmitted;
        /// <summary>Last completed frame: CpuQueryAsync GPU-dispatch queries whose results were resolved.</summary>
        public static int CpuQueryAsyncResolved => _lastFrameCpuQueryAsyncResolved;
        /// <summary>Last completed frame: CpuQueryAsync GPU-dispatch candidates the temporal filter removed.</summary>
        public static int CpuQueryAsyncOccluded => _lastFrameCpuQueryAsyncOccluded;

        /// <summary>Called by RuntimeEngine.Rendering.Stats.BeginFrame to snapshot and reset counters.</summary>
        public static void BeginFrame()
        {
            lock (CpuViewCountersLock)
            {
                int epoch = ++_cpuViewTelemetryEpoch;
                CpuViewStaleKeys.Clear();
                foreach (KeyValuePair<OcclusionViewKey, CpuViewCounters> pair in CpuViewCountersByKey)
                {
                    pair.Value.SnapshotAndReset();
                    if (epoch - Volatile.Read(ref pair.Value.LastTouchedEpoch) > 240)
                        CpuViewStaleKeys.Add(pair.Key);
                }
                foreach (OcclusionViewKey key in CpuViewStaleKeys)
                    CpuViewCountersByKey.Remove(key);
            }

            _lastFrameCpuTested = _cpuTested;
            _lastFrameCpuCulled = _cpuCulled;
            _lastFrameCpuPassesActive = _cpuPassesActive;
            _lastFrameCpuPassesSkippedNoCamera = _cpuPassesSkippedNoCamera;
            _lastFrameCpuPassesSkippedShadow = _cpuPassesSkippedShadow;
            _lastFrameCpuPassesSkippedDepthNormalPrePass = _cpuPassesSkippedDepthNormalPrePass;
            _lastFrameCpuPassesSkippedModeOff = _cpuPassesSkippedModeOff;
            _lastFrameGpuCandidates = _gpuCandidates;
            _lastFrameGpuOccluded = _gpuOccluded;
            _lastFrameGpuPassesActive = _gpuPassesActive;
            _lastFrameGpuPassesWithReadback = _gpuPassesWithReadback;
            _lastFrameGpuPassesPassthroughDirty = _gpuPassesPassthroughDirty;
            _lastFrameGpuDepthSourceHistory = _gpuDepthSourceHistory;
            _lastFrameGpuDepthSourceCurrent = _gpuDepthSourceCurrent;
            for (int i = 0; i < _gpuHiZSkipReasons.Length; ++i)
            {
                _lastFrameGpuHiZSkipReasons[i] = _gpuHiZSkipReasons[i];
                _gpuHiZSkipReasons[i] = 0;
            }
            lock (ActiveModeLock)
            {
                _lastEffectiveMode = _currentEffectiveMode;
                _lastSubmissionStrategy = _currentSubmissionStrategy;
                _currentEffectiveMode = EOcclusionCullingMode.Disabled;
                _currentSubmissionStrategy = default;
            }

            _lastFrameCpuSocTested = _cpuSocTested;
            _lastFrameCpuSocCulled = _cpuSocCulled;
            _lastFrameCpuSocOccludersSelected = _cpuSocOccludersSelected;
            _lastFrameCpuSocOccludersRasterized = _cpuSocOccludersRasterized;
            _lastFrameCpuSocTilesClosed = _cpuSocTilesClosed;
            _lastFrameCpuSocBeginMicros = _cpuSocBeginMicros;
            _lastFrameCpuSocRasterMicros = _cpuSocRasterMicros;
            _lastFrameCpuSocTestMicros = _cpuSocTestMicros;
            _lastFrameCpuSocForceVisible = _cpuSocForceVisible;
            _lastFrameCpuSocSelfOccluderSkipped = _cpuSocSelfOccluderSkipped;

            _lastFrameCpuDecisionSeed = _cpuDecisionSeed;
            _lastFrameCpuDecisionCached = _cpuDecisionCached;
            _lastFrameCpuDecisionVisibleQuery = _cpuDecisionVisibleQuery;
            _lastFrameCpuDecisionVisibleHyst = _cpuDecisionVisibleHyst;
            _lastFrameCpuDecisionProbe = _cpuDecisionProbe;
            _lastFrameCpuDecisionSkip = _cpuDecisionSkip;
            _lastFrameCpuDecisionForcedVisible = _cpuDecisionForcedVisible;

            _lastFrameCpuGlobalConservativeFrames = _cpuGlobalConservativeFrames;
            _lastFrameCpuPendingQueries = _cpuPendingQueries;
            _lastFrameCpuQueryLatencySamples = _cpuQueryLatencySamples;
            _lastFrameCpuQueryLatencyTotalFrames = _cpuQueryLatencyTotalFrames;
            _lastFrameCpuQueryLatencyMaxFrames = _cpuQueryLatencyMaxFrames;
            _lastFrameCpuUnsupportedStereoQueryMode = _cpuUnsupportedStereoQueryMode;
            _lastCpuMotionTier = _currentCpuMotionTier;
            _lastCpuViewScope = _currentCpuViewScope;
            SnapshotAndReset(_cpuForcedVisibleReasons, _lastFrameCpuForcedVisibleReasons);
            SnapshotAndReset(_cpuQuerySubmittedReasons, _lastFrameCpuQuerySubmittedReasons);
            SnapshotAndReset(_cpuQueryResolvedReasons, _lastFrameCpuQueryResolvedReasons);
            SnapshotAndReset(_cpuBudgetSkippedReasons, _lastFrameCpuBudgetSkippedReasons);

            _lastFrameCpuQueryAsyncSubmitted = _cpuQueryAsyncSubmitted;
            _lastFrameCpuQueryAsyncResolved = _cpuQueryAsyncResolved;
            _lastFrameCpuQueryAsyncOccluded = _cpuQueryAsyncOccluded;

            _cpuTested = 0;
            _cpuCulled = 0;
            _cpuPassesActive = 0;
            _cpuPassesSkippedNoCamera = 0;
            _cpuPassesSkippedShadow = 0;
            _cpuPassesSkippedDepthNormalPrePass = 0;
            _cpuPassesSkippedModeOff = 0;
            _gpuCandidates = 0;
            _gpuOccluded = 0;
            _gpuPassesActive = 0;
            _gpuPassesWithReadback = 0;
            _gpuPassesPassthroughDirty = 0;
            _gpuDepthSourceHistory = 0;
            _gpuDepthSourceCurrent = 0;
            _cpuSocTested = 0;
            _cpuSocCulled = 0;
            _cpuSocOccludersSelected = 0;
            _cpuSocOccludersRasterized = 0;
            _cpuSocTilesClosed = 0;
            _cpuSocBeginMicros = 0;
            _cpuSocRasterMicros = 0;
            _cpuSocTestMicros = 0;
            _cpuSocForceVisible = 0;
            _cpuSocSelfOccluderSkipped = 0;
            _cpuDecisionSeed = 0;
            _cpuDecisionCached = 0;
            _cpuDecisionVisibleQuery = 0;
            _cpuDecisionVisibleHyst = 0;
            _cpuDecisionProbe = 0;
            _cpuDecisionSkip = 0;
            _cpuDecisionForcedVisible = 0;
            _cpuGlobalConservativeFrames = 0;
            _cpuPendingQueries = 0;
            _cpuQueryLatencySamples = 0;
            _cpuQueryLatencyTotalFrames = 0;
            _cpuQueryLatencyMaxFrames = 0;
            _cpuUnsupportedStereoQueryMode = 0;

            _cpuQueryAsyncSubmitted = 0;
            _cpuQueryAsyncResolved = 0;
            _cpuQueryAsyncOccluded = 0;
        }

        /// <summary>Records a CpuQueryAsync GPU-dispatch proxy-AABB query submission (one per candidate).</summary>
        public static void RecordCpuQueryAsyncSubmitted(int count = 1)
        {
            if (count > 0)
                Interlocked.Add(ref _cpuQueryAsyncSubmitted, count);
        }

        /// <summary>Records a CpuQueryAsync GPU-dispatch query result resolution.</summary>
        public static void RecordCpuQueryAsyncResolved(int count = 1)
        {
            if (count > 0)
                Interlocked.Add(ref _cpuQueryAsyncResolved, count);
        }

        /// <summary>Records candidates the CpuQueryAsync temporal filter removed this frame.</summary>
        public static void RecordCpuQueryAsyncOccluded(int count = 1)
        {
            if (count > 0)
                Interlocked.Add(ref _cpuQueryAsyncOccluded, count);
        }

        public static void RecordCpuMotionTier(ECpuOcclusionMotionTier tier)
            => Interlocked.Exchange(ref _currentCpuMotionTier, (int)tier);

        public static void RecordCpuViewPassBegin(OcclusionViewKey key, int candidateCount)
        {
            CpuViewCounters counters = GetCpuViewCounters(key);
            if (candidateCount > 0)
                Interlocked.Add(ref counters.CandidateCount, candidateCount);
        }

        public static void RecordCpuViewSubmission(OcclusionViewKey key)
            => Interlocked.Increment(ref GetCpuViewCounters(key).Submissions);

        public static void RecordCpuViewResolution(OcclusionViewKey key, ulong submittedFrame, ulong resolvedFrame)
        {
            CpuViewCounters counters = GetCpuViewCounters(key);
            Interlocked.Increment(ref counters.Resolutions);
            ulong latency = resolvedFrame >= submittedFrame ? resolvedFrame - submittedFrame : 0UL;
            int boundedLatency = latency > int.MaxValue ? int.MaxValue : (int)latency;
            UpdateMax(ref counters.RecoveryLatencyFrames, boundedLatency);
        }

        public static void RecordCpuViewSkip(OcclusionViewKey key)
            => Interlocked.Increment(ref GetCpuViewCounters(key).Skips);

        public static void RecordCpuViewBudgetSkipped(OcclusionViewKey key, int count)
        {
            if (count > 0)
                Interlocked.Add(ref GetCpuViewCounters(key).BudgetSkipped, count);
        }

        public static void RecordCpuViewForcedVisible(OcclusionViewKey key)
            => Interlocked.Increment(ref GetCpuViewCounters(key).ForcedVisible);

        public static void RecordCpuViewResultAge(OcclusionViewKey key, int ageFrames)
        {
            CpuViewCounters counters = GetCpuViewCounters(key);
            int boundedAge = Math.Clamp(ageFrames, 0, 1_000_000);
            Interlocked.Exchange(ref counters.CurrentResultAgeFrames, boundedAge);
            UpdateMax(ref counters.MaxResultAgeFrames, boundedAge);
        }

        private static CpuViewCounters GetCpuViewCounters(OcclusionViewKey key)
        {
            lock (CpuViewCountersLock)
            {
                if (!CpuViewCountersByKey.TryGetValue(key, out CpuViewCounters? counters))
                {
                    counters = new CpuViewCounters(key);
                    CpuViewCountersByKey.Add(key, counters);
                }
                Volatile.Write(ref counters.LastTouchedEpoch, _cpuViewTelemetryEpoch);
                return counters;
            }
        }

        public static void RecordCpuActiveViewScope(EOcclusionViewScope scope)
            => Interlocked.Exchange(ref _currentCpuViewScope, (int)scope);

        public static void RecordCpuGlobalConservativeFrame(ECpuOcclusionForceVisibleReason reason)
        {
            _ = reason;
            Interlocked.Increment(ref _cpuGlobalConservativeFrames);
        }

        public static void RecordCpuForcedVisible(ECpuOcclusionForceVisibleReason reason, int count = 1)
        {
            if (count <= 0)
                return;

            AddToBucket(_cpuForcedVisibleReasons, (int)reason, count);
        }

        public static void RecordCpuPendingQueries(int count)
        {
            if (count > 0)
                Interlocked.Add(ref _cpuPendingQueries, count);
        }

        public static void RecordCpuQuerySubmitted(ECpuOcclusionQueryReason reason, int count = 1)
        {
            if (count > 0)
                AddToBucket(_cpuQuerySubmittedReasons, (int)reason, count);
        }

        public static void RecordCpuQueryResolved(ECpuOcclusionQueryReason reason, ulong latencyFrames)
        {
            AddToBucket(_cpuQueryResolvedReasons, (int)reason, 1);
            int latency = latencyFrames > int.MaxValue ? int.MaxValue : (int)latencyFrames;
            Interlocked.Increment(ref _cpuQueryLatencySamples);
            Interlocked.Add(ref _cpuQueryLatencyTotalFrames, latency);
            UpdateMax(ref _cpuQueryLatencyMaxFrames, latency);
        }

        public static void RecordCpuBudgetSkipped(ECpuOcclusionQueryReason reason, int count = 1)
        {
            if (count > 0)
                AddToBucket(_cpuBudgetSkippedReasons, (int)reason, count);
        }

        public static void RecordCpuUnsupportedStereoQueryMode()
            => Interlocked.Increment(ref _cpuUnsupportedStereoQueryMode);

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
        public static void RecordCpuPassSkipped(bool noCamera, bool shadowPass, bool depthNormalPrePass, bool modeOff)
        {
            if (noCamera)
                Interlocked.Increment(ref _cpuPassesSkippedNoCamera);
            if (shadowPass)
                Interlocked.Increment(ref _cpuPassesSkippedShadow);
            if (depthNormalPrePass)
                Interlocked.Increment(ref _cpuPassesSkippedDepthNormalPrePass);
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

        /// <summary>Records which depth source a GPU Hi-Z pass sampled.</summary>
        public static void RecordGpuDepthSource(bool history)
        {
            if (history)
                Interlocked.Increment(ref _gpuDepthSourceHistory);
            else
                Interlocked.Increment(ref _gpuDepthSourceCurrent);
        }

        /// <summary>
        /// C-GPU-3: records a GPU Hi-Z pass that bypassed cull because temporal state
        /// was invalidated (scene mutation or large camera jump). Always paired with
        /// <see cref="RecordGpuPass"/> reporting <c>occluded=0</c>.
        /// </summary>
        public static void RecordGpuPassthroughDirty() =>
            Interlocked.Increment(ref _gpuPassesPassthroughDirty);

        /// <summary>
        /// Records a GPU Hi-Z pass that exited before producing a cull decision (missing
        /// shaders, missing depth texture, unsupported depth view, etc.). Surfaces in the
        /// occlusion panel as a "skipped" bucket so users can tell HiZ is configured but
        /// not running this frame.
        /// </summary>
        public static void RecordGpuHiZSkipped(EGpuHiZSkipReason reason)
        {
            int idx = (int)reason;
            if ((uint)idx >= (uint)_gpuHiZSkipReasons.Length)
                return;
            Interlocked.Increment(ref _gpuHiZSkipReasons[idx]);
        }

        /// <summary>Records the active occlusion mode and submission strategy this frame.</summary>
        public static void RecordActiveMode(EOcclusionCullingMode mode, EMeshSubmissionStrategy strategy)
        {
            lock (ActiveModeLock)
            {
                if (GetModePriority(mode) < GetModePriority(_currentEffectiveMode))
                    return;

                _currentEffectiveMode = mode;
                _currentSubmissionStrategy = strategy;
            }
        }

        private static int GetModePriority(EOcclusionCullingMode mode)
        {
            return mode switch
            {
                EOcclusionCullingMode.CpuSoftwareOcclusion => 3,
                EOcclusionCullingMode.CpuQueryAsync => 3,
                EOcclusionCullingMode.GpuHiZ => 2,
                EOcclusionCullingMode.Disabled => 0,
                _ => 1,
            };
        }

        /// <summary>Records one CPU SOC visibility test.</summary>
        public static void RecordCpuSocTested() => Interlocked.Increment(ref _cpuSocTested);

        /// <summary>Records one CPU SOC test skipped to avoid self-occluding the command that populated the mask.</summary>
        public static void RecordCpuSocSelfOccluderSkipped() => Interlocked.Increment(ref _cpuSocSelfOccluderSkipped);

        /// <summary>Records one CPU SOC visibility test that reported occluded.</summary>
        public static void RecordCpuSocCulled() => Interlocked.Increment(ref _cpuSocCulled);

        /// <summary>Records CPU SOC frame setup timing and diagnostic force-visible state.</summary>
        public static void RecordCpuSocFrameBegin(double milliseconds, bool forceVisible)
        {
            Interlocked.Add(ref _cpuSocBeginMicros, ToMicroseconds(milliseconds));
            if (forceVisible)
                Interlocked.Exchange(ref _cpuSocForceVisible, 1);
        }

        /// <summary>Records CPU SOC occluder selection and rasterization summary.</summary>
        public static void RecordCpuSocOccluders(int selected, int rasterized, int tilesClosed, double milliseconds)
        {
            if (selected > 0)
                Interlocked.Add(ref _cpuSocOccludersSelected, selected);
            if (rasterized > 0)
                Interlocked.Add(ref _cpuSocOccludersRasterized, rasterized);
            if (tilesClosed > 0)
                Interlocked.Add(ref _cpuSocTilesClosed, tilesClosed);
            Interlocked.Add(ref _cpuSocRasterMicros, ToMicroseconds(milliseconds));
        }

        /// <summary>Records one CPU SOC visibility decision.</summary>
        public static void RecordCpuSocTest(double milliseconds, bool culled)
        {
            Interlocked.Increment(ref _cpuSocTested);
            if (culled)
                Interlocked.Increment(ref _cpuSocCulled);
            Interlocked.Add(ref _cpuSocTestMicros, ToMicroseconds(milliseconds));
        }

        private static long ToMicroseconds(double milliseconds)
            => (long)Math.Round(milliseconds * 1000.0);

        private static void AddToBucket(int[] buckets, int index, int count)
        {
            if ((uint)index >= (uint)buckets.Length)
                return;
            Interlocked.Add(ref buckets[index], count);
        }

        private static int GetCount(int[] buckets, int index)
            => (uint)index < (uint)buckets.Length ? buckets[index] : 0;

        private static int Sum(int[] buckets)
        {
            int total = 0;
            for (int i = 0; i < buckets.Length; i++)
                total += buckets[i];
            return total;
        }

        private static void SnapshotAndReset(int[] source, int[] destination)
        {
            int count = Math.Min(source.Length, destination.Length);
            for (int i = 0; i < count; i++)
                destination[i] = Interlocked.Exchange(ref source[i], 0);
        }

        private static void UpdateMax(ref int target, int value)
        {
            int observed;
            do
            {
                observed = Volatile.Read(ref target);
                if (value <= observed)
                    return;
            }
            while (Interlocked.CompareExchange(ref target, value, observed) != observed);
        }

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
                case ECpuDecisionKind.ForcedVisible: Interlocked.Increment(ref _cpuDecisionForcedVisible); break;
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
        ForcedVisible,
    }

    /// <summary>
    /// Reasons a GPU Hi-Z pass bailed before producing a cull decision. Kept compact and
    /// dense so it can index a per-frame counter array atomically. New reasons append.
    /// </summary>
    public enum EGpuHiZSkipReason
    {
        MissingShaders,
        MissingPipeline,
        NoCamera,
        NoDepthTexture,
        UnsupportedDepthView,
        MissingBuffers,
        ExternalVrSharedVisibility,
        Count,
    }
}
