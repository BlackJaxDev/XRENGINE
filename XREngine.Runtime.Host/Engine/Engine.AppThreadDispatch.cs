using System.Diagnostics;

namespace XREngine
{
    /// <summary>
    /// Limits how long render frames can defer queued app-thread work.
    /// </summary>
    /// <remarks>
    /// The update thread drains app-thread jobs once in each update iteration, and it does not
    /// start them while a render frame dispatches. When the update and render loops have almost
    /// the same period, that check can fall inside a render frame for many iterations in a row,
    /// and queued work, such as MCP tool calls in main-thread dispatch mode, can wait for minutes.
    /// After queued work has stayed deferred for <see cref="AppThreadWorkMaxDeferralMilliseconds"/>,
    /// the update thread starts it although a render frame dispatches.
    /// <para>
    /// The update thread never waits for the render thread here: a render frame can wait for an
    /// update-thread task, so such a wait stalls both threads. App-thread work runs on the update
    /// thread like other update work, and it already overlapped render frames when it ran longer
    /// than the gap between two frames. The render thread itself never runs app-thread work
    /// during its frame.
    /// </para>
    /// </remarks>
    public static partial class Engine
    {
        private const double AppThreadWorkMaxDeferralMilliseconds = 50.0;
        private static readonly AppThreadWorkDeferralPolicy _appThreadWorkDeferral =
            new((long)(AppThreadWorkMaxDeferralMilliseconds * Stopwatch.Frequency / 1000.0));

        /// <summary>Captures the cumulative counters of app-thread work that render frames deferred.</summary>
        public static AppThreadDispatchDiagnostics CaptureAppThreadDispatchDiagnostics()
            => _appThreadWorkDeferral.Capture();

        /// <summary>
        /// Returns true when queued app-thread work may start while a render frame dispatches,
        /// because render frames have deferred it for too long.
        /// </summary>
        /// <remarks>Call this only while <see cref="IsDispatchingRenderFrame"/> is true.</remarks>
        private static bool ShouldStartDeferredAppThreadWork()
            => !IsRenderThread &&
                _appThreadWorkDeferral.ShouldStartDuringRenderFrame(
                    Jobs.QueuedAppThreadJobCount != 0,
                    Stopwatch.GetTimestamp());
    }
}
