namespace XREngine;

public readonly record struct RvcLightReference(
    uint LightId,
    uint ResourceIndex,
    float EstimatedContribution,
    bool CastsShadow,
    bool UsesCookie);
