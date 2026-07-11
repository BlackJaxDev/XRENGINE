using Silk.NET.OpenXR;

namespace XREngine.Rendering.API.Rendering.OpenXR;

internal enum OpenXrProbeFailureDisposition
{
    Retry,
    HaltUntilReconfigured,
}

internal readonly record struct OpenXrProbeRetryDecision(
    OpenXrProbeFailureDisposition Disposition,
    TimeSpan Delay,
    string Category,
    bool RecreateInstance)
{
    public bool ShouldRetry => Disposition == OpenXrProbeFailureDisposition.Retry;
}

internal static class OpenXrProbeRetryPolicy
{
    internal static OpenXrProbeRetryDecision ForCreateInstanceResult(
        Result result,
        int consecutiveFailures,
        TimeSpan initialDelay,
        TimeSpan maximumDelay)
    {
        if (result is Result.ErrorRuntimeUnavailable
            or Result.ErrorRuntimeFailure
            or Result.ErrorInitializationFailed
            or Result.ErrorInstanceLost)
        {
            return new(
                OpenXrProbeFailureDisposition.Retry,
                CalculateBackoff(consecutiveFailures, initialDelay, maximumDelay),
                "recoverable-runtime",
                RecreateInstance: true);
        }

        return new(
            OpenXrProbeFailureDisposition.HaltUntilReconfigured,
            Timeout.InfiniteTimeSpan,
            "configuration-or-capability",
            RecreateInstance: false);
    }

    internal static OpenXrProbeRetryDecision ForGetSystemResult(
        Result result,
        int consecutiveFailures,
        TimeSpan initialDelay,
        TimeSpan maximumDelay)
    {
        if (result == Result.ErrorFormFactorUnavailable)
        {
            return new(
                OpenXrProbeFailureDisposition.Retry,
                CalculateBackoff(consecutiveFailures, initialDelay, maximumDelay),
                "headset-or-form-factor-unavailable",
                RecreateInstance: false);
        }

        return ForCreateInstanceResult(result, consecutiveFailures, initialDelay, maximumDelay);
    }

    internal static TimeSpan CalculateBackoff(
        int consecutiveFailures,
        TimeSpan initialDelay,
        TimeSpan maximumDelay)
    {
        if (initialDelay <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(initialDelay));
        if (maximumDelay < initialDelay)
            throw new ArgumentOutOfRangeException(nameof(maximumDelay));

        int exponent = Math.Clamp(consecutiveFailures - 1, 0, 20);
        double multiplier = Math.Pow(2.0, exponent);
        double delayMilliseconds = Math.Min(
            maximumDelay.TotalMilliseconds,
            initialDelay.TotalMilliseconds * multiplier);
        return TimeSpan.FromMilliseconds(delayMilliseconds);
    }
}
