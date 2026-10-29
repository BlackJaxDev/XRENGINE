using System.Globalization;

namespace XREngine.Rendering.Models.Gaussian.Baking.Sampling;

/// <summary>
/// A SplitMix64 random generator. The same seed always gives the same sequence on every machine.
/// </summary>
/// <param name="seed">The first state of the generator.</param>
public sealed class BakeRandom(ulong seed)
{
    private const ulong Increment = 0x9E3779B97F4A7C15UL;

    private ulong _state = seed;

    /// <summary>Returns the next 64-bit value.</summary>
    public ulong NextUInt64()
    {
        _state += Increment;
        ulong z = _state;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    /// <summary>Returns the next value in the range from 0 (included) to 1 (not included), with 24 bits of precision.</summary>
    public float NextFloat01()
        => (NextUInt64() >> 40) * (1.0f / 16777216.0f);

    /// <summary>
    /// Makes a seed from the SHA-256 hash of the input and the kind of bake.
    /// </summary>
    /// <param name="sha256Hex">The hash as hexadecimal text. The first 16 digits are used.</param>
    /// <param name="kind">The kind of bake.</param>
    public static ulong SeedFrom(string sha256Hex, GaussianBakeKind kind)
    {
        ArgumentNullException.ThrowIfNull(sha256Hex);
        if (sha256Hex.Length < 16)
            throw new ArgumentException("The hash must have at least 16 hexadecimal digits.", nameof(sha256Hex));

        ulong prefix = ulong.Parse(sha256Hex.AsSpan(0, 16), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
        return prefix ^ Increment ^ (ulong)kind;
    }
}
