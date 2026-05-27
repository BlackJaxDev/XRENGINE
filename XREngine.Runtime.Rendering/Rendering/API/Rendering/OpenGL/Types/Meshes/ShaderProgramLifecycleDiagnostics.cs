using System.Threading;

namespace XREngine.Rendering.OpenGL
{
    /// <summary>
    /// Phase 8: process-wide counters for OpenGL shader-program lifecycle events.
    /// <para/>
    /// Counters are aggregated across all <see cref="GLRenderProgram"/> instances and
    /// emitted as a single <c>[ShaderProgramSummary]</c> log line via
    /// <see cref="LogSummary"/>, typically from <see cref="OpenGLRenderer.CleanUp"/>.
    /// <para/>
    /// The intent is to keep per-program <c>[ShaderCache] READY/MISS/QUEUE</c> verbose
    /// logs available for cold-start diagnosis while also providing one compact line at
    /// shutdown that lets us answer "how many cache hits/misses did this run produce"
    /// without grep-counting lines.
    /// </summary>
    public static class ShaderProgramLifecycleDiagnostics
    {
        private static long s_binaryCacheHits;
        private static long s_binaryCacheMisses;
        private static long s_sourceBuilds;
        private static long s_sourceFailures;
        private static long s_failedHashSkips;
        private static long s_slowLinkPreparations;
        private static long s_sharedContextSourceQueued;
        private static long s_sharedProgramCreates;
        private static long s_sharedProgramReuses;
        private static long s_sharedProgramDeletes;
        private static long s_logicalProgramCreates;
        private static long s_logicalProgramDestroys;
        private static long s_sharedProgramAttachEvents;
        private static long s_sharedProgramDetachEvents;
        private static long s_peakSharedProgramReferences;
        private static long s_combinedProgramPoolHits;
        private static long s_combinedProgramPoolMisses;
        private static long s_combinedProgramPoolEvictions;
        private static long s_combinedProgramPoolActiveReferences;
        private static long s_combinedProgramPoolPeakReferences;
        private static long s_gpuDrivenProgramPoolHits;
        private static long s_gpuDrivenProgramPoolMisses;

        public static long BinaryCacheHits => Interlocked.Read(ref s_binaryCacheHits);
        public static long BinaryCacheMisses => Interlocked.Read(ref s_binaryCacheMisses);
        public static long SourceBuilds => Interlocked.Read(ref s_sourceBuilds);
        public static long SourceFailures => Interlocked.Read(ref s_sourceFailures);
        public static long FailedHashSkips => Interlocked.Read(ref s_failedHashSkips);
        public static long SlowLinkPreparations => Interlocked.Read(ref s_slowLinkPreparations);
        public static long SharedContextSourceQueued => Interlocked.Read(ref s_sharedContextSourceQueued);
        public static long SharedProgramCreates => Interlocked.Read(ref s_sharedProgramCreates);
        public static long SharedProgramReuses => Interlocked.Read(ref s_sharedProgramReuses);
        public static long SharedProgramDeletes => Interlocked.Read(ref s_sharedProgramDeletes);
        public static long LogicalProgramCreates => Interlocked.Read(ref s_logicalProgramCreates);
        public static long LogicalProgramDestroys => Interlocked.Read(ref s_logicalProgramDestroys);
        public static long SharedProgramAttachEvents => Interlocked.Read(ref s_sharedProgramAttachEvents);
        public static long SharedProgramDetachEvents => Interlocked.Read(ref s_sharedProgramDetachEvents);
        public static long PeakSharedProgramReferences => Interlocked.Read(ref s_peakSharedProgramReferences);
        public static long CombinedProgramPoolHits => Interlocked.Read(ref s_combinedProgramPoolHits);
        public static long CombinedProgramPoolMisses => Interlocked.Read(ref s_combinedProgramPoolMisses);
        public static long CombinedProgramPoolEvictions => Interlocked.Read(ref s_combinedProgramPoolEvictions);
        public static long CombinedProgramPoolActiveReferences => Interlocked.Read(ref s_combinedProgramPoolActiveReferences);
        public static long CombinedProgramPoolPeakReferences => Interlocked.Read(ref s_combinedProgramPoolPeakReferences);
        public static long GpuDrivenProgramPoolHits => Interlocked.Read(ref s_gpuDrivenProgramPoolHits);
        public static long GpuDrivenProgramPoolMisses => Interlocked.Read(ref s_gpuDrivenProgramPoolMisses);

