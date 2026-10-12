using MemoryPack;
using System.ComponentModel;
using System.Globalization;
using XREngine.Data.Core;

namespace XREngine;

/// <summary>Explicit resource and effect limits for the shared engine's browser WebGPU output.</summary>
[Serializable]
[MemoryPackable]
public partial class BrowserWebGpuQualitySettings : XRBase
{
    private float _resolutionScale = 1;
    private float _maxDevicePixelRatio = 2;
    private int _maxBackingDimension = 1920;
    private int _maxDirectionalLights = 4;
    private int _maxPointLights = 8;
    private int _maxSpotLights = 8;
    private int _maxDirectionalShadowDimension = 2048;
    private int _maxPointShadowDimension = 2048;
    private int _maxSpotShadowDimension = 2048;
    private int _shadowUpdateInterval = 1;
    private int _maxTextureDimension;
    private bool _enableGtao = true;
    private bool _enableBloom = true;

    [Category("Browser WebGPU")]
    [Description("Multiplier applied after the device-pixel-ratio cap when sizing the canvas backing image.")]
    public float ResolutionScale { get => _resolutionScale; set => SetField(ref _resolutionScale, value); }

    [Category("Browser WebGPU")]
    [Description("Maximum browser device-pixel ratio used for the canvas backing image.")]
    public float MaxDevicePixelRatio { get => _maxDevicePixelRatio; set => SetField(ref _maxDevicePixelRatio, value); }

    [Category("Browser WebGPU")]
    [Description("Maximum width or height of the canvas backing image.")]
    public int MaxBackingDimension { get => _maxBackingDimension; set => SetField(ref _maxBackingDimension, value); }

    [Category("Browser WebGPU")]
    [Description("Maximum admitted authored directional lights. Excess lights fail visibly.")]
    public int MaxDirectionalLights { get => _maxDirectionalLights; set => SetField(ref _maxDirectionalLights, value); }

    [Category("Browser WebGPU")]
    [Description("Maximum admitted authored point lights. Excess lights fail visibly.")]
    public int MaxPointLights { get => _maxPointLights; set => SetField(ref _maxPointLights, value); }

    [Category("Browser WebGPU")]
    [Description("Maximum admitted authored spot lights. Excess lights fail visibly.")]
    public int MaxSpotLights { get => _maxSpotLights; set => SetField(ref _maxSpotLights, value); }

    [Category("Browser WebGPU")]
    [Description("Maximum browser directional shadow-map width or height; authored resolution is preserved while the output target scales down.")]
    public int MaxDirectionalShadowDimension { get => _maxDirectionalShadowDimension; set => SetField(ref _maxDirectionalShadowDimension, value); }

    [Category("Browser WebGPU")]
    [Description("Maximum browser point shadow-map width or height; authored resolution is preserved while the output target scales down.")]
    public int MaxPointShadowDimension { get => _maxPointShadowDimension; set => SetField(ref _maxPointShadowDimension, value); }

    [Category("Browser WebGPU")]
    [Description("Maximum browser spot shadow-map width or height; authored resolution is preserved while the output target scales down.")]
    public int MaxSpotShadowDimension { get => _maxSpotShadowDimension; set => SetField(ref _maxSpotShadowDimension, value); }

    [Category("Browser WebGPU")]
    [Description("Maximum rendered frames between unchanged shadow-map updates. Scene or output changes refresh immediately.")]
    public int ShadowUpdateInterval { get => _shadowUpdateInterval; set => SetField(ref _shadowUpdateInterval, value); }

    [Category("Browser WebGPU")]
    [Description("Maximum admitted GPU texture width or height, including render targets. Zero uses the device limit.")]
    public int MaxTextureDimension { get => _maxTextureDimension; set => SetField(ref _maxTextureDimension, value); }

    [Category("Browser WebGPU")]
    [Description("Whether authored GTAO executes and owns its WebGPU targets.")]
    public bool EnableGtao { get => _enableGtao; set => SetField(ref _enableGtao, value); }

    [Category("Browser WebGPU")]
    [Description("Whether authored bloom executes and owns its WebGPU targets.")]
    public bool EnableBloom { get => _enableBloom; set => SetField(ref _enableBloom, value); }

    /// <summary>Returns a deliberate browser quality selection; the default preserves the admitted output.</summary>
    public static BrowserWebGpuQualitySettings CreatePreset(string preset) => preset switch
    {
        "high" => new(),
        "balanced" => new()
        {
            MaxDevicePixelRatio = 1.5f,
            MaxBackingDimension = 1280,
            MaxDirectionalLights = 2,
            MaxPointLights = 4,
            MaxSpotLights = 4,
            MaxDirectionalShadowDimension = 1024,
            MaxPointShadowDimension = 1024,
            MaxSpotShadowDimension = 1024,
            ShadowUpdateInterval = 2,
            MaxTextureDimension = 2048,
        },
        "low" => new()
        {
            ResolutionScale = 0.75f,
            MaxDevicePixelRatio = 1,
            MaxBackingDimension = 1024,
            MaxDirectionalLights = 1,
            MaxPointLights = 2,
            MaxSpotLights = 2,
            MaxDirectionalShadowDimension = 512,
            MaxPointShadowDimension = 512,
            MaxSpotShadowDimension = 512,
            ShadowUpdateInterval = 3,
            MaxTextureDimension = 1024,
            EnableGtao = false,
            EnableBloom = false,
        },
        _ => throw new ArgumentOutOfRangeException(nameof(preset), "Browser quality must be low, balanced, or high."),
    };

    /// <summary>Rejects invalid settings before a browser output allocates resources.</summary>
    public void Validate()
    {
        if (!float.IsFinite(ResolutionScale) || ResolutionScale is < 0.25f or > 1 ||
            !float.IsFinite(MaxDevicePixelRatio) || MaxDevicePixelRatio is < 0.5f or > 4 ||
            MaxBackingDimension is < 128 or > 16384 ||
            MaxDirectionalLights is < 0 or > 4 || MaxPointLights is < 0 or > 8 || MaxSpotLights is < 0 or > 8 ||
            MaxDirectionalShadowDimension is < 128 or > 2048 ||
            MaxPointShadowDimension is < 128 or > 2048 || MaxSpotShadowDimension is < 128 or > 2048 ||
            ShadowUpdateInterval is < 1 or > 120 ||
            MaxTextureDimension is < 0 or > 16384 || MaxTextureDimension is > 0 and < 128 ||
            MaxTextureDimension > 0 && (MaxTextureDimension < MaxBackingDimension ||
                MaxTextureDimension < MaxDirectionalShadowDimension ||
                MaxTextureDimension < MaxPointShadowDimension || MaxTextureDimension < MaxSpotShadowDimension))
            throw new NotSupportedException("WebGPU.Quality.Invalid: browser quality exceeds the shared renderer's admitted limits.");
    }

    /// <summary>Transfers only canvas sizing values to the browser host after engine settings are resolved.</summary>
    public string ToCanvasJson()
    {
        Validate();
        return $"{{\"resolutionScale\":{ResolutionScale.ToString("R", CultureInfo.InvariantCulture)}," +
            $"\"maxDevicePixelRatio\":{MaxDevicePixelRatio.ToString("R", CultureInfo.InvariantCulture)}," +
            $"\"maxBackingDimension\":{MaxBackingDimension}}}";
    }
}
