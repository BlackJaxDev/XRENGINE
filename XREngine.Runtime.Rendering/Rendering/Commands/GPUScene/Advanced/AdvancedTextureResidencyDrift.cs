using System.Numerics;

namespace XREngine.Rendering.Commands;

/// <summary>
/// Compares a published Advanced texture record with the current encoding of its source when
/// texture streaming changed the source after publication.
/// <para>
/// The backend binds the current image when it prepares the descriptor table, so a
/// publication from before a streaming commit can still render when the change keeps what the
/// shaders see. Two changes qualify:
/// </para>
/// <list type="bullet">
/// <item><description>
/// Placeholder: the published record described the import loading filler, which never drew
/// (the renderer used the binding fallback while the texture waited for its first resident
/// data). Only the dimension, layer count and depth flag must match; the GPU row gets the whole
/// current record.
/// </description></item>
/// <item><description>
/// Residency: every change was a dense streaming resident commit. The mip count, extent,
/// sampler LOD clamps and the format inside one format family can change; the dimension,
/// flags, layers, UV scale/bias and sampler modes must match. The GPU row gets the current
/// width, height, mip count and format class.
/// </description></item>
/// </list>
/// Any other change keeps the exact comparison.
/// </summary>
internal static class AdvancedTextureResidencyDrift
{
    /// <summary>
    /// Classifies how the source changed after <paramref name="capturedGeneration"/>. Only
    /// <see cref="XRTexture2D"/> sources can drift; arrays, cubes, capture outputs and sparse
    /// commits keep <see cref="EAdvancedTextureDriftKind.Exact"/>.
    /// </summary>
    internal static EAdvancedTextureDriftKind Classify(XRTexture source, ulong capturedGeneration, ulong currentGeneration)
    {
        if (currentGeneration == capturedGeneration || source is not XRTexture2D texture2D)
            return EAdvancedTextureDriftKind.Exact;
        if (texture2D.WasCanonicalPlaceholderAt(capturedGeneration))
            return EAdvancedTextureDriftKind.Placeholder;
        return texture2D.IsCanonicalResidencyOnlyChangeSince(capturedGeneration)
            ? EAdvancedTextureDriftKind.Residency
            : EAdvancedTextureDriftKind.Exact;
    }

    /// <summary>True when every change after publication was a dense streaming resident commit.</summary>
    internal static bool IsResidencyOnlyChange(XRTexture source, ulong capturedGeneration, ulong currentGeneration)
        => Classify(source, capturedGeneration, currentGeneration) == EAdvancedTextureDriftKind.Residency;

    /// <summary>
    /// Compares the current texture encoding with the published record for
    /// <paramref name="kind"/> and returns the first mismatch, or None.
    /// <paramref name="generationChanged"/> is true when the content generation differs.
    /// </summary>
    internal static EAdvancedTextureSourceMismatch CompareTexture(
        EAdvancedTextureDriftKind kind,
        bool generationChanged,
        in AdvancedTextureRecord current,
        in AdvancedTextureRecord published)
    {
        if (kind == EAdvancedTextureDriftKind.Placeholder)
            return PlaceholderShapeEquals(current, published)
                ? EAdvancedTextureSourceMismatch.None
                : EAdvancedTextureSourceMismatch.TextureShapeChanged;
        if (generationChanged && kind == EAdvancedTextureDriftKind.Exact)
            return EAdvancedTextureSourceMismatch.ContentGenerationChanged;
        if (current.Dimension != published.Dimension ||
            current.Flags != published.Flags ||
            current.DepthOrLayers != published.DepthOrLayers ||
            current.UvScaleBias != published.UvScaleBias)
            return EAdvancedTextureSourceMismatch.TextureShapeChanged;
        if (kind == EAdvancedTextureDriftKind.Residency)
            return FormatFamilyEquals(current.FormatClass, published.FormatClass)
                ? EAdvancedTextureSourceMismatch.None
                : EAdvancedTextureSourceMismatch.TextureFormatChanged;
        if (current.FormatClass != published.FormatClass)
            return EAdvancedTextureSourceMismatch.TextureFormatChanged;
        return current.Width != published.Width ||
               current.Height != published.Height ||
               current.MipCount != published.MipCount
            ? EAdvancedTextureSourceMismatch.TextureExtentOrMipsChanged
            : EAdvancedTextureSourceMismatch.None;
    }

    /// <summary>
    /// Compares the current sampler encoding with the published sampler record for
    /// <paramref name="kind"/>. A placeholder accepts any sampler, because the published sampler
    /// row stays valid and the placeholder never drew; a residency change ignores the LOD clamps.
    /// </summary>
    internal static bool SamplerEquals(
        EAdvancedTextureDriftKind kind,
        in AdvancedSamplerRecord current,
        in AdvancedSamplerRecord published)
        => kind switch
        {
            EAdvancedTextureDriftKind.Placeholder => true,
            EAdvancedTextureDriftKind.Residency => SamplerShapeEquals(current, published),
            _ => SamplerShapeEquals(current, published) &&
                 FloatBitsEqual(current.LodBiasMinMaxAnisotropy.Y, published.LodBiasMinMaxAnisotropy.Y) &&
                 FloatBitsEqual(current.LodBiasMinMaxAnisotropy.Z, published.LodBiasMinMaxAnisotropy.Z),
        };

