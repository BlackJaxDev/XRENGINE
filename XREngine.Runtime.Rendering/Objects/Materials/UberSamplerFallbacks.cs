using System.Numerics;

namespace XREngine.Rendering;

/// <summary>
/// Canonical sampler-role fallbacks shared by importers and runtime binders.
/// </summary>
public static class UberSamplerFallbacks
{
    private static readonly UberSamplerFallback[] Values =
    [
        new(EUberSamplerRole.Color, Vector4.One, false),
        new(EUberSamplerRole.Normal, new Vector4(0.5f, 0.5f, 1.0f, 1.0f), true),
        new(EUberSamplerRole.MaskWhite, Vector4.One, true),
        new(EUberSamplerRole.MaskBlack, new Vector4(0.0f, 0.0f, 0.0f, 1.0f), true),
        new(EUberSamplerRole.DataZero, Vector4.Zero, true),
        new(EUberSamplerRole.HeightNeutral, new Vector4(0.5f), true),
        new(EUberSamplerRole.EmissionBlack, new Vector4(0.0f, 0.0f, 0.0f, 1.0f), false),
    ];

    public static UberSamplerFallback Get(EUberSamplerRole role)
        => Values[(int)role];
}
