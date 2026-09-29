using System.Globalization;
using System.Numerics;

namespace XREngine.Rendering;

/// <summary>Bound-texture surface policy for the focused browser forward renderer.</summary>
public sealed class BrowserMaterialData
{
    public BrowserMaterialData(Vector4 tint, BrowserTextureData? texture = null,
        string alphaMode = "opaque", string shading = "unlit", string cullMode = "none",
        float alphaCutoff = 0.5f, bool castShadow = true, bool receiveShadow = true,
        BrowserSamplerDescription? sampler = null)
    {
        if (!float.IsFinite(tint.X) || !float.IsFinite(tint.Y) || !float.IsFinite(tint.Z) || !float.IsFinite(tint.W) ||
            tint.X < 0 || tint.X > 1 || tint.Y < 0 || tint.Y > 1 || tint.Z < 0 || tint.Z > 1 || tint.W < 0 || tint.W > 1)
            throw new ArgumentException("Material tint must be finite and normalized.", nameof(tint));
        if (alphaMode is not ("opaque" or "masked" or "transparent") || shading is not ("unlit" or "lambert") ||
            cullMode is not ("none" or "front" or "back") || !float.IsFinite(alphaCutoff) || alphaCutoff < 0 || alphaCutoff > 1)
            throw new ArgumentException("Material alpha, shading, culling or cutoff is unsupported.");
        if (alphaMode == "transparent" && castShadow)
            throw new NotSupportedException("Transparent shadow casting is unsupported; explicitly set castShadow to false.");
        Tint = tint;
        Texture = texture;
        AlphaMode = alphaMode;
        Shading = shading;
        CullMode = cullMode;
        AlphaCutoff = alphaCutoff;
        CastShadow = castShadow;
        ReceiveShadow = receiveShadow;
        Sampler = sampler;
    }

    public Vector4 Tint { get; }
    public BrowserTextureData? Texture { get; }
    public string AlphaMode { get; }
    public string Shading { get; }
    public string CullMode { get; }
    public float AlphaCutoff { get; }
    public bool CastShadow { get; }
    public bool ReceiveShadow { get; }
    public BrowserSamplerDescription? Sampler { get; }

    public BrowserMaterialData WithTint(Vector4 tint) =>
        new(tint, Texture, AlphaMode, Shading, CullMode, AlphaCutoff, CastShadow, ReceiveShadow, Sampler);

    /// <summary>Cold-path immutable material configuration, separate from its GPU tint and texture handles.</summary>
    public string ToPipelineJson()
    {
        string sampler = Sampler is null ? string.Empty : ",\"sampler\":" + Sampler.ToPipelineJson();
        return $"{{\"alphaMode\":\"{AlphaMode}\",\"shading\":\"{Shading}\",\"cullMode\":\"{CullMode}\",\"alphaCutoff\":{AlphaCutoff.ToString("R", CultureInfo.InvariantCulture)},\"castShadow\":{(CastShadow ? "true" : "false")},\"receiveShadow\":{(ReceiveShadow ? "true" : "false")}{sampler}}}";
    }
}
