using XREngine.Data.Rendering;

namespace XREngine.Rendering;

/// <summary>Collision-free browser-only identity for a frozen sampled view and its relative LOD clamps.</summary>
public readonly record struct AdvancedEngineSurfaceSamplingKey(uint Value)
{
    private const uint Present = 1u << 27;
    public int StorageMipCount => checked((int)(Value & 31u)) + 1;
    public int BaseMip => checked((int)((Value >> 5) & 31u));
    public int ViewMipCount => checked((int)((Value >> 10) & 31u)) + 1;
    public int MinLod => checked((int)((Value >> 15) & 63u));
    public int MaxLod => checked((int)((Value >> 21) & 63u));
    public bool IsValid => (Value & 0xf8000000u) == Present && BaseMip < StorageMipCount &&
        ViewMipCount <= StorageMipCount - BaseMip && MinLod <= MaxLod && MaxLod <= 32;

    /// <summary>Applies the preserved relative clamps without changing canonical sampler records.</summary>
    public AdvancedSamplerRecord ApplyTo(in AdvancedSamplerRecord source)
    {
        if (!IsValid) throw new ArgumentException("A frozen engine sampling identity is required.");
        AdvancedSamplerRecord result = source;
        result.LodBiasMinMaxAnisotropy.Y = MinLod;
        result.LodBiasMinMaxAnisotropy.Z = MaxLod;
        return result;
    }

    public static bool TryCapture(XRTexture? source, out uint value, out string reason)
    {
        value = 0;
        reason = string.Empty;
        if (source is null) return true;
        if (source is not XRTexture2D texture)
        { reason = "Native engine surface sampling requires a retained ordinary 2D texture."; return false; }
        int storage = texture.Mipmaps.Length;
        int first = texture.LargestMipmapLevel;
        int last = Math.Min(storage - 1, texture.SmallestAllowedMipmapLevel);
        bool mipmapped = texture.MinFilter is ETexMinFilter.NearestMipmapNearest or ETexMinFilter.LinearMipmapNearest or
            ETexMinFilter.NearestMipmapLinear or ETexMinFilter.LinearMipmapLinear;
        int min = mipmapped ? Math.Max(texture.MinLOD, 0) : 0;
        int max = mipmapped ? Math.Min(texture.MaxLOD, 32) : 0;
        if (storage is < 1 or > 32 || first < 0 || first > last || min > max || min > 32 || max < 0 ||
            !mipmapped && (texture.MinLOD > 0 || texture.MaxLOD < 0))
        { reason = "Native engine surface sampling requires a nonempty stored mip view and valid relative LOD clamps."; return false; }
        value = Present | (uint)(storage - 1) | ((uint)first << 5) | ((uint)(last - first) << 10) |
            ((uint)min << 15) | ((uint)max << 21);
        return true;
    }

    /// <summary>
    /// The array owns its sampled mip view and LOD clamps. Its first slice supplies
    /// the delegated minification filter, while automatic mip generation expands
    /// the physical storage beyond the authored slice chain.
    /// </summary>
    public static bool TryCapture(XRTexture2DArray array, out uint value, out string reason)
    {
        value = 0;
        reason = "Native engine array sampling requires a nonempty first slice and positive extent.";
        if (array.Textures is not { Length: > 0 } || array.Textures[0] is not { } firstSlice ||
            array.Width == 0 || array.Height == 0 || firstSlice.Mipmaps.Length == 0)
            return false;

        bool automatic = array.AutoGenerateMipmaps || firstSlice.AutoGenerateMipmaps;
        int storage = automatic
            ? 1 + System.Numerics.BitOperations.Log2(Math.Max(array.Width, array.Height))
            : firstSlice.Mipmaps.Length;
        int first = array.LargestMipmapLevel;
        int last = Math.Min(storage - 1, array.SmallestAllowedMipmapLevel);
        bool mipmapped = array.MinFilter is ETexMinFilter.NearestMipmapNearest or ETexMinFilter.LinearMipmapNearest or
            ETexMinFilter.NearestMipmapLinear or ETexMinFilter.LinearMipmapLinear;
        int min = mipmapped ? Math.Max(array.MinLOD, 0) : 0;
        int max = mipmapped ? Math.Min(array.MaxLOD, 32) : 0;
        if (storage is < 1 or > 32 || first < 0 || first > last || min > max || min > 32 || max < 0 ||
            !mipmapped && (array.MinLOD > 0 || array.MaxLOD < 0) || automatic && first != 0)
        {
            reason = "Native engine array sampling requires the exact array-owned sampled mip view, LOD clamps, and automatic-mip base level.";
            return false;
        }
        value = Present | (uint)(storage - 1) | ((uint)first << 5) | ((uint)(last - first) << 10) |
            ((uint)min << 15) | ((uint)max << 21);
        reason = string.Empty;
        return true;
    }
}
