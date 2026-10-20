namespace XREngine
{
    /// <summary>
    /// Cumulative counters of queued app-thread work that render frame dispatch deferred.
    /// Read two captures and use the difference.
    /// </summary>
    /// <param name="DeferredChecks">Update iterations that deferred queued app-thread work because a render frame dispatched.</param>
    /// <param name="DeferredStarts">Starts of app-thread work during a render frame, after the work reached the deferral limit.</param>
    /// <param name="MaximumDeferralMilliseconds">Longest time that queued app-thread work stayed deferred by render frames.</param>
    public readonly record struct AppThreadDispatchDiagnostics(
        long DeferredChecks,
        long DeferredStarts,
        double MaximumDeferralMilliseconds);
}
