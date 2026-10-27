namespace XREngine.Rendering.Models.Gaussian.Cache.Encoding;

/// <summary>
/// Packs a coordinate on a triangle plane into 16 bits. The range from -1 to 2 maps linearly onto 0 to 65535.
/// </summary>
public static class PlaneCoordinate
{
    private const float RangeMin = -1f;
    private const float RangeSize = 3f;
    private const float MaxPacked = 65535f;

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
