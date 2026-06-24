using System;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading;

namespace XREngine.Rendering
{
    /// <summary>
    /// P3 instrumentation for the GPU-indirect zero-readback hot path.
    ///
    /// Documented in docs/work/design/rendering/render-submission-perf-debug-plan.md §3
    /// "Required new logging (P3)" and §4 "P3 hypothesis". All counters are opt-in via
    /// <c>XRE_P3_LOGGING=1</c>; bisect toggles are independent env vars.
    ///
    /// Goal: confirm or reject the hypothesis that the zero-readback bucket fan-out
    /// (<c>materialSlotIds × MaterialTierCount × renderPasses</c>) dominates render-thread
    /// cost on two-Sponza/no-lights. Compare CpuDirect vs ZeroReadback head-to-head.
    /// </summary>
    internal static class P3Diagnostics
    {
        // -----------------------------------------------------------------
        // Env-var flags (read once at startup).
        // -----------------------------------------------------------------

        /// <summary>Master switch for census + state-bind counter logging.</summary>
        public static readonly bool LoggingEnabled = ReadFlag(XREngineEnvironmentVariables.P3Logging);

        /// <summary>
        /// Run the slot/tier loop but skip the final <c>MultiDrawElementsIndirect[Count]</c>
        /// inside <c>DispatchRenderIndirectCountBucket</c>. If fps recovers near CpuDirect,
        /// GL/driver submission cost dominates (validates P3-A).
        /// </summary>
        public static readonly bool BucketLoopDryRun = ReadFlag(XREngineEnvironmentVariables.BucketLoopDryRun);

        /// <summary>
        /// Short-circuit <c>GPUScene.SwapCommandBuffers</c> when the command-content version
        /// has not changed since the last swap. Validates O-6 cheaply before committing the
        /// full version-stamp gate.
        /// </summary>
        public static readonly bool SkipCommandSwapIfClean = ReadFlag(XREngineEnvironmentVariables.SkipCommandSwapIfClean);

        /// <summary>
        /// Skip the bucket-loop iteration when the CPU-side active-slot mask says the bucket
        /// is empty. Requires O-18 (active-slot CPU mask) to be wired; reserved for that
        /// implementation phase. Reading the flag now lets the upcoming O-18 patch be a no-op
        /// to integrate.
        /// </summary>
        public static readonly bool BucketLoopSkipEmpty = ReadFlag(XREngineEnvironmentVariables.BucketLoopSkipEmpty);

        /// <summary>
        /// Force every (slot, tier) iteration to dispatch a single bucket index. Strictly for
        /// isolating per-bucket fan-out overhead from per-draw cost. Reserved for the
        /// implementation phase alongside O-18.
        /// </summary>
        public static readonly bool ForceSingleBucket = ReadFlag(XREngineEnvironmentVariables.ForceSingleBucket);

        /// <summary>
        /// Diagnostic-only sync after each material-tier
        /// <c>MultiDrawElementsIndirectCount</c>. This intentionally destroys throughput, but
        /// turns asynchronous driver faults into a precise bucket breadcrumb.
        /// </summary>
        public static readonly bool FinishAfterMultiDrawIndirectCount = ReadFlag(XREngineEnvironmentVariables.MdicGlFinish);

        // -----------------------------------------------------------------
        // Per-frame counters (incremented from render thread).
        // -----------------------------------------------------------------

        private static long _slotsIterated;
        private static long _slotsSkippedPassMismatch;
        private static long _slotsSkippedNoMaterial;
        private static long _slotsSkippedTextShader;
        private static long _slotsSkippedProgram;
        private static long _tiersIterated;
        private static long _tiersSkippedConfigure;
        private static long _bucketsDispatched;
        private static long _bucketsDryRunSkipped;
        private static long _commandSwapsCleanSkipped;
        private static long _commandSwapsExecuted;
        private static long _materialScatterLookupCapacity;
        private static long _materialScatterDenseSlots;
        private static long _materialScatterBucketCount;
        private static long _materialScatterMaxDrawsPerBucket;
        private static long _materialScatterIndirectCommandClears;
        private static long _materialScatterIndirectCommandClearSkips;

        private static readonly Stopwatch _flushTimer = Stopwatch.StartNew();
        private static long _lastFlushMs;
        private const long FlushIntervalMs = 1000;

        public static void IncSlotIterated() => Interlocked.Increment(ref _slotsIterated);
        public static void IncSlotSkippedPassMismatch() => Interlocked.Increment(ref _slotsSkippedPassMismatch);
        public static void IncSlotSkippedNoMaterial() => Interlocked.Increment(ref _slotsSkippedNoMaterial);
        public static void IncSlotSkippedTextShader() => Interlocked.Increment(ref _slotsSkippedTextShader);
        public static void IncSlotSkippedProgram() => Interlocked.Increment(ref _slotsSkippedProgram);
        public static void IncTierIterated() => Interlocked.Increment(ref _tiersIterated);
        public static void IncTierSkippedConfigure() => Interlocked.Increment(ref _tiersSkippedConfigure);
        public static void IncBucketDispatched() => Interlocked.Increment(ref _bucketsDispatched);
        public static void IncBucketDryRunSkipped() => Interlocked.Increment(ref _bucketsDryRunSkipped);
        public static void IncCommandSwapCleanSkipped() => Interlocked.Increment(ref _commandSwapsCleanSkipped);
        public static void IncCommandSwapExecuted() => Interlocked.Increment(ref _commandSwapsExecuted);
        public static void RecordMaterialScatterSizing(uint lookupCapacity, uint denseSlots, uint bucketCount, uint maxDrawsPerBucket)
        {
            if (!LoggingEnabled)
                return;

            Interlocked.Exchange(ref _materialScatterLookupCapacity, lookupCapacity);
            Interlocked.Exchange(ref _materialScatterDenseSlots, denseSlots);
            Interlocked.Exchange(ref _materialScatterBucketCount, bucketCount);
            Interlocked.Exchange(ref _materialScatterMaxDrawsPerBucket, maxDrawsPerBucket);
        }

