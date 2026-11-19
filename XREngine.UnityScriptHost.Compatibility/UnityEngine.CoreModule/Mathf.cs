namespace UnityEngine;

/// <summary>Provides scalar operations that do not depend on Unity object state.</summary>
public static class Mathf
{
    public const float PI = MathF.PI;
    public static float Min(float a, float b) => MathF.Min(a, b);
    public static float Max(float a, float b) => MathF.Max(a, b);
    public static float Abs(float value) => MathF.Abs(value);
    public static float Clamp(float value, float min, float max) => MathF.Max(min, MathF.Min(max, value));
    public static float Clamp01(float value) => Clamp(value, 0f, 1f);
}
