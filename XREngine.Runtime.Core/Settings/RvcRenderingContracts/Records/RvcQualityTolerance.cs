namespace XREngine;

public readonly record struct RvcQualityTolerance(
    ERvcFoveationRegion Region,
    float MaxPerPixelError,
    float MinSsim,
    float MaxFlipError);
