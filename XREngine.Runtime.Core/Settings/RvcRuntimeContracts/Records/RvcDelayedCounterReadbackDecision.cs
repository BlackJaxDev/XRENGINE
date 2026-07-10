namespace XREngine;

public readonly record struct RvcDelayedCounterReadbackDecision(
    ERvcCounterReadbackDecision Decision,
    bool AllowReadback,
    ulong EarliestReadableFrameId,
    ERvcFallbackReason FallbackReason,
    string Diagnostic);
