namespace XREngine.Networking;

/// <summary>
/// Counts of received state-change frames rejected before or during decoding, plus high-rate lane
/// drops. Every rejection is counted; none escapes the receive loop as an exception.
/// </summary>
public readonly record struct StateChangeRejectionSnapshot(
    long Truncated,
    long Oversized,
    long UnknownType,
    long ProtocolMismatch,
    long LengthMismatch,
    long DecodeFailed,
    long HighRateQueueDrops,
    long HighRateOversizeFallbacks,
    long PoseSlabExhausted);
