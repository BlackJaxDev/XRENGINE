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

        public static long BinaryCacheHits => Interlocked.Read(ref s_binaryCacheHits);
        public static long BinaryCacheMisses => Interlocked.Read(ref s_binaryCacheMisses);
        public static long SourceBuilds => Interlocked.Read(ref s_sourceBuilds);
        public static long SourceFailures => Interlocked.Read(ref s_sourceFailures);
        public static long FailedHashSkips => Interlocked.Read(ref s_failedHashSkips);
        public static long SlowLinkPreparations => Interlocked.Read(ref s_slowLinkPreparations);
        public static long SharedContextSourceQueued => Interlocked.Read(ref s_sharedContextSourceQueued);

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
                $"asyncUploadCompleted={uploadCompleted} asyncUploadFailed={uploadFailed} " +
                $"asyncUploadBackpressure={uploadBackpressure} asyncUploadCoalesced={uploadCoalesced}.");
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
        }
    }
}
