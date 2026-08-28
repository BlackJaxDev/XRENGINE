using System.Security.Cryptography;
using System.Text;

namespace XREngine.Rendering.Models.Caching;

/// <summary>
/// Fixed 128-bit value used for truncated compatibility fingerprints.
/// </summary>
internal readonly struct ModelBinaryHash128 : IEquatable<ModelBinaryHash128>
{
    public const int Size = 16;

    private static readonly byte[] ZeroBytes = new byte[Size];
    private readonly byte[]? _bytes;

    public ModelBinaryHash128(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != Size)
            throw new ArgumentException($"A model-cache hash must contain exactly {Size} bytes.", nameof(bytes));

        _bytes = bytes.ToArray();
    }

    public static ModelBinaryHash128 Zero => new(ZeroBytes);

    public ReadOnlySpan<byte> Bytes => _bytes ?? ZeroBytes;

    public static ModelBinaryHash128 FromHexPrefix(string hexadecimal)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hexadecimal);
        if (hexadecimal.Length < Size * 2)
            throw new ArgumentException("A model-cache hash needs at least 128 bits of hexadecimal input.", nameof(hexadecimal));

        return new ModelBinaryHash128(Convert.FromHexString(hexadecimal[..(Size * 2)]));
    }

    public static ModelBinaryHash128 HashUtf8(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return new ModelBinaryHash128(digest.AsSpan(0, Size));
    }

    public void CopyTo(Span<byte> destination)
    {
        if (destination.Length < Size)
            throw new ArgumentException($"The destination must have room for {Size} bytes.", nameof(destination));

        Bytes.CopyTo(destination);
    }

    public bool Equals(ModelBinaryHash128 other)
        => Bytes.SequenceEqual(other.Bytes);

    public override bool Equals(object? obj)
        => obj is ModelBinaryHash128 other && Equals(other);

    public override int GetHashCode()
    {
        HashCode hash = new();
        ReadOnlySpan<byte> bytes = Bytes;
        for (int i = 0; i < bytes.Length; i++)
            hash.Add(bytes[i]);
        return hash.ToHashCode();
    }

    public override string ToString() => Convert.ToHexString(Bytes).ToLowerInvariant();
}
