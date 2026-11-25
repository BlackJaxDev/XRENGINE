using System.Numerics;

namespace XREngine.Editor;

/// <summary>Replaces one numeric key channel while retaining the other authored channels.</summary>
internal static class AnimationKeyChannels
{
    public static int Count(Type type) => type == typeof(float) ? 1 :
        type == typeof(Vector2) ? 2 : type == typeof(Vector3) ? 3 :
        type == typeof(Vector4) || type == typeof(Quaternion) ? 4 : 0;

    public static bool TryRead(object value, int channel, out float result)
    {
        result = 0;
        if ((uint)channel >= (uint)Count(value.GetType()))
            return false;
        result = value switch
        {
            float number => number,
            Vector2 vector => channel == 0 ? vector.X : vector.Y,
            Vector3 vector => channel switch { 0 => vector.X, 1 => vector.Y, _ => vector.Z },
            Vector4 vector => channel switch { 0 => vector.X, 1 => vector.Y, 2 => vector.Z, _ => vector.W },
            Quaternion rotation => channel switch { 0 => rotation.X, 1 => rotation.Y, 2 => rotation.Z, _ => rotation.W },
            _ => 0
        };
        return float.IsFinite(result);
    }

    public static bool TryReplace(object value, int channel, float next, out object? result)
    {
        result = null;
        if (!float.IsFinite(next) || !TryRead(value, channel, out _))
            return false;
        result = value switch
        {
            float => next,
            Vector2 vector => channel == 0 ? vector with { X = next } : vector with { Y = next },
            Vector3 vector => channel switch
            {
                0 => vector with { X = next }, 1 => vector with { Y = next },
                _ => vector with { Z = next }
            },
            Vector4 vector => channel switch
            {
                0 => vector with { X = next }, 1 => vector with { Y = next },
                2 => vector with { Z = next }, _ => vector with { W = next }
            },
            Quaternion rotation => channel switch
            {
                0 => rotation with { X = next }, 1 => rotation with { Y = next },
                2 => rotation with { Z = next }, _ => rotation with { W = next }
            },
            _ => null
        };
        return result is not null;
    }
}
