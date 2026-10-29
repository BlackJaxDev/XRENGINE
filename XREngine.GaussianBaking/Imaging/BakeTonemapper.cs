namespace XREngine.Rendering.Models.Gaussian.Baking.Imaging;

/// <summary>
/// Converts linear HDR pixels to 8-bit sRGB pixels for bake images and videos.
/// Color uses the fitted ACES curve (Narkowicz), then the sRGB transfer function.
/// </summary>
public static class BakeTonemapper
{
    /// <summary>A linear value at or above this value maps to full intensity.</summary>
    private const float SaturationInput = 16.0f;

    /// <summary>
    /// Converts linear RGBA floats to sRGB RGBA bytes. Each pixel uses four values in both spans.
    /// </summary>
    /// <param name="linearRgba">The linear RGBA values. Negative and NaN values become 0.</param>
    /// <param name="rgba8">The output bytes. The length must equal the length of <paramref name="linearRgba"/>.</param>
    /// <param name="exposure">The factor that the color channels are multiplied by before the curve. Alpha does not change.</param>
    public static void ToSrgb8(ReadOnlySpan<float> linearRgba, Span<byte> rgba8, float exposure = 1f)
    {
        if (linearRgba.Length != rgba8.Length)
            throw new ArgumentException("The output must have one byte for each input value.", nameof(rgba8));
        if (linearRgba.Length % 4 != 0)
            throw new ArgumentException("The input must have four values for each pixel.", nameof(linearRgba));

        for (int i = 0; i < linearRgba.Length; i += 4)
        {
            rgba8[i] = EncodeColor(linearRgba[i] * exposure);
            rgba8[i + 1] = EncodeColor(linearRgba[i + 1] * exposure);
            rgba8[i + 2] = EncodeColor(linearRgba[i + 2] * exposure);
            rgba8[i + 3] = ToByte(Saturate(linearRgba[i + 3]));
        }
    }

    private static byte EncodeColor(float linear)
    {
        float x = linear > 0.0f ? linear : 0.0f;

        // The curve reaches 1 near x = 7.24. Stop above that point, so that very large values cannot overflow to NaN.
        if (x >= SaturationInput)
            return byte.MaxValue;

        float mapped = Saturate(x * (2.51f * x + 0.03f) / (x * (2.43f * x + 0.59f) + 0.14f));
        float encoded = mapped <= 0.0031308f
            ? mapped * 12.92f
            : 1.055f * MathF.Pow(mapped, 1.0f / 2.4f) - 0.055f;
        return ToByte(Saturate(encoded));
    }

    /// <summary>Clamps a value to [0, 1]. NaN becomes 0.</summary>
    private static float Saturate(float value)
        => value > 0.0f ? (value < 1.0f ? value : 1.0f) : 0.0f;

    private static byte ToByte(float unit)
        => (byte)MathF.Round(unit * 255.0f, MidpointRounding.AwayFromZero);
}
