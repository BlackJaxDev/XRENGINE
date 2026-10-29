namespace XREngine.Rendering.Models.Gaussian.Cache.Encoding;

/// <summary>Converts between linear values and 8-bit sRGB values.</summary>
public static class Srgb8
{
    /// <summary>Clamps to the range 0 to 1, applies the sRGB transfer function, and rounds to nearest.</summary>
    public static byte FromLinear(float v)
    {
        if (!(v > 0f))
            return 0;
        if (v >= 1f)
            return 255;
        float encoded = v <= 0.0031308f
            ? v * 12.92f
            : 1.055f * MathF.Pow(v, 1f / 2.4f) - 0.055f;
        return (byte)MathF.Round(encoded * 255f, MidpointRounding.AwayFromZero);
    }

    /// <summary>Applies the inverse sRGB transfer function to an 8-bit value.</summary>
    public static float ToLinear(byte v)
    {
        float encoded = v / 255f;
        return encoded <= 0.04045f
            ? encoded / 12.92f
            : MathF.Pow((encoded + 0.055f) / 1.055f, 2.4f);
    }
}
