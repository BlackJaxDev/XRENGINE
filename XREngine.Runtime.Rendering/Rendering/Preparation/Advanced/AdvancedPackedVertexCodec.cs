using System.Numerics;
using XREngine.Data.Rendering;

namespace XREngine.Rendering;

/// <summary>
/// Cross-backend packing helpers for the canonical deformation vertex.
/// </summary>
public static class AdvancedPackedVertexCodec
{
    private const float Snorm16Scale = 32767.0f;
    private const float Snorm15Scale = 16383.0f;

    public static AdvancedDeformedVertex Pack(
        Vertex vertex,
        uint sourceVertex)
    {
        ArgumentNullException.ThrowIfNull(vertex);

        return Pack(
            vertex.Position,
            vertex.Normal ?? Vector3.UnitY,
            vertex.Tangent ?? Vector3.UnitX,
            vertex.BitangentSign,
            GetOrDefault(vertex.TextureCoordinateSets, 0),
            GetOrDefault(vertex.TextureCoordinateSets, 1),
            GetOrDefault(vertex.ColorSets, 0, Vector4.One),
            GetOrDefault(vertex.ColorSets, 1, Vector4.One),
            sourceVertex);
    }

    public static AdvancedDeformedVertex Pack(
        Vector3 position,
        Vector3 normal,
        Vector3 tangent,
        float bitangentSign,
        Vector2 texCoord0,
        Vector2 texCoord1,
        Vector4 color0,
        Vector4 color1,
        uint sourceVertex)
        => new()
        {
            Position = position,
            NormalOct = EncodeOct(NormalizeOr(normal, Vector3.UnitY)),
            TangentOctAndSign = EncodeTangentOct(
                NormalizeOr(tangent, Vector3.UnitX),
                bitangentSign),
            TexCoord0Half = PackHalf2(texCoord0),
            TexCoord1Half = PackHalf2(texCoord1),
            Color0Rgba8 = PackRgba8(color0),
            Color1Rgba8 = PackRgba8(color1),
            SourceVertex = sourceVertex,
        };

    public static uint EncodeOct(Vector3 value)
    {
        Vector3 normal = NormalizeOr(value, Vector3.UnitY);
        float inverseL1 =
            1.0f / (MathF.Abs(normal.X) +
                    MathF.Abs(normal.Y) +
                    MathF.Abs(normal.Z));
        Vector2 encoded = new(normal.X * inverseL1, normal.Y * inverseL1);
        if (normal.Z < 0.0f)
        {
            encoded = new Vector2(
                (1.0f - MathF.Abs(encoded.Y)) * SignNotZero(encoded.X),
                (1.0f - MathF.Abs(encoded.X)) * SignNotZero(encoded.Y));
        }

        ushort x = unchecked((ushort)QuantizeSnorm(encoded.X, Snorm16Scale));
        ushort y = unchecked((ushort)QuantizeSnorm(encoded.Y, Snorm16Scale));
        return x | ((uint)y << 16);
    }

    public static Vector3 DecodeOct(uint packed)
    {
        float x = unchecked((short)(packed & 0xFFFFu)) / Snorm16Scale;
        float y = unchecked((short)(packed >> 16)) / Snorm16Scale;
        Vector3 value = new(x, y, 1.0f - MathF.Abs(x) - MathF.Abs(y));
        if (value.Z < 0.0f)
        {
            float oldX = value.X;
            value.X = (1.0f - MathF.Abs(value.Y)) * SignNotZero(oldX);
            value.Y = (1.0f - MathF.Abs(oldX)) * SignNotZero(value.Y);
        }
        return NormalizeOr(value, Vector3.UnitY);
    }

    public static uint EncodeTangentOct(Vector3 value, float bitangentSign)
    {
        uint oct = EncodeOct(value);
        short x = unchecked((short)(oct & 0xFFFFu));
        short y16 = unchecked((short)(oct >> 16));
        float y = y16 / Snorm16Scale;
        int y15 = QuantizeSnorm(y, Snorm15Scale);
        uint packedY = unchecked((uint)y15) & 0x7FFFu;
        uint sign = bitangentSign < 0.0f ? 0x80000000u : 0u;
        return unchecked((ushort)x) | (packedY << 16) | sign;
    }

    public static Vector3 DecodeTangentOct(
        uint packed,
        out float bitangentSign)
    {
        bitangentSign = (packed & 0x80000000u) != 0u ? -1.0f : 1.0f;
        int packedY = checked((int)((packed >> 16) & 0x7FFFu));
        if ((packedY & 0x4000) != 0)
            packedY |= unchecked((int)0xFFFF8000u);

        short x = unchecked((short)(packed & 0xFFFFu));
        float xValue = x / Snorm16Scale;
        float yValue = packedY / Snorm15Scale;
        Vector3 value = new(
            xValue,
            yValue,
            1.0f - MathF.Abs(xValue) - MathF.Abs(yValue));
        if (value.Z < 0.0f)
        {
            float oldX = value.X;
            value.X = (1.0f - MathF.Abs(value.Y)) * SignNotZero(oldX);
            value.Y = (1.0f - MathF.Abs(oldX)) * SignNotZero(value.Y);
        }
        return NormalizeOr(value, Vector3.UnitX);
    }

    public static uint PackHalf2(Vector2 value)
        => BitConverter.HalfToUInt16Bits((Half)value.X) |
           ((uint)BitConverter.HalfToUInt16Bits((Half)value.Y) << 16);

    public static uint PackRgba8(Vector4 value)
        => QuantizeUnorm8(value.X) |
           ((uint)QuantizeUnorm8(value.Y) << 8) |
           ((uint)QuantizeUnorm8(value.Z) << 16) |
           ((uint)QuantizeUnorm8(value.W) << 24);

    private static int QuantizeSnorm(float value, float scale)
        => checked((int)MathF.Round(
            Math.Clamp(value, -1.0f, 1.0f) * scale));

    private static byte QuantizeUnorm8(float value)
        => checked((byte)MathF.Round(
            Math.Clamp(value, 0.0f, 1.0f) * 255.0f));

    private static float SignNotZero(float value)
        => value >= 0.0f ? 1.0f : -1.0f;

    private static Vector3 NormalizeOr(Vector3 value, Vector3 fallback)
        => value.LengthSquared() > 1.0e-20f &&
           float.IsFinite(value.X) &&
           float.IsFinite(value.Y) &&
           float.IsFinite(value.Z)
            ? Vector3.Normalize(value)
            : fallback;

    private static Vector2 GetOrDefault(
        IReadOnlyList<Vector2>? values,
        int index)
        => values is not null && (uint)index < (uint)values.Count
            ? values[index]
            : Vector2.Zero;

    private static Vector4 GetOrDefault(
        IReadOnlyList<Vector4>? values,
        int index,
        Vector4 fallback)
        => values is not null && (uint)index < (uint)values.Count
            ? values[index]
            : fallback;
}
