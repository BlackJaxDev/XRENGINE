namespace XREngine.Networking;

/// <summary>
/// The fixed fields of a clock-synchronization exchange. Unlike <see cref="ClockSyncMessage"/> it
/// carries no string, so the high-rate path can pass it by value without allocating.
/// </summary>
public readonly record struct ClockSyncSample(
    Guid SessionId,
    int ServerPlayerIndex,
    double ClientSendTimestampUtc,
    double ServerReceiveTimestampUtc,
    double ServerSendTimestampUtc,
    long ServerTickId);
