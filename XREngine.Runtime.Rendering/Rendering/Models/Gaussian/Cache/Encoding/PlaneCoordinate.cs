namespace XREngine.Rendering.Models.Gaussian.Cache.Encoding;

/// <summary>
/// Packs a coordinate on a triangle plane into 16 bits. The range from -1 to 2 maps linearly onto 0 to 65535.
/// </summary>
public static class PlaneCoordinate
{
    /// <summary>The smallest value that packs without a clamp.</summary>
    public const float MinValue = -1f;

    /// <summary>The largest value that packs without a clamp.</summary>
    public const float MaxValue = 2f;

    private const float RangeMin = MinValue;
    private const float RangeSize = MaxValue - MinValue;
    private const float MaxPacked = 65535f;

    /// <summary>
    /// Returns true when the barycentric weights (1 − u − v, u, v) are all in the range from <see cref="MinValue"/> to
    /// <see cref="MaxValue"/>. Then u and v pack without a clamp for each corner order of the triangle, because a turn
    /// of the corners makes 1 − u − v one of the stored values.
    /// </summary>
    public static bool IsInRange(float u, float v)
    {
        float w = 1f - u - v;
        return u >= MinValue && u <= MaxValue
            && v >= MinValue && v <= MaxValue
            && w >= MinValue && w <= MaxValue;
    }

    /// <summary>
    /// Packs a value with round-to-nearest. Values outside the range clamp to the border.
    /// NaN packs to the same value as 0.
    /// </summary>
    public static ushort Pack(float v)
    {
        if (float.IsNaN(v))
            v = 0f;
        float scaled = MathF.Round((v - RangeMin) / RangeSize * MaxPacked, MidpointRounding.AwayFromZero);
        if (scaled <= 0f)
            return 0;
        if (scaled >= MaxPacked)
            return ushort.MaxValue;
        return (ushort)scaled;
    }

    public static float Unpack(ushort v)
        => v / MaxPacked * RangeSize + RangeMin;
}
