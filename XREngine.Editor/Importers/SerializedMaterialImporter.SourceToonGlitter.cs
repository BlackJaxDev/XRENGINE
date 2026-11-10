using System.Numerics;
using System.Text.RegularExpressions;
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
        SourceAssetResolver resolver,
        SourceToonShaderVersion? sourceVersion,
        ICollection<MaterialConversionDiagnostic> diagnostics)
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
        bool source10 = sourceVersion == SourceToon10Catalog.Version;
        SetFloat(material, "_GlitterDistanceLOD", source10 ? GetFloat(document, "_GlitterDistanceLOD", 1.0f) : 0.0f);
        if (source10)
        {
            RejectGlitterOption(document, "_GlitterFullCoverage");
            RejectGlitterOption(document, "_GlitterSpriteSheetEnabled");
            if (GetFloat(document, "_GlitterMode") == 1.0f)
                throw new SourceVisualImportException("The source linear glitter mode requires a separate directional response mapping.");
        }

        bool randomSize = document.TryGetPositive("_GlitterRandomSize");
        Vector4 sizeRange = Vector4.Zero;
        if (randomSize && (!TryGetGlitterSizeRange(document, out sizeRange) ||
            !float.IsFinite(sizeRange.X) || !float.IsFinite(sizeRange.Y) ||
            sizeRange.X < 0.0f || sizeRange.Y > 1.0f || sizeRange.X > sizeRange.Y))
            throw new SourceVisualImportException("Active glitter random size requires a valid source size range.");
        bool randomColors = document.TryGetPositive("_GlitterRandomColors");
        if (randomColors && (!document.TryGetVector("_GlitterMinMaxSaturation", out Vector4 saturationRange) ||
            !document.TryGetVector("_GlitterMinMaxBrightness", out Vector4 valueRange) ||
            !IsValidUnitRange(saturationRange) || !IsValidUnitRange(valueRange)))
            throw new SourceVisualImportException("Active glitter random colors require valid saturation and value ranges.");
        RejectGlitterOption(document, "_GlitterRandomRotation");
        RejectGlitterOption(document, "_GlitterTextureRotation");
        bool audioLink = document.TryGetPositive("_GlitterALEnabled");
        if (audioLink && GetFloat(document, "_GlitterALChronoRotationSpeed") != 0.0f)
            throw new SourceVisualImportException("Active glitter AudioLink chrono rotation is not supported.");
        if (audioLink && GetFloat(document, "_GlitterALChronoSparkleSpeed") != 0.0f)
            throw new SourceVisualImportException("Active glitter AudioLink chrono sparkle is not supported.");
        int globalMaskIndex = legacy81 ? 0 : GetInt(document, "_GlitterMaskGlobalMask");
        int globalMaskBlend = GetInt(document, "_GlitterMaskGlobalMaskBlendType", 2);
        if (globalMaskIndex < 0 || globalMaskIndex > 16 || globalMaskBlend < 0 || globalMaskBlend > 7)
            throw new SourceVisualImportException("Active glitter global mask has an unsupported index or blend mode.");
        if (globalMaskIndex > 0 && !document.TryGetPositive("_GlobalMaskTexturesEnable"))
        {
            if (globalMaskBlend != 2)
                throw new SourceVisualImportException("Active glitter global mask requires its source mask module.");
            globalMaskIndex = 0;
        }
        if (globalMaskIndex > 0)
        {
            string maskName = $"_GlobalMaskTexture{(globalMaskIndex - 1) / 4}";
            if (!HasExternalTexture(document, maskName) &&
                !HasWhiteShaderTextureDefault(resolver.Resolve(document.Shader.Guid), maskName))
                throw new SourceVisualImportException("Active glitter global mask texture is missing and has no verified white source default.");
        }
        int themeIndex = GetInt(document, "_GlitterColorThemeIndex");
        if (themeIndex < 0 || themeIndex > 4)
            throw new SourceVisualImportException("Active glitter color theme is outside the supported range.");
        if (document.TryGetVector("_GlitterRandomRotationSpeed", out Vector4 rotationSpeed) &&
            (rotationSpeed.X != 0.0f || rotationSpeed.Y != 0.0f))
        {
            throw new SourceVisualImportException("Active glitter rotation speed is not supported: _GlitterRandomRotationSpeed.");
        }

        if (HasExternalTexture(document, "_GlitterShapeTex") && !HasExternalTexture(document, "_GlitterTexture"))
            throw new SourceVisualImportException("Active glitter shape texture is not supported: _GlitterShapeTex.");
        if (HasExternalTexture(document, "_GlitterColorTex"))
            throw new SourceVisualImportException("Active glitter color texture is not supported: _GlitterColorTex.");

        int uv = GetSupportedGlitterValue(document, "_GlitterUV", 0, 0, 3);
        int mode = GetSupportedGlitterValue(document, "_GlitterMode", 0, 0, 2);
        int shape = GetSupportedGlitterValue(document, "_GlitterShape", 0, 0, 1);
        if (shape != 0)
            throw new SourceVisualImportException("Active glitter shape is not supported: _GlitterShape must be Circle (0).");
        int blendType = GetSupportedGlitterValue(document, "_GlitterBlendType", 0, 0, 1);
        int layers = legacy81 ? 1 : GetSupportedGlitterValue(document, "_GlitterLayers", 2, 1, 8);
        int maskUv = GetSupportedGlitterValue(document, "_GlitterMaskUV", 0, 0, 3);
        int maskChannel = GetSupportedGlitterValue(document, "_GlitterMaskChannel", 0, 0, 3);
        int colorMapUv = HasExternalTexture(document, "_GlitterColorMap")
            ? GetSupportedGlitterValue(document, "_GlitterColorMapUV", 0, 0, 3)
            : 0;

        SetInt(material, "_GlitterUV", uv);
        SetInt(material, "_GlitterMode", mode);
        SetInt(material, "_GlitterShape", shape);
        SetInt(material, "_GlitterBlendType", blendType);
        SetInt(material, "_GlitterLayers", layers);
        SetInt(material, "_SourceGlitterVersion", legacy81 ? 81 : sourceVersion == SourceToon10Catalog.Version ? 100 : 92);
        SetInt(material, "_GlitterMaskChannel", maskChannel);
        SetInt(material, "_GlitterColorMapUV", colorMapUv);
        SetFloat(material, "_GlitterFrequency", GetFloat(document, "_GlitterFrequency", legacy81 ? 100.0f : 300.0f));
        SetFloat(material, "_GlitterSize", GetFloat(document, "_GlitterSize", legacy81 ? 0.25f : 0.3f));
        SetInt(material, "_GlitterRandomSize", randomSize ? 1 : 0);
        SetVector2(material, "_GlitterMinMaxSize", new Vector2(sizeRange.X, sizeRange.Y));
        SetInt(material, "_GlitterRandomColors", randomColors ? 1 : 0);
        SetVector2(material, "_GlitterRandomSaturation", randomColors
            ? GetSourceAudioRange(document, "_GlitterMinMaxSaturation") : Vector2.Zero);
        SetVector2(material, "_GlitterRandomValue", randomColors
            ? GetSourceAudioRange(document, "_GlitterMinMaxBrightness") : Vector2.Zero);
        SetVector2(material, "_GlitterGlobalMask", new Vector2(globalMaskIndex, globalMaskBlend));
        SetInt(material, "_GlitterColorThemeIndex", themeIndex);
        SetVector4(material, "_GlitterHueControl", new Vector4(
            document.TryGetPositive("_GlitterHueShiftEnabled") ? 1.0f : 0.0f,
            GetFloat(document, "_GlitterHueShift"),
            GetFloat(document, "_GlitterHueShiftSpeed"),
            GetFloat(document, "_GlitterHueShiftColorSpace")));
        SetVector4(material, "_GlitterAudioBands", new Vector4(
            audioLink ? 1.0f : 0.0f,
            GetSourceAudioBand(document, "_GlitterALSizeAddBand", audioLink),
            GetSourceAudioBand(document, "_GlitterALMaxBrightnessBand", audioLink),
            GetSourceAudioBand(document, "_GlitterALAlphaAddBand", audioLink)));
        SetVector2(material, "_GlitterAudioSizeRange", GetSourceAudioRange(document, "_GlitterALSizeAdd"));
        SetVector2(material, "_GlitterAudioBrightnessRange", GetSourceAudioRange(document, "_GlitterALMaxBrightnessAdd"));
        SetVector2(material, "_GlitterAudioMaskRange", GetSourceAudioRange(document, "_GlitterALAlphaAdd"));
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
        SetVector4(material, "_GlitterColorMap_ST", new Vector4(1.0f, 1.0f, 0.0f, 0.0f));
        ApplyTextureTransform(material, document, "_GlitterMask", "_GlitterMask");
        ApplyTextureTransform(material, document, "_GlitterTexture", "_GlitterTexture");
        ApplyTextureTransform(material, document, "_GlitterColorMap", "_GlitterColorMap");
        SetInt(material, "_GlitterMaskUV", maskUv);
        SetVector2(material, "_GlitterMaskPan", document.TryGetVector("_GlitterMaskPan", out Vector4 maskPan)
            ? new Vector2(maskPan.X, maskPan.Y)
            : Vector2.Zero);
        SetVector2(material, "_GlitterColorMapPan", document.TryGetVector("_GlitterColorMapPan", out Vector4 colorMapPan)
            ? new Vector2(colorMapPan.X, colorMapPan.Y)
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

    private static bool TryGetGlitterSizeRange(SerializedMaterialDocument document, out Vector4 range)
    {
        if (document.TryGetVector("_GlitterMinMaxSize", out range))
            return true;

        range = Vector4.Zero;
        bool found = false;
        foreach ((string name, Vector4 value) in document.Vectors)
        {
            if (!name.StartsWith("_GlitterMinMaxSize_", StringComparison.Ordinal))
                continue;
            if (found)
                throw new SourceVisualImportException("Active glitter random size has more than one optimized size range.");
            range = value;
            found = true;
        }

        return found;
    }

    private static Vector2 GetSourceAudioRange(SerializedMaterialDocument document, string name)
        => document.TryGetVector(name, out Vector4 range)
            ? new Vector2(range.X, range.Y)
            : Vector2.Zero;

    private static bool IsValidUnitRange(Vector4 range)
        => float.IsFinite(range.X) && float.IsFinite(range.Y) &&
           range.X >= 0.0f && range.Y <= 1.0f && range.X <= range.Y;

    private static bool HasWhiteShaderTextureDefault(string? shaderPath, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(shaderPath) ||
            !shaderPath.EndsWith(".shader", StringComparison.OrdinalIgnoreCase))
            return false;

        try
        {
            string pattern = $@"^\s*(?:\[[^\]\r\n]+\]\s*)*{Regex.Escape(propertyName)}\s*\(\s*""(?:[^""\\]|\\.)*""\s*,\s*2D\s*\)\s*=\s*""white""\s*\{{";
            foreach (string line in File.ReadLines(shaderPath))
            {
                if (Regex.IsMatch(line, pattern, RegexOptions.CultureInvariant))
                    return true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        return false;
    }

    private static int GetSourceAudioBand(SerializedMaterialDocument document, string property, bool enabled)
    {
        float value = GetFloat(document, property);
        if (enabled && (!float.IsFinite(value) || value < 0.0f || value > 3.0f ||
            value != MathF.Truncate(value)))
            throw new SourceVisualImportException($"Active AudioLink band '{property}' is outside the supported 0..3 range.");
        return (int)value;
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