    /// <summary>
    /// Compares the texture fields that a residency change cannot change: dimension, flags
    /// (sRGB, depth, storage), layer count and UV scale/bias, and the format family.
    /// </summary>
    internal static bool TextureShapeEquals(in AdvancedTextureRecord current, in AdvancedTextureRecord published)
        => CompareTexture(EAdvancedTextureDriftKind.Residency, generationChanged: true, current, published) ==
           EAdvancedTextureSourceMismatch.None;

    /// <summary>Compares the fields that a placeholder replacement must keep: dimension, layers and the depth flag.</summary>
    internal static bool PlaceholderShapeEquals(in AdvancedTextureRecord current, in AdvancedTextureRecord published)
        => current.Dimension == published.Dimension &&
           current.DepthOrLayers == published.DepthOrLayers &&
           (current.Flags & EAdvancedTextureRecordFlags.Depth) == (published.Flags & EAdvancedTextureRecordFlags.Depth);

    /// <summary>
    /// True when both format classes are equal or belong to one family whose formats sample
    /// the same way for a texture of one import:
    /// linear {Rgb8, Rgba8, Bc7Unorm, Bc4Unorm} or sRGB {Srgb8, Srgb8Alpha8, Bc7Srgb}.
    /// The texture cook chooses BC4 only for gray, opaque pixels, and the backends sample BC4
    /// as (R, R, R, 1). BC5 is not in a family; its (R, G, 0, 0) sampling relies on the
    /// normal-unpack helpers.
    /// </summary>
    internal static bool FormatFamilyEquals(uint current, uint published)
    {
        if (current == published)
            return true;
        int currentFamily = GetFormatFamily((EAdvancedTextureFormatClass)current);
        return currentFamily != 0 && currentFamily == GetFormatFamily((EAdvancedTextureFormatClass)published);
    }

    private static int GetFormatFamily(EAdvancedTextureFormatClass format)
        => format switch
        {
            EAdvancedTextureFormatClass.Rgb8 or
            EAdvancedTextureFormatClass.Rgba8 or
            EAdvancedTextureFormatClass.Bc7Unorm or
            EAdvancedTextureFormatClass.Bc4Unorm => 1,
            EAdvancedTextureFormatClass.Srgb8 or
            EAdvancedTextureFormatClass.Srgb8Alpha8 or
            EAdvancedTextureFormatClass.Bc7Srgb => 2,
            _ => 0,
        };

    /// <summary>
    /// Compares the sampler fields that a residency change cannot change: filter, flags,
    /// address modes, compare operation, LOD bias, anisotropy and border color. The minimum
    /// and maximum LOD clamps come from the mip count and are ignored; Vulkan clamps the
    /// sampled LOD to the levels of the bound image view.
    /// </summary>
    internal static bool SamplerShapeEquals(in AdvancedSamplerRecord current, in AdvancedSamplerRecord published)
        => current.Filter == published.Filter &&
           current.Flags == published.Flags &&
           current.AddressU == published.AddressU &&
           current.AddressV == published.AddressV &&
           current.AddressW == published.AddressW &&
           current.CompareOperation == published.CompareOperation &&
           FloatBitsEqual(current.LodBiasMinMaxAnisotropy.X, published.LodBiasMinMaxAnisotropy.X) &&
           FloatBitsEqual(current.LodBiasMinMaxAnisotropy.W, published.LodBiasMinMaxAnisotropy.W) &&
           VectorBitsEqual(current.BorderColor, published.BorderColor);

    /// <summary>
    /// Writes the fields of <paramref name="current"/> that <paramref name="kind"/> lets change
    /// into <paramref name="row"/>, so the GPU texture row describes the bound image. The row
    /// keeps its identity, encoded reference and default sampler.
    /// </summary>
    internal static void Apply(EAdvancedTextureDriftKind kind, ref AdvancedTextureRecord row, in AdvancedTextureRecord current)
    {
        if (kind == EAdvancedTextureDriftKind.Exact)
            return;
        row.Width = current.Width;
        row.Height = current.Height;
        row.MipCount = current.MipCount;
        row.FormatClass = current.FormatClass;
        if (kind != EAdvancedTextureDriftKind.Placeholder)
            return;
        row.Dimension = current.Dimension;
        row.Flags = current.Flags;
        row.DepthOrLayers = current.DepthOrLayers;
        row.UvScaleBias = current.UvScaleBias;
    }

    /// <summary>Writes the residency fields (width, height, mip count, format class) of <paramref name="current"/>.</summary>
    internal static void ApplyResidency(ref AdvancedTextureRecord row, in AdvancedTextureRecord current)
        => Apply(EAdvancedTextureDriftKind.Residency, ref row, current);

    private static bool VectorBitsEqual(Vector4 left, Vector4 right)
        => FloatBitsEqual(left.X, right.X) &&
           FloatBitsEqual(left.Y, right.Y) &&
           FloatBitsEqual(left.Z, right.Z) &&
           FloatBitsEqual(left.W, right.W);

    // +0 and -0 compare equal, as in the exact sampler comparison of the Vulkan runtime.
    private static bool FloatBitsEqual(float left, float right)
        => CanonicalBits(left) == CanonicalBits(right);

    private static uint CanonicalBits(float value)
        => value == 0.0f ? 0u : BitConverter.SingleToUInt32Bits(value);
}
