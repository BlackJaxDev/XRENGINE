using System.Numerics;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Scene.Importers.SourceToon;

namespace XREngine.Scene.Importers;

public static partial class SerializedMaterialImporter
{
    /// <summary>Bind supported source glitter values and reject active unsupported options.</summary>
    private static void BindSourceToonGlitter(
        XRMaterial material,
        SerializedMaterialDocument document,
        SourceToonShaderVersion? sourceVersion)
    {
        bool enabled = SourceToonFeatureStateResolver.IsEnabled(
            document,
            HasExternalTexture(document, "_GlitterMask"),
            ["_GlitterEnable"],
            ["_SUNDISK_SIMPLE"]);
        SetInt(material, "_SourceGlitter", enabled ? 1 : 0);
        if (!enabled)
            return;

        bool legacy81 = sourceVersion == SourceToon81Catalog.Version;
        if (legacy81)
            VerifySource81Glitter(document);

        RejectGlitterOption(document, "_GlitterRandomSize");
        RejectGlitterOption(document, "_GlitterRandomColors");
        RejectGlitterOption(document, "_GlitterRandomRotation");
        RejectGlitterOption(document, "_GlitterTextureRotation");
        RejectGlitterOption(document, "_GlitterHueShiftEnabled");
        RejectGlitterOption(document, "_GlitterALEnabled");
        if (!legacy81)
            RejectGlitterOption(document, "_GlitterMaskGlobalMask");
        RejectGlitterOption(document, "_GlitterColorThemeIndex");
        if (document.TryGetVector("_GlitterRandomRotationSpeed", out Vector4 rotationSpeed) &&
            (rotationSpeed.X != 0.0f || rotationSpeed.Y != 0.0f))
        {
            throw new SourceVisualImportException("Active glitter rotation speed is not supported: _GlitterRandomRotationSpeed.");
        }

        if (HasExternalTexture(document, "_GlitterShapeTex") && !HasExternalTexture(document, "_GlitterTexture"))
            throw new SourceVisualImportException("Active glitter shape texture is not supported: _GlitterShapeTex.");
        if (HasExternalTexture(document, "_GlitterColorMap", "_GlitterColorTex"))
            throw new SourceVisualImportException("Active glitter color texture is not supported: _GlitterColorMap.");

        int uv = GetSupportedGlitterValue(document, "_GlitterUV", 0, 0, 3);
        int mode = GetSupportedGlitterValue(document, "_GlitterMode", 0, 0, 2);
        int shape = GetSupportedGlitterValue(document, "_GlitterShape", 0, 0, 1);
        if (shape != 0)
            throw new SourceVisualImportException("Active glitter shape is not supported: _GlitterShape must be Circle (0).");
        int blendType = GetSupportedGlitterValue(document, "_GlitterBlendType", 0, 0, 1);
        int layers = legacy81 ? 1 : GetSupportedGlitterValue(document, "_GlitterLayers", 2, 1, 8);
        int maskUv = GetSupportedGlitterValue(document, "_GlitterMaskUV", 0, 0, 3);
        int maskChannel = GetSupportedGlitterValue(document, "_GlitterMaskChannel", 0, 0, 3);

        SetInt(material, "_GlitterUV", uv);
        SetInt(material, "_GlitterMode", mode);
        SetInt(material, "_GlitterShape", shape);
        SetInt(material, "_GlitterBlendType", blendType);
        SetInt(material, "_GlitterLayers", layers);
        SetInt(material, "_SourceGlitterVersion", legacy81 ? 81 : 92);
        SetInt(material, "_GlitterMaskChannel", maskChannel);
        SetFloat(material, "_GlitterFrequency", GetFloat(document, "_GlitterFrequency", legacy81 ? 100.0f : 300.0f));
        SetFloat(material, "_GlitterSize", GetFloat(document, "_GlitterSize", legacy81 ? 0.25f : 0.3f));
        SetFloat(material, "_GlitterSpeed", GetFloat(document, "_GlitterSpeed", 10.0f));
        SetFloat(material, "_GlitterBrightness", GetFloat(document, "_GlitterBrightness", 3.0f));
        SetFloat(material, "_GlitterAngleRange", GetFloat(document, "_GlitterAngleRange", legacy81 ? 7.0f : 90.0f));
        SetFloat(material, "_GlitterContrast", GetFloat(document, "_GlitterContrast", 300.0f));
        SetFloat(material, "_GlitterBias", GetFloat(document, "_GlitterBias", 0.8f));
        SetFloat(material, "_GlitterMinBrightness", GetFloat(document, "_GlitterMinBrightness"));
        SetFloat(material, "_GlitterCenterSize", GetFloat(document, "_GlitterCenterSize", 0.08f));
        SetFloat(material, "_GlitterRandomLocation", GetFloat(document, "_GlitterRandomLocation", 1.0f));
        SetFloat(material, "_GlitterUseNormals", GetFloat(document, "_GlitterUseNormals"));
        SetFloat(material, "_GlitterUseSurfaceColor", GetFloat(document, "_GlitterUseSurfaceColor"));
        SetFloat(material, "_GlitterHideInShadow", GetFloat(document, "_GlitterHideInShadow"));
        SetFloat(material, "_GlitterScaleWithLighting", GetFloat(document, "_GlitterScaleWithLighting"));
        SetFloat(material, "_GlitterMaskInvert", GetFloat(document, "_GlitterMaskInvert"));
        SetVector2(material, "_GlitterUVPanning", document.TryGetVector("_GlitterUVPanning", out Vector4 panning)
            ? new Vector2(panning.X, panning.Y)
            : Vector2.Zero);
        SetVector4(material, "_GlitterColor", document.TryGetVector("_GlitterColor", out Vector4 color)
            ? color
            : Vector4.One);
        SetVector4(material, "_GlitterMask_ST", new Vector4(1.0f, 1.0f, 0.0f, 0.0f));
        SetVector4(material, "_GlitterTexture_ST", new Vector4(1.0f, 1.0f, 0.0f, 0.0f));
        ApplyTextureTransform(material, document, "_GlitterMask", "_GlitterMask");
        ApplyTextureTransform(material, document, "_GlitterTexture", "_GlitterTexture");
        SetInt(material, "_GlitterMaskUV", maskUv);
        SetVector2(material, "_GlitterMaskPan", document.TryGetVector("_GlitterMaskPan", out Vector4 maskPan)
            ? new Vector2(maskPan.X, maskPan.Y)
            : Vector2.Zero);
    }

