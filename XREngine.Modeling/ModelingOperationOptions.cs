using System.Numerics;

namespace XREngine.Modeling;

public enum ModelingAttributeInterpolationPolicy
{
    Linear = 0,
    Nearest
}

public sealed class ModelingOperationOptions
{
    public ModelingAttributeInterpolationPolicy AttributeInterpolationPolicy { get; init; }
        = ModelingAttributeInterpolationPolicy.Linear;

    public static readonly ModelingOperationOptions Default = new();
}

public static class ModelingAttributeInterpolation
{
    public static Vector3 InterpolatePosition(
        Vector3 first,
        Vector3 second,
        float t,
        ModelingAttributeInterpolationPolicy policy)
    {
        t = Math.Clamp(t, 0f, 1f);
        return policy switch
        {
            ModelingAttributeInterpolationPolicy.Linear => Vector3.Lerp(first, second, t),
            ModelingAttributeInterpolationPolicy.Nearest => t < 0.5f ? first : second,
            _ => Vector3.Lerp(first, second, t)
        };
    }
}
