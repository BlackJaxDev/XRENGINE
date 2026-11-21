using System;
using XREngine.Data;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.OpenGL;

public partial class GLTexture2D
{
    private enum SamplingEquivalenceKind : byte
    {
        None = 0,
        ConstantBinaryRgba = 1,
        StaticContent = 2,
    }

    private readonly record struct SamplingEquivalenceSignature(
        SamplingEquivalenceKind Kind,
        uint ConstantRgba,
        uint Width,
        uint Height,
        int MipCount,
        ESizedInternalFormat SizedInternalFormat,
        ETexWrapMode UWrap,
        ETexWrapMode VWrap,
        ETexMinFilter MinFilter,
        ETexMagFilter MagFilter,
        float LodBias,
        int LargestMipmapLevel,
        int SmallestAllowedMipmapLevel,
        int Mip0ByteLength,
        ulong Mip0ContentHash);

    private bool _samplingEquivalenceCached;
    private SamplingEquivalenceSignature _samplingEquivalence;

    /// <summary>
    /// Marks the cached sampling-equivalence signature dirty when pixel data, dimensions, or sampler parameters change.
    /// </summary>
    internal void InvalidateSamplingEquivalenceCache()
        => _samplingEquivalenceCached = false;

    /// <summary>
    /// Returns true when <paramref name="other"/> produces the same sampled texel values as this texture on a 2D sampler unit.
    /// </summary>
    /// <remarks>
    /// OpenGL fragment shaders provide a bounded set of texture image units (typically 32). Imported Uber materials
    /// assign distinct <see cref="XRTexture2D"/> objects per sampler slot so each slot keeps its own sampler uniform
    /// name, even when dozens of slots hold 1x1 white, black, or flat-normal fallback textures or share identical
    /// cooked image bytes. Reusing one bound unit across sampling-equivalent immutable 2D textures keeps all active
    /// material samplers within the fragment texture unit budget.
    /// </remarks>
    internal bool IsSamplingEquivalentTo(GLTexture2D other)
    {
        if (ReferenceEquals(this, other))
            return true;

        SamplingEquivalenceSignature selfSig = GetSamplingEquivalenceSignature();
        if (selfSig.Kind == SamplingEquivalenceKind.None)
            return false;

        SamplingEquivalenceSignature otherSig = other.GetSamplingEquivalenceSignature();
        if (selfSig.Kind != otherSig.Kind)
            return false;

        if (selfSig.Kind == SamplingEquivalenceKind.ConstantBinaryRgba)
            return selfSig.ConstantRgba == otherSig.ConstantRgba;

        if (selfSig != otherSig)
            return false;

        return TryGetMip0Bytes(out ReadOnlySpan<byte> selfBytes)
            && other.TryGetMip0Bytes(out ReadOnlySpan<byte> otherBytes)
            && selfBytes.SequenceEqual(otherBytes);
    }

    private SamplingEquivalenceSignature GetSamplingEquivalenceSignature()
    {
        if (!IsEligibleForSamplingEquivalence())
            return default;

        if (_samplingEquivalenceCached)
            return _samplingEquivalence;

        _samplingEquivalence = ComputeSamplingEquivalenceSignature();
        _samplingEquivalenceCached = true;
        return _samplingEquivalence;
    }

    private bool IsEligibleForSamplingEquivalence()
        => !IsMultisampleTarget
            && !Data.Resizable
            && !Data.IsGpuWritable
            && Data.GrabPass is null
            && !Data.FrameBufferAttachment.HasValue
            && !Data.SparseTextureStreamingEnabled
            && !Data.UsesOpenGlExternalMemoryImport
            && !Data.EnableComparison
            && Mipmaps is { Length: > 0 }
            && Data.Mipmaps is { Length: > 0 };

    private SamplingEquivalenceSignature ComputeSamplingEquivalenceSignature()
    {
        if (!TryGetMip0Bytes(out ReadOnlySpan<byte> bytes))
            return default;

        if (TryComputeConstantBinaryRgba(bytes, out uint constantRgba))
        {
            return new SamplingEquivalenceSignature(
                SamplingEquivalenceKind.ConstantBinaryRgba,
                constantRgba,
                0u,
                0u,
                0,
                default,
                default,
                default,
                default,
                default,
                0.0f,
                0,
                0,
                0,
                0ul);
        }

        return new SamplingEquivalenceSignature(
            SamplingEquivalenceKind.StaticContent,
            0u,
            Data.Width,
            Data.Height,
            Mipmaps.Length,
            Data.SizedInternalFormat,
            Data.UWrap,
            Data.VWrap,
            Data.MinFilter,
            Data.MagFilter,
            Data.LodBias,
            Data.LargestMipmapLevel,
            Data.SmallestAllowedMipmapLevel,
            bytes.Length,
            ComputeFnv1a64(bytes));
    }

    private bool TryComputeConstantBinaryRgba(ReadOnlySpan<byte> bytes, out uint constantRgba)
    {
        constantRgba = 0u;
        if (Mipmaps.Length != 1
            || Data.Width is not (1u or 2u)
            || Data.Height is not (1u or 2u)
            || Data.SizedInternalFormat is not (ESizedInternalFormat.Rgba8 or ESizedInternalFormat.Srgb8Alpha8)
            || Data.Mipmaps[0] is not { PixelFormat: EPixelFormat.Rgba, PixelType: EPixelType.UnsignedByte })
        {
            return false;
        }

        if (bytes.Length < 4 || (bytes.Length & 3) != 0)
            return false;

        byte r = bytes[0];
        byte g = bytes[1];
        byte b = bytes[2];
        byte a = bytes[3];

        // 0 and 255 decode to exact 0.0 and 1.0 in both linear RGBA8 and sRGB8_ALPHA8,
        // and uniform 1x1/2x2 single-mip textures sample identically across wrap/filter modes.
        if ((r != 0 && r != 255)
            || (g != 0 && g != 255)
            || (b != 0 && b != 255)
            || (a != 0 && a != 255))
        {
            return false;
        }

        ReadOnlySpan<byte> firstTexel = bytes[..4];
        for (int offset = 4; offset < bytes.Length; offset += 4)
        {
            if (!bytes.Slice(offset, 4).SequenceEqual(firstTexel))
                return false;
        }

        constantRgba = r | ((uint)g << 8) | ((uint)b << 16) | ((uint)a << 24);
        return true;
    }

    private bool TryGetMip0Bytes(out ReadOnlySpan<byte> bytes)
    {
        if (Data.Mipmaps is { Length: > 0 } mips
            && mips[0]?.Data is { IsDisposed: false, Length: > 0 } mipData)
        {
            bytes = mipData.AsReadOnlySpan();
            return bytes.Length > 0;
        }

        bytes = default;
        return false;
    }

    private static ulong ComputeFnv1a64(ReadOnlySpan<byte> bytes)
    {
        const ulong offsetBasis = 14695981039346656037ul;
        const ulong prime = 1099511628211ul;

        ulong hash = offsetBasis;
        for (int i = 0; i < bytes.Length; i++)
            hash = (hash ^ bytes[i]) * prime;

        return hash;
    }
}
