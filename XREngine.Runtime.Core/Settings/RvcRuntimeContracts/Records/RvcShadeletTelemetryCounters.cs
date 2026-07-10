namespace XREngine;

public readonly record struct RvcShadeletTelemetryCounters(
    ulong UniqueShadelets,
    ulong IntraViewKeyMatches,
    ulong InsetWideKeyMatches,
    ulong StereoKeyMatches,
    ulong MaterialBinCount,
    ulong CacheMisses,
    ulong ShadeletMapOverflows,
    ulong DeduplicationOverflows);
