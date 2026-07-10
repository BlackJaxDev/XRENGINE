namespace XREngine;

public readonly record struct RvcTemporalCacheEntry(
    RvcTemporalHashGridKey Key,
    RvcShadeletKey ShadeletKey,
    uint MaterialResourceGeneration,
    uint DeformationVersion,
    byte LodBucket,
    byte Confidence,
    ushort AgeFrames,
    ERvcTemporalInvalidationReason InvalidationReason)
{
    public bool IsValid => InvalidationReason == ERvcTemporalInvalidationReason.None && Confidence > 0;
}
