using System.Globalization;

namespace XREngine.Rendering;

/// <summary>Explicit bounded mobile quality policy; unsupported features are rejected instead of substituted.</summary>
public sealed record BrowserPipelineQualitySettings
{
    public float ResolutionScale { get; init; } = 1;
    public float MaxDevicePixelRatio { get; init; } = 2;
    public int ShadowResolution { get; init; } = 1024;
    public int ShadowUpdateInterval { get; init; } = 1;
    public int DirectionalLightCount { get; init; } = 1;
    public string TextureTier { get; init; } = "rgba8";
    public int MaxTextureDimension { get; init; } = 2048;
    public int MaxMaterials { get; init; } = 256;
    public string MaxMaterialComplexity { get; init; } = "lambert";
    public bool Hdr { get; init; }
    public string ToneMap { get; init; } = "none";
    public bool UiEnabled { get; init; } = true;

    public void Validate()
    {
        if (!float.IsFinite(ResolutionScale) || ResolutionScale is < 0.25f or > 1 ||
            !float.IsFinite(MaxDevicePixelRatio) || MaxDevicePixelRatio is < 0.5f or > 4 ||
            ShadowResolution is < 128 or > 4096 || (ShadowResolution & (ShadowResolution - 1)) != 0 ||
            ShadowUpdateInterval is < 1 or > 120 || DirectionalLightCount is < 0 or > 1 ||
            TextureTier != "rgba8" || MaxTextureDimension is < 128 or > 8192 || MaxMaterials is < 1 or > 4096 ||
            MaxMaterialComplexity is not ("unlit" or "lambert") || ToneMap is not ("none" or "reinhard") ||
            (!Hdr && ToneMap != "none"))
            throw new NotSupportedException("Browser quality requires bounded resolution, one directional light, RGBA8 textures, unlit/flat Lambert surfaces, and an optional HDR Reinhard output.");
    }

    /// <summary>Cold-path configuration payload; no JSON is generated during frame submission.</summary>
    public string ToJson()
    {
        Validate();
        return $"{{\"resolutionScale\":{ResolutionScale.ToString("R", CultureInfo.InvariantCulture)},\"maxDevicePixelRatio\":{MaxDevicePixelRatio.ToString("R", CultureInfo.InvariantCulture)},\"shadowResolution\":{ShadowResolution},\"shadowUpdateInterval\":{ShadowUpdateInterval},\"directionalLightCount\":{DirectionalLightCount},\"textureTier\":\"{TextureTier}\",\"maxTextureDimension\":{MaxTextureDimension},\"maxMaterials\":{MaxMaterials},\"maxMaterialComplexity\":\"{MaxMaterialComplexity}\",\"hdr\":{(Hdr ? "true" : "false")},\"toneMap\":\"{ToneMap}\",\"uiEnabled\":{(UiEnabled ? "true" : "false")}}}";
    }
}
