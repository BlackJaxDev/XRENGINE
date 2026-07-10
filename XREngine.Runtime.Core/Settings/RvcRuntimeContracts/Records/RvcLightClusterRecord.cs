namespace XREngine;

public readonly record struct RvcLightClusterRecord(
    RvcHeadSpaceClusterKey Key,
    uint FirstLightIndex,
    uint LightCount,
    RvcLightReservoir Reservoir);
