using System.Numerics;

namespace XREngine;

public readonly record struct RvcAggregateLight(
    Vector3 DirectionOrPosition,
    Vector3 Radiance,
    float WeightSum,
    uint SourceLightCount)
{
    public static RvcAggregateLight Empty => default;

    public RvcAggregateLight Add(Vector3 directionOrPosition, Vector3 radiance, float weight)
    {
        float clampedWeight = MathF.Max(0.0f, weight);
        float nextWeight = WeightSum + clampedWeight;
        Vector3 nextDirection = nextWeight > 0.0f
            ? ((DirectionOrPosition * WeightSum) + (directionOrPosition * clampedWeight)) / nextWeight
            : Vector3.Zero;
        Vector3 nextRadiance = Radiance + radiance * clampedWeight;
        return new(nextDirection, nextRadiance, nextWeight, SourceLightCount + 1u);
    }
}
