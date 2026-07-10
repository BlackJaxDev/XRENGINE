namespace XREngine;

public readonly record struct RvcReuseCounters(
    ulong IntraViewReuseAttempts,
    ulong InsetWideReuseAttempts,
    ulong StereoReuseAttempts,
    ulong TemporalReuseAttempts,
    ulong AcceptedReuse,
    ulong RejectedReuse,
    ulong DisocclusionLocalShading);
