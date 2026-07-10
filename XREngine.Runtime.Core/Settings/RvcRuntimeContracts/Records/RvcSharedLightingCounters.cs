namespace XREngine;

public readonly record struct RvcSharedLightingCounters(
    ulong ClusterCount,
    ulong ExactLightReferences,
    ulong RejectedLightReferences,
    ulong AggregateLightCount,
    ulong ReservoirCandidateCount,
    float EstimatedEnergyError);
