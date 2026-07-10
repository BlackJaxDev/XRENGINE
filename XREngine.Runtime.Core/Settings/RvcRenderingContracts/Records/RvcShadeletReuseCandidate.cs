using System.Numerics;

namespace XREngine;

public readonly record struct RvcShadeletReuseCandidate(
    RvcShadeletKey Key,
    uint MaterialResourceGeneration,
    Vector3 Normal,
    float DepthMeters,
    byte RoughnessBucket,
    uint DeformationVersion,
    byte LodBucket,
    bool Disoccluded,
    bool ViewDependentMaterial);