        public static void RecordBinaryCacheHit()
            => Interlocked.Increment(ref s_binaryCacheHits);

        public static void RecordBinaryCacheMiss()
            => Interlocked.Increment(ref s_binaryCacheMisses);

        public static void RecordSourceBuild()
            => Interlocked.Increment(ref s_sourceBuilds);

        public static void RecordSourceFailure()
            => Interlocked.Increment(ref s_sourceFailures);

        public static void RecordFailedHashSkip()
            => Interlocked.Increment(ref s_failedHashSkips);

        public static void RecordSlowLinkPreparation()
            => Interlocked.Increment(ref s_slowLinkPreparations);

        public static void RecordSharedContextSourceQueued()
            => Interlocked.Increment(ref s_sharedContextSourceQueued);

        public static void RecordSharedProgramCreate()
            => Interlocked.Increment(ref s_sharedProgramCreates);

        public static void RecordSharedProgramReuse()
            => Interlocked.Increment(ref s_sharedProgramReuses);

        public static void RecordSharedProgramDelete()
            => Interlocked.Increment(ref s_sharedProgramDeletes);

        public static void RecordLogicalProgramCreate()
            => Interlocked.Increment(ref s_logicalProgramCreates);

        public static void RecordLogicalProgramDestroy()
            => Interlocked.Increment(ref s_logicalProgramDestroys);

        public static void RecordSharedProgramAttach(int referenceCount)
        {
            Interlocked.Increment(ref s_sharedProgramAttachEvents);
            UpdatePeak(ref s_peakSharedProgramReferences, referenceCount);
        }

        public static void RecordSharedProgramDetach()
            => Interlocked.Increment(ref s_sharedProgramDetachEvents);

        public static void RecordCombinedProgramPoolHit()
            => Interlocked.Increment(ref s_combinedProgramPoolHits);

        public static void RecordCombinedProgramPoolMiss()
            => Interlocked.Increment(ref s_combinedProgramPoolMisses);

        public static void RecordCombinedProgramPoolEviction()
            => Interlocked.Increment(ref s_combinedProgramPoolEvictions);

        public static void RecordCombinedProgramPoolAcquire(int referenceCount)
        {
            Interlocked.Increment(ref s_combinedProgramPoolActiveReferences);
            UpdatePeak(ref s_combinedProgramPoolPeakReferences, referenceCount);
        }

        public static void RecordCombinedProgramPoolRelease()
            => Interlocked.Decrement(ref s_combinedProgramPoolActiveReferences);

        public static void RecordGpuDrivenProgramPoolHit()
            => Interlocked.Increment(ref s_gpuDrivenProgramPoolHits);

        public static void RecordGpuDrivenProgramPoolMiss()
            => Interlocked.Increment(ref s_gpuDrivenProgramPoolMisses);

        private static void UpdatePeak(ref long target, long value)
        {
            long current;
            do
            {
                current = Interlocked.Read(ref target);
                if (value <= current)
                    return;
            }
            while (Interlocked.CompareExchange(ref target, value, current) != current);
        }

