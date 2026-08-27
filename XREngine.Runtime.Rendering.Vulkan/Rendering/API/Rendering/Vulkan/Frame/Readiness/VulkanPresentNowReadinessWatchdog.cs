using System.Diagnostics;
using System.Globalization;

namespace XREngine.Rendering.Vulkan;

/// <summary>Allocation-free liveness guard for one foreground readiness barrier.</summary>
internal struct VulkanPresentNowReadinessWatchdog
{
    private const double DefaultTimeoutMilliseconds = 30_000.0;
    private const double MaximumTimeoutMilliseconds = 300_000.0;
    private static readonly long TimeoutTicks = ResolveTimeoutTicks();

    private readonly ulong _frameId;
    private readonly long _startTimestamp;
    private long _lastProgressTimestamp;

    internal VulkanPresentNowReadinessWatchdog(ulong frameId)
    {
        _frameId = frameId;
        _startTimestamp = Stopwatch.GetTimestamp();
        _lastProgressTimestamp = _startTimestamp;
    }

    internal readonly long DeadlineTimestamp
        => checked(_lastProgressTimestamp + TimeoutTicks);

    internal static long StallTimeoutTicks => TimeoutTicks;

    internal void RecordProgress()
        => _lastProgressTimestamp = Stopwatch.GetTimestamp();

    internal readonly bool IsExpired
        => Stopwatch.GetTimestamp() >= DeadlineTimestamp;

    internal readonly VulkanPresentNowReadinessException CreateFailure(
        EVulkanPresentNowReadinessStage stage,
        string activeTicket,
        string dependencyChain,
        string detail,
        Exception? innerException = null)
    {
        long now = Stopwatch.GetTimestamp();
        return new VulkanPresentNowReadinessException(
            _frameId,
            stage,
            activeTicket,
            dependencyChain,
            Stopwatch.GetElapsedTime(_startTimestamp, now),
            Stopwatch.GetElapsedTime(_lastProgressTimestamp, now),
            detail,
            innerException);
    }

    private static long ResolveTimeoutTicks()
    {
        string? configured = Environment.GetEnvironmentVariable(
            XREngineEnvironmentVariables.VulkanPresentNowReadinessWatchdogMs);
        double milliseconds = double.TryParse(
                configured,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double parsed)
            ? Math.Clamp(parsed, 1.0, MaximumTimeoutMilliseconds)
            : DefaultTimeoutMilliseconds;
        return Math.Max(
            1L,
            checked((long)Math.Ceiling(
                milliseconds * Stopwatch.Frequency / 1000.0)));
    }
}
