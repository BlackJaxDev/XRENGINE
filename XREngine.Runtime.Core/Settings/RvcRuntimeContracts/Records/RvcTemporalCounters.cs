namespace XREngine;

public readonly record struct RvcTemporalCounters(
    ulong CacheHits,
    ulong CacheMisses,
    ulong InvalidatedEntries,
    ulong FovealStaleRejections,
    float TemporalHitRate);
