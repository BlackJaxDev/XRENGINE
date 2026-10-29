using XREngine.Data.Rendering;

namespace XREngine.Rendering;

/// <summary>Retains sampling values without owning texture storage or pixel data.</summary>
internal readonly struct TextureSamplingSettings(XRTexture2D source)
{
    private readonly string? _samplerName = source.SamplerName;
    private readonly ETexMagFilter _magFilter = source.MagFilter;
    private readonly ETexMinFilter _minFilter = source.MinFilter;
    private readonly ETexWrapMode _uWrap = source.UWrap;
    private readonly ETexWrapMode _vWrap = source.VWrap;
    private readonly int _minLod = source.MinLOD;
    private readonly int _maxLod = source.MaxLOD;
    private readonly float _lodBias = source.LodBias;
    private readonly float _maxAnisotropy = source.MaxAnisotropy;
    private readonly bool _enableComparison = source.EnableComparison;
    private readonly ETextureCompareFunc _compareFunc = source.CompareFunc;
    private readonly ETextureColorSpace _colorSpace = source.ImportedColorSpace;
    private readonly ETextureImportUsage _usage = source.ImportedUsage;
    private readonly bool _flipGreen = source.ImportedNormalMapFlipGreen;
    private readonly bool _alphaAsTransparency = source.AlphaAsTransparency;

    public float LodBias => _lodBias;
    public int MinLOD => _minLod;
    public int MaxLOD => _maxLod;

    public void ApplyTo(XRTexture2D target)
    {
        target.SamplerName = _samplerName;
        target.MagFilter = _magFilter;
        target.MinFilter = _minFilter;
        target.UWrap = _uWrap;
        target.VWrap = _vWrap;
        target.MinLOD = _minLod;
        target.MaxLOD = _maxLod;
        target.LodBias = _lodBias;
        target.MaxAnisotropy = _maxAnisotropy;
        target.EnableComparison = _enableComparison;
        target.CompareFunc = _compareFunc;
        target.ImportedColorSpace = _colorSpace;
        target.ImportedUsage = _usage;
        target.ImportedNormalMapFlipGreen = _flipGreen;
        target.AlphaAsTransparency = _alphaAsTransparency;
    }

    /// <summary>Retains authored filters while exposing the sole resident mip as level zero.</summary>
    public void ApplyToPreview(XRTexture2D target)
    {
        ApplyTo(target);
        target.MinLOD = 0;
        target.MaxLOD = 0;
    }
}
