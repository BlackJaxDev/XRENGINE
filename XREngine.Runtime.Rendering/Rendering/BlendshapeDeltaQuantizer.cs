using System;
using System.Numerics;

namespace XREngine.Rendering;

public static class BlendshapeDeltaQuantizer
{
    public static void ComputeBounds(ReadOnlySpan<Vector3> values, out Vector3 min, out Vector3 max, out Vector3 scale, out Vector3 bias)
    {
        if (values.IsEmpty)
        {
            min = Vector3.Zero;
            max = Vector3.Zero;
            scale = Vector3.One;
            bias = Vector3.Zero;
            return;
        }

        min = new Vector3(float.PositiveInfinity);
        max = new Vector3(float.NegativeInfinity);
        for (int i = 0; i < values.Length; i++)
        {
            min = Vector3.Min(min, values[i]);
            max = Vector3.Max(max, values[i]);
        }

        ComputeScaleBias(min, max, out scale, out bias);
    }

    public static void ComputeScaleBias(Vector3 min, Vector3 max, out Vector3 scale, out Vector3 bias)
    {
        bias = (min + max) * 0.5f;
        Vector3 halfRange = (max - min) * 0.5f;
        scale = new Vector3(
            MathF.Max(halfRange.X, 1.0e-8f),
            MathF.Max(halfRange.Y, 1.0e-8f),
            MathF.Max(halfRange.Z, 1.0e-8f));
    }

    public static (short x, short y, short z) EncodeSnorm16(Vector3 value, Vector3 scale, Vector3 bias)
        => (
            EncodeSnorm16Component(value.X, scale.X, bias.X),
            EncodeSnorm16Component(value.Y, scale.Y, bias.Y),
            EncodeSnorm16Component(value.Z, scale.Z, bias.Z));

    public static Vector3 DecodeSnorm16((short x, short y, short z) encoded, Vector3 scale, Vector3 bias)
        => new(
            DecodeSnorm16Component(encoded.x, scale.X, bias.X),
            DecodeSnorm16Component(encoded.y, scale.Y, bias.Y),
            DecodeSnorm16Component(encoded.z, scale.Z, bias.Z));

    public static uint PackSnorm16Pair(short x, short y)
        => (ushort)x | ((uint)(ushort)y << 16);

    public static (short x, short y) UnpackSnorm16Pair(uint packed)
        => ((short)(packed & 0xFFFFu), (short)(packed >> 16));

    private static short EncodeSnorm16Component(float value, float scale, float bias)
    {
        float normalized = scale <= 0.0f ? 0.0f : (value - bias) / scale;
        normalized = Math.Clamp(normalized, -1.0f, 1.0f);
        return (short)MathF.Round(normalized * short.MaxValue);
    }

    private static float DecodeSnorm16Component(short encoded, float scale, float bias)
        => bias + Math.Clamp(encoded / (float)short.MaxValue, -1.0f, 1.0f) * scale;
}
