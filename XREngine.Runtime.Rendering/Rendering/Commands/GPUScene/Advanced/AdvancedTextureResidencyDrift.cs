using System.Numerics;

namespace XREngine.Rendering.Commands;

/// <summary>
/// Compares a published Advanced texture record with the current encoding of its source when
/// texture streaming changed the source after publication.
/// <para>
/// A dense streaming resident commit replaces the texture's mip chain: the mip count, the
/// extent and the sampler LOD clamps can change, but the dimension, format class, flags, layers
/// and the sampler modes stay the same. The backend binds the current image when it prepares
/// the descriptor table, so a publication from before such a commit can still render. The
/// backend accepts it when the shape fields match, and writes the residency fields of the
/// current encoding into the GPU texture row. Any other change keeps the exact comparison.
/// </para>
/// </summary>
internal static class AdvancedTextureResidencyDrift
{
    /// <summary>
    /// True when the source changed after publication (other content generation) and every
    /// change was a dense streaming resident commit. Only <see cref="XRTexture2D"/> sources
    /// qualify; arrays, cubes, capture outputs and sparse commits keep the exact check.
    /// </summary>
    internal static bool IsResidencyOnlyChange(XRTexture source, ulong capturedGeneration, ulong currentGeneration)
        => currentGeneration != capturedGeneration &&
           source is XRTexture2D texture2D &&
           texture2D.IsCanonicalResidencyOnlyChangeSince(capturedGeneration);

    /// <summary>
    /// Compares the texture fields that a residency change cannot change: dimension, flags,
    /// layer count, format class and UV scale/bias. Width, height and mip count are ignored.
    /// </summary>
    internal static bool TextureShapeEquals(in AdvancedTextureRecord current, in AdvancedTextureRecord published)
        => current.Dimension == published.Dimension &&
           current.Flags == published.Flags &&
           current.DepthOrLayers == published.DepthOrLayers &&
           current.FormatClass == published.FormatClass &&
           current.UvScaleBias == published.UvScaleBias;

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
    /// Writes the width, height and mip count of <paramref name="current"/> into
    /// <paramref name="row"/>, so the GPU texture row describes the bound image.
    /// </summary>
    internal static void ApplyResidency(ref AdvancedTextureRecord row, in AdvancedTextureRecord current)
    {
        row.Width = current.Width;
        row.Height = current.Height;
        row.MipCount = current.MipCount;
    }

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
