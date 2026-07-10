namespace XREngine;

public readonly record struct RvcEdgeRejectionPolicy(
    ERvcEdgeRejectionFlags Flags,
    float MaxDepthDeltaMeters,
    float MaxNormalAngleDegrees)
{
    public static RvcEdgeRejectionPolicy Conservative => new(
        ERvcEdgeRejectionFlags.Depth |
        ERvcEdgeRejectionFlags.Normal |
        ERvcEdgeRejectionFlags.Material |
        ERvcEdgeRejectionFlags.Primitive |
        ERvcEdgeRejectionFlags.Disocclusion,
        MaxDepthDeltaMeters: 0.02f,
        MaxNormalAngleDegrees: 8.0f);
}
