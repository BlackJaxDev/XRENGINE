using System.Diagnostics;

namespace XREngine
{
    /// <summary>
    /// Decides when queued app-thread work may start while a render frame dispatches. Work that
    /// render frames have deferred for the deferral limit starts anyway.
    /// </summary>
    /// <remarks>
    /// The thread that drains app-thread work is the only caller of
    /// <see cref="ShouldStartDuringRenderFrame"/> and <see cref="OnWorkStarted"/>. Any thread can
    /// call <see cref="Capture"/>.
    /// </remarks>
    internal sealed class AppThreadWorkDeferralPolicy(long maxDeferralTicks)
    {
        private bool _deferring;
        private long _deferredSince;
        private long _deferredChecks;
        private long _deferredStarts;
        private long _maximumDeferralTicks;

        /// <summary>The deferral limit, in <see cref="Stopwatch"/> ticks.</summary>
        public long MaxDeferralTicks { get; } = maxDeferralTicks;

        /// <summary>
        /// Returns true when queued work may start although a render frame dispatches, because
        /// render frames have deferred it for the deferral limit. An empty queue ends the deferral.
        /// </summary>
        public bool ShouldStartDuringRenderFrame(bool hasQueuedWork, long nowTicks)
        {
            if (!hasQueuedWork)
            {
                _deferring = false;
                return false;
            }

            if (!_deferring)
            {
                _deferring = true;
                _deferredSince = nowTicks;
            }

            long deferredTicks = nowTicks - _deferredSince;
            RecordMaximum(ref _maximumDeferralTicks, deferredTicks);
            if (deferredTicks < MaxDeferralTicks)
            {
                Interlocked.Increment(ref _deferredChecks);
                return false;
            }

            Interlocked.Increment(ref _deferredStarts);
            return true;
        }

        /// <summary>Ends the current deferral, because queued work started.</summary>
        public void OnWorkStarted() => _deferring = false;

        /// <summary>Captures the cumulative counters.</summary>
        public AppThreadDispatchDiagnostics Capture()
            => new(
                Interlocked.Read(ref _deferredChecks),
                Interlocked.Read(ref _deferredStarts),
                Interlocked.Read(ref _maximumDeferralTicks) * 1000.0 / Stopwatch.Frequency);

        private static void RecordMaximum(ref long target, long value)
        {
            long current = Interlocked.Read(ref target);
            while (value > current)
            {
                long observed = Interlocked.CompareExchange(ref target, value, current);
                if (observed == current)
                    return;
                current = observed;
            }
        }
    }
}