        public static void RecordMaterialScatterIndirectCommandClear(bool cleared)
        {
            if (!LoggingEnabled)
                return;

            if (cleared)
                Interlocked.Increment(ref _materialScatterIndirectCommandClears);
            else
                Interlocked.Increment(ref _materialScatterIndirectCommandClearSkips);
        }

        /// <summary>
        /// Flush counters at most once per second to <c>profiler-indirect-calls.log</c>.
        /// Safe to call from the bucket loop tail; intentionally non-blocking.
        /// </summary>
        public static void MaybeFlush()
        {
            if (!LoggingEnabled)
                return;

            long nowMs = _flushTimer.ElapsedMilliseconds;
            long last = Interlocked.Read(ref _lastFlushMs);
            if (nowMs - last < FlushIntervalMs)
                return;

            // Single-flusher: CAS the lastFlush timestamp so only one thread emits a row.
            if (Interlocked.CompareExchange(ref _lastFlushMs, nowMs, last) != last)
                return;

            long slots = Interlocked.Exchange(ref _slotsIterated, 0);
            long slotsPass = Interlocked.Exchange(ref _slotsSkippedPassMismatch, 0);
            long slotsNoMat = Interlocked.Exchange(ref _slotsSkippedNoMaterial, 0);
            long slotsText = Interlocked.Exchange(ref _slotsSkippedTextShader, 0);
            long slotsProg = Interlocked.Exchange(ref _slotsSkippedProgram, 0);
            long tiers = Interlocked.Exchange(ref _tiersIterated, 0);
            long tiersCfg = Interlocked.Exchange(ref _tiersSkippedConfigure, 0);
            long buckets = Interlocked.Exchange(ref _bucketsDispatched, 0);
            long bucketsDry = Interlocked.Exchange(ref _bucketsDryRunSkipped, 0);
            long swapsClean = Interlocked.Exchange(ref _commandSwapsCleanSkipped, 0);
            long swapsExec = Interlocked.Exchange(ref _commandSwapsExecuted, 0);
            long scatterLookup = Interlocked.Read(ref _materialScatterLookupCapacity);
            long scatterSlots = Interlocked.Read(ref _materialScatterDenseSlots);
            long scatterBuckets = Interlocked.Read(ref _materialScatterBucketCount);
            long scatterMaxDraws = Interlocked.Read(ref _materialScatterMaxDrawsPerBucket);
            long scatterCmdClears = Interlocked.Exchange(ref _materialScatterIndirectCommandClears, 0);
            long scatterCmdClearSkips = Interlocked.Exchange(ref _materialScatterIndirectCommandClearSkips, 0);

            var sb = new StringBuilder(256);
            sb.Append('[').Append(DateTime.UtcNow.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)).Append("] ");
            sb.Append("slots=").Append(slots);
            sb.Append(" slotsSkip(pass=").Append(slotsPass);
            sb.Append(",noMat=").Append(slotsNoMat);
            sb.Append(",text=").Append(slotsText);
            sb.Append(",prog=").Append(slotsProg).Append(')');
            sb.Append(" tiers=").Append(tiers);
            sb.Append(" tiersSkipCfg=").Append(tiersCfg);
            sb.Append(" buckets=").Append(buckets);
            sb.Append(" bucketsDryRunSkipped=").Append(bucketsDry);
            sb.Append(" cmdSwap(exec=").Append(swapsExec).Append(",cleanSkip=").Append(swapsClean).Append(')');
            sb.Append(" materialScatter(lookup=").Append(scatterLookup);
            sb.Append(",slots=").Append(scatterSlots);
            sb.Append(",buckets=").Append(scatterBuckets);
            sb.Append(",maxDrawsPerBucket=").Append(scatterMaxDraws);
            sb.Append(",cmdClears=").Append(scatterCmdClears);
            sb.Append(",cmdClearSkips=").Append(scatterCmdClearSkips).Append(')');
            sb.Append(" flags(dryRun=").Append(BucketLoopDryRun ? '1' : '0');
            sb.Append(",skipEmpty=").Append(BucketLoopSkipEmpty ? '1' : '0');
            sb.Append(",forceSingle=").Append(ForceSingleBucket ? '1' : '0');
            sb.Append(",finishAfterMdic=").Append(FinishAfterMultiDrawIndirectCount ? '1' : '0');
            sb.Append(",skipCleanSwap=").Append(SkipCommandSwapIfClean ? '1' : '0').Append(')');

            try
            {
                XREngine.Debug.WriteAuxiliaryLog("profiler-indirect-calls.log", sb.ToString());
            }
            catch
            {
                // Diagnostics must never fault the render thread.
            }
        }

        private static bool ReadFlag(string name)
        {
            string? value = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrEmpty(value))
                return false;
            return value == "1" ||
                   string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);
        }
    }
}
