namespace XREngine.RenderBench;

/// <summary>Wall-clock process intervals, including delayed query drainage.</summary>
public sealed record RenderBenchPhaseIntervals(
    DateTimeOffset ProcessStartUtc,
    DateTimeOffset WarmupStartUtc,
    DateTimeOffset WarmupEndUtc,
    DateTimeOffset StabilityStartUtc,
    DateTimeOffset StabilityEndUtc,
    DateTimeOffset CaptureStartUtc,
    DateTimeOffset CaptureEndUtc,
    DateTimeOffset DrainStartUtc,
    DateTimeOffset DrainEndUtc,
    DateTimeOffset ProcessEndUtc);
