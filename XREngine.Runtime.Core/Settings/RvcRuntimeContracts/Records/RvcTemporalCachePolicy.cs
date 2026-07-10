namespace XREngine;

public readonly record struct RvcTemporalCachePolicy(
    bool PersistentStaticDiffuseEntries,
    bool WorldSpaceHashGrid,
    ushort MaxAgeFrames,
    byte MinConfidenceForReuse)
{
    public static RvcTemporalCachePolicy Default => new(
        PersistentStaticDiffuseEntries: true,
        WorldSpaceHashGrid: true,
        MaxAgeFrames: 60,
        MinConfidenceForReuse: 128);
}