        /// <summary>
        /// Emits one <c>[ShaderProgramSummary]</c> log line with all lifecycle counters
        /// plus optional <see cref="OpenGLRenderer.GLProgramBinaryUploadQueue"/> totals
        /// when supplied. Safe to call any time; intended for shutdown.
        /// </summary>
        public static void LogSummary(OpenGLRenderer.GLProgramBinaryUploadQueue? binaryUploadQueue = null)
        {
            long binaryHits = BinaryCacheHits;
            long binaryMisses = BinaryCacheMisses;
            long total = binaryHits + binaryMisses;
            double hitRatio = total > 0 ? (double)binaryHits / total : 0.0;

            long uploadCompleted = binaryUploadQueue?.CompletedCount ?? 0L;
            long uploadFailed = binaryUploadQueue?.FailedCount ?? 0L;
            long uploadBackpressure = binaryUploadQueue?.BackpressureCount ?? 0L;
            long uploadCoalesced = binaryUploadQueue?.CoalescedCount ?? 0L;

            Debug.OpenGL(
                $"[ShaderProgramSummary] binaryHits={binaryHits} binaryMisses={binaryMisses} " +
                $"binaryHitRatio={hitRatio:F3} sourceBuilds={SourceBuilds} sourceFailures={SourceFailures} " +
                $"failedHashSkips={FailedHashSkips} slowLinkPreps={SlowLinkPreparations} " +
                $"sharedContextSourceQueued={SharedContextSourceQueued} " +
                $"sharedProgramCreates={SharedProgramCreates} sharedProgramReuses={SharedProgramReuses} " +
                $"sharedProgramDeletes={SharedProgramDeletes} " +
                $"asyncUploadCompleted={uploadCompleted} asyncUploadFailed={uploadFailed} " +
                $"asyncUploadBackpressure={uploadBackpressure} asyncUploadCoalesced={uploadCoalesced}.");

            Debug.OpenGL(
                $"[ShaderProgramDedupSummary] logicalCreates={LogicalProgramCreates} logicalDestroys={LogicalProgramDestroys} " +
                $"sharedAttach={SharedProgramAttachEvents} sharedDetach={SharedProgramDetachEvents} " +
                $"sharedPeakRefs={PeakSharedProgramReferences} combinedPoolHits={CombinedProgramPoolHits} " +
                $"combinedPoolMisses={CombinedProgramPoolMisses} combinedPoolEvictions={CombinedProgramPoolEvictions} " +
                $"combinedPoolActiveRefs={CombinedProgramPoolActiveReferences} combinedPoolPeakRefs={CombinedProgramPoolPeakReferences} " +
                $"gpuPoolHits={GpuDrivenProgramPoolHits} gpuPoolMisses={GpuDrivenProgramPoolMisses}.");
        }

        /// <summary>Test-only reset. Not normally required during runtime.</summary>
        internal static void ResetForTests()
        {
            Interlocked.Exchange(ref s_binaryCacheHits, 0);
            Interlocked.Exchange(ref s_binaryCacheMisses, 0);
            Interlocked.Exchange(ref s_sourceBuilds, 0);
            Interlocked.Exchange(ref s_sourceFailures, 0);
            Interlocked.Exchange(ref s_failedHashSkips, 0);
            Interlocked.Exchange(ref s_slowLinkPreparations, 0);
            Interlocked.Exchange(ref s_sharedContextSourceQueued, 0);
            Interlocked.Exchange(ref s_sharedProgramCreates, 0);
            Interlocked.Exchange(ref s_sharedProgramReuses, 0);
            Interlocked.Exchange(ref s_sharedProgramDeletes, 0);
            Interlocked.Exchange(ref s_logicalProgramCreates, 0);
            Interlocked.Exchange(ref s_logicalProgramDestroys, 0);
            Interlocked.Exchange(ref s_sharedProgramAttachEvents, 0);
            Interlocked.Exchange(ref s_sharedProgramDetachEvents, 0);
            Interlocked.Exchange(ref s_peakSharedProgramReferences, 0);
            Interlocked.Exchange(ref s_combinedProgramPoolHits, 0);
            Interlocked.Exchange(ref s_combinedProgramPoolMisses, 0);
            Interlocked.Exchange(ref s_combinedProgramPoolEvictions, 0);
            Interlocked.Exchange(ref s_combinedProgramPoolActiveReferences, 0);
            Interlocked.Exchange(ref s_combinedProgramPoolPeakReferences, 0);
            Interlocked.Exchange(ref s_gpuDrivenProgramPoolHits, 0);
            Interlocked.Exchange(ref s_gpuDrivenProgramPoolMisses, 0);
        }
    }
}