    private static void VerifySource81Glitter(SerializedMaterialDocument document)
    {
        if (HasExternalTexture(document, "_GlitterMask") || HasExternalTexture(document, "_GlitterTexture"))
            throw new SourceVisualImportException("The verified Poiyomi 8.1.167 shader omits external glitter mask and shape texture sampling.");
        (string Name, float Value)[] lockedValues =
        [
            ("_GlitterJitter", 1.0f), ("_GlitterFrequency", 100.0f),
            ("_GlitterSize", 0.25f), ("_GlitterSpeed", 10.0f),
            ("_GlitterAngleRange", 7.0f), ("_GlitterContrast", 300.0f),
            ("_GlitterBias", 0.8f), ("_GlitterBrightness", 3.0f),
            ("_GlitterMaskGlobalMask", 2.0f),
            ("_GlitterMaskGlobalMaskBlendType", 2.0f),
            ("_GlitterUV", 0.0f), ("_GlitterMode", 0.0f),
            ("_GlitterShape", 0.0f), ("_GlitterBlendType", 0.0f),
            ("_GlitterMaskChannel", 0.0f),
            ("_GlobalMaskTexturesEnable", 1.0f),
            ("_GlobalMaskTexture0Split", 1.0f),
            ("_GlobalMaskTexture0UV", 0.0f),
        ];
        foreach ((string name, float expected) in lockedValues)
        {
            if (document.TryGetFloat(name, out float value) && MathF.Abs(value - expected) > 0.00001f)
                throw new SourceVisualImportException($"The verified Poiyomi 8.1.167 shader fixes {name} to {expected}; this material value is not supported.");
        }
        if (!HasExternalTexture(document, "_GlobalMaskTexture0"))
            throw new SourceVisualImportException("The verified Poiyomi 8.1.167 glitter requires its global mask texture.");
        if (document.TryGetVector("_GlobalMaskTexture0SplitTilingOffset_G", out Vector4 scaleOffset) &&
            scaleOffset != new Vector4(1.0f, 1.0f, 0.0f, 0.0f))
            throw new SourceVisualImportException("The verified Poiyomi 8.1.167 glitter requires the original global mask green transform.");
        if (document.TryGetVector("_GlobalMaskTexture0SplitPan_G", out Vector4 splitPan) &&
            splitPan != Vector4.Zero)
            throw new SourceVisualImportException("The verified Poiyomi 8.1.167 glitter requires the original global mask green pan.");
    }

    private static int GetSupportedGlitterValue(
        SerializedMaterialDocument document,
        string property,
        int fallback,
        int minimum,
        int maximum)
    {
        if (!document.TryGetFloat(property, out float value))
            return fallback;
        if (!float.IsFinite(value) || value < minimum || value > maximum || value != MathF.Truncate(value))
            throw new SourceVisualImportException($"Active glitter value is not supported: {property} must be an integer from {minimum} to {maximum}.");
        return (int)value;
    }

    private static void RejectGlitterOption(SerializedMaterialDocument document, string property)
    {
        if (document.TryGetFloat(property, out float value) && value != 0.0f)
            throw new SourceVisualImportException($"Active glitter option is not supported: {property}.");
    }
}
