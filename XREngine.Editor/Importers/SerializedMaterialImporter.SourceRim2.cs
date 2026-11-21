using System.Numerics;
using XREngine.Rendering;

namespace XREngine.Scene.Importers;

public static partial class SerializedMaterialImporter
{
    private static void BindSourceRim2Parameters(
        XRMaterial material,
        SerializedMaterialDocument document,
        SourceAssetResolver resolver)
    {
        if (!document.TryGetPositive("_EnableRim2Lighting"))
            return;

        string? shaderPath = resolver.Resolve(document.Shader.Guid);
        string? unsupported = GetUnsupportedSourceRim2Option(document, shaderPath);
        if (unsupported is not null)
            throw new SourceVisualImportException($"Active source Rim2 option '{unsupported}' on '{document.Name}' has no exact Uber conversion.");

        int globalMask = GetInt(document, "_Rim2GlobalMask");
        if (!document.TryGetPositive("_GlobalMaskTexturesEnable"))
            globalMask = 0;

        SetVector4(material, "_SourceRim2Settings", new Vector4(
            1.0f,
            GetFloat(document, "_Rim2LightingInvert"),
            GetFloat(document, "_Rim2Power", 1.0f),
            GetFloat(document, "_Is_NormalMapToRim2Light", 1.0f)));
        SetVector4(material, "_SourceRim2ColorSettings", new Vector4(
            GetFloat(document, "_Rim2BaseColorMix"),
            GetFloat(document, "_Rim2Brightness", 1.0f),
            GetInt(document, "_RimPoi2BlendMode"),
            globalMask));
        SetVector4(material, "_SourceRim2Strengths", new Vector4(
            GetFloat(document, "_Rim2BlendStrength", 1.0f),
            GetFloat(document, "_Rim2Strength"),
            GetInt(document, "_Rim2GlobalMaskBlendType", 2),
            GetInt(document, "_Rim2LightColorThemeIndex")));
        SetVector4(material, "_SourceRim2Tint",
            document.TryGetVector("_Rim2LightColor", out Vector4 tint) ? tint : Vector4.One);

        // The source branch changes the base color before lighting. The legacy
        // second-rim pass must not add the same contribution after lighting.
        SetVector4(material, "_Rim2Params", new Vector4(
            GetFloat(document, "_Rim2Width", 0.8f),
            GetFloat(document, "_Rim2Sharpness", 0.25f),
            GetFloat(document, "_Rim2HideInShadow"),
            0.0f));
    }

    private static string? GetUnsupportedSourceRim2Option(
        SerializedMaterialDocument document,
        string? shaderPath)
    {
        if (GetInt(document, "_Rim2Style") != 0)
            return "_Rim2Style";
        foreach (string name in new[] { "_Rim2Tex", "_Rim2Mask" })
        {
            if (HasExternalTexture(document, name))
                return name;
            if (!HasWhiteShaderTextureDefault(shaderPath, name))
                return name + " default";
            if (document.Textures.TryGetValue(name, out SerializedTexturePropertyDocument? texture) &&
                texture is not null && (texture.Scale != Vector2.One || texture.Offset != Vector2.Zero))
                return name + " transform";
        }
        foreach (string name in new[]
        {
            "_Rim2BiasIntensity", "_Rim2ShadowToggle",
            "_Rim2ApplyAlpha", "_Rim2ApplyGlobalMaskIndex", "_Rim2MaskInvert"
        })
            if (document.TryGetPositive(name))
                return name;
        // The checks above allow only the white default for both textures, so their UV set,
        // channel, and pan do not change the sampled value.
        if (document.TryGetPositive("_Rim2HueShiftEnabled") && !IsIdentitySourceHueShift(document, "_Rim2"))
            return "_Rim2HueShiftEnabled";
        if (document.TryGetPositive("_EnableAudioLink"))
        {
            foreach (string name in new[]
            {
                "_AudioLinkRim2WidthAdd", "_AudioLinkRim2EmissionAdd", "_AudioLinkRim2BrightnessAdd"
            })
            {
                if (document.TryGetVector(name, out Vector4 range) && range != Vector4.Zero)
                    return name;
            }
        }
        int globalMask = GetInt(document, "_Rim2GlobalMask");
        int globalBlend = GetInt(document, "_Rim2GlobalMaskBlendType", 2);
        bool globalMasksEnabled = document.TryGetPositive("_GlobalMaskTexturesEnable");
        if (globalMask < 0 || globalMask > 16 || globalMask > 0 && globalBlend != 2 ||
            globalMask > 0 && globalMasksEnabled &&
            !HasExternalTexture(document, $"_GlobalMaskTexture{(globalMask - 1) / 4}") &&
            !HasWhiteShaderTextureDefault(shaderPath, $"_GlobalMaskTexture{(globalMask - 1) / 4}"))
            return "_Rim2GlobalMask";
        if (GetInt(document, "_Rim2LightColorThemeIndex") is < 0 or > 4)
            return "_Rim2LightColorThemeIndex";
        if (GetInt(document, "_RimPoi2BlendMode") is < 0 or > 4)
            return "_RimPoi2BlendMode";
        return null;
    }

    /// <summary>
    /// True when an enabled source hue shift with the given prefix leaves the color unchanged:
    /// shift mode (<c>{prefix}HueSelectOrShift</c> = 1) with a zero shift and a zero speed.
    /// </summary>
    private static bool IsIdentitySourceHueShift(SerializedMaterialDocument document, string prefix)
        => GetInt(document, prefix + "HueSelectOrShift", 1) == 1 &&
           GetFloat(document, prefix + "HueShift") == 0.0f &&
           GetFloat(document, prefix + "HueShiftSpeed") == 0.0f;
}
