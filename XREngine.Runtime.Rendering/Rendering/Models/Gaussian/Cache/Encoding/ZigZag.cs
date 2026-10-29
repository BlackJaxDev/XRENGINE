namespace XREngine.Rendering.Models.Gaussian.Cache.Encoding;

/// <summary>Maps signed integers to unsigned integers so that values near zero stay small.</summary>
public static class ZigZag
{
    /// <summary>Encodes a value in the range of a 16-bit signed integer.</summary>
    /// <exception cref="OverflowException">The value is outside the 16-bit signed range.</exception>
    public static ushort Encode16(int v)
    {
        if (v < short.MinValue || v > short.MaxValue)
            throw new OverflowException($"The value {v} does not fit in a 16-bit signed integer.");
        return (ushort)((v << 1) ^ (v >> 31));
    }

    public static int Decode16(ushort v)
        => (v >> 1) ^ -(v & 1);

    public static uint Encode32(int v)
        => (uint)((v << 1) ^ (v >> 31));

    public static int Decode32(uint v)
        => (int)(v >> 1) ^ -(int)(v & 1u);
}
