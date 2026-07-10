namespace XREngine;

public readonly record struct RvcCounterReadbackContract(
    ERvcCounterReadbackMode Mode,
    int DelayFrames,
    bool DoubleBuffered,
    bool SynchronousReadbackForbidden)
{
    public static RvcCounterReadbackContract Default => new(
        ERvcCounterReadbackMode.DelayedGpuReadback,
        DelayFrames: 2,
        DoubleBuffered: true,
        SynchronousReadbackForbidden: true);

    public RvcDelayedCounterReadbackDecision Evaluate(
        ulong currentFrameId,
        ulong producedFrameId,
        bool synchronousReadbackRequested)
    {
        if (Mode == ERvcCounterReadbackMode.Disabled)
            return new(
                ERvcCounterReadbackDecision.Disabled,
                AllowReadback: false,
                EarliestReadableFrameId: producedFrameId,
                ERvcFallbackReason.None,
                "RVC counter readback is disabled.");

        if (synchronousReadbackRequested && SynchronousReadbackForbidden)
            return new(
                ERvcCounterReadbackDecision.SynchronousForbidden,
                AllowReadback: false,
                EarliestReadableFrameId: producedFrameId + (ulong)Math.Max(0, DelayFrames),
                ERvcFallbackReason.SynchronousCounterReadbackForbidden,
                "RVC forbids synchronous GPU counter readback on the render path.");

        ulong earliest = producedFrameId + (ulong)Math.Max(0, DelayFrames);
        bool ready = currentFrameId >= earliest;
        return new(
            ready ? ERvcCounterReadbackDecision.Ready : ERvcCounterReadbackDecision.Pending,
            ready,
            earliest,
            ERvcFallbackReason.None,
            ready
                ? "RVC delayed counter readback is ready."
                : "RVC delayed counter readback is still pending.");
    }
}
