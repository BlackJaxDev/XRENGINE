namespace XREngine.Rendering;

/// <summary>Immutable texture payload with explicit encoding, color space and complete cooked mip chains.</summary>
public sealed class BrowserTextureData
{
    private const int MaximumBytes = 64 * 1024 * 1024;
    private readonly byte[] _bytes;
    private readonly int[] _mipOffsets;

    /// <summary>Creates the original single-level sRGB color texture used by procedural content.</summary>
    public BrowserTextureData(int width, int height, ReadOnlySpan<byte> rgba)
    {
        if (width <= 0 || height <= 0 || (long)width * height * 4 != rgba.Length || rgba.Length > MaximumBytes)
            throw new ArgumentException("Texture pixels must contain exactly width × height × four RGBA8 bytes, at most 64 MiB.");
        Width = width;
        Height = height;
        Format = "rgba8unorm-srgb";
        NormalConvention = "none";
        AlphaMode = "straight";
        _bytes = rgba.ToArray();
        _mipOffsets = [0, _bytes.Length];
    }

    /// <summary>Copies pre-encoded mips largest-to-smallest. Compressed rows contain tightly packed 4×4 blocks.</summary>
    public BrowserTextureData(int width, int height, string format, IReadOnlyList<byte[]> mips,
        string normalConvention = "none", string alphaMode = "straight")
    {
        ArgumentNullException.ThrowIfNull(mips);
        BrowserTextureFormat.Validate(width, height, format, normalConvention, alphaMode);
        int expectedMips = BrowserTextureFormat.GetMipCount(width, height);
        if (mips.Count != expectedMips)
            throw new ArgumentException("Cooked textures require every mip down to 1×1.", nameof(mips));
        _mipOffsets = new int[expectedMips + 1];
        long total = 0;
        for (int mip = 0; mip < mips.Count; mip++)
        {
            int expectedBytes = BrowserTextureFormat.GetMipByteLength(width, height, format, mip);
            if (mips[mip] is not { } bytes || bytes.Length != expectedBytes)
                throw new ArgumentException($"Cooked texture mip {mip} has an invalid tightly packed byte count.", nameof(mips));
            total += expectedBytes;
            if (total > MaximumBytes)
                throw new ArgumentException("Cooked texture payload exceeds 64 MiB.", nameof(mips));
            _mipOffsets[mip + 1] = (int)total;
        }
        Width = width;
        Height = height;
        Format = format;
        NormalConvention = normalConvention;
        AlphaMode = alphaMode;
        _bytes = new byte[(int)total];
        for (int mip = 0; mip < mips.Count; mip++)
            mips[mip].AsSpan().CopyTo(_bytes.AsSpan(_mipOffsets[mip]));
    }

    public int Width { get; }
    public int Height { get; }
    public string Format { get; }
    public int MipCount => _mipOffsets.Length - 1;
    public int ByteLength => _bytes.Length;
    public string NormalConvention { get; }
    public string AlphaMode { get; }
    public bool IsSrgb => Format.EndsWith("-srgb", StringComparison.Ordinal);

    public ReadOnlySpan<byte> GetMipBytes(int mip)
    {
        if ((uint)mip >= (uint)MipCount)
            throw new ArgumentOutOfRangeException(nameof(mip));
        return _bytes.AsSpan(_mipOffsets[mip], _mipOffsets[mip + 1] - _mipOffsets[mip]);
    }

    public byte[] CopyMipBytes(int mip) => GetMipBytes(mip).ToArray();
    public byte[] CopyPackedBytes() => (byte[])_bytes.Clone();

    /// <summary>The legacy scene JSON cannot represent compressed, linear, normal or mipmapped textures.</summary>
    public byte[] CopyRgbaBytes()
    {
        if (Format != "rgba8unorm-srgb" || MipCount != 1 || NormalConvention != "none")
            throw new NotSupportedException("Legacy scene snapshots only support single-level sRGB RGBA8 color; use the cooked content package for this texture.");
        return (byte[])_bytes.Clone();
    }
}
