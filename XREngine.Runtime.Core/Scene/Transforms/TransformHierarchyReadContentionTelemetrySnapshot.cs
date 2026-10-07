namespace XREngine.Scene.Transforms;

/// <summary>Contains cumulative sequence-read contention for each matrix space.</summary>
public readonly record struct TransformHierarchyReadContentionTelemetrySnapshot(
    bool Enabled,
    long StopwatchFrequency,
    long LocalContendedReads,
    long LocalOddSequenceRetries,
    long LocalChangedSequenceRetries,
    long LocalRetryTicks,
    int LocalMaximumSpinCount,
    long WorldContendedReads,
    long WorldOddSequenceRetries,
    long WorldChangedSequenceRetries,
    long WorldRetryTicks,
    int WorldMaximumSpinCount,
    long RenderContendedReads,
    long RenderOddSequenceRetries,
    long RenderChangedSequenceRetries,
    long RenderRetryTicks,
    int RenderMaximumSpinCount);
