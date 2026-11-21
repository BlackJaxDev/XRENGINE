using System.Numerics;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Scene.Importers.SourceToon;

namespace XREngine.Scene.Importers;

public static partial class SerializedMaterialImporter
{
    /// <summary>Source blend-factor index for the source shader's "SrcAlpha" grab blend. The source shader reads it as one minus the grab blend factor.</summary>
    private const float SourceGrabDefaultSrcBlend = 5.0f;

    /// <summary>Source blend-factor index for the source shader's "OneMinusSrcAlpha" grab blend. The source shader reads it as the grab blend factor.</summary>
    private const float SourceGrabDefaultDstBlend = 10.0f;

    /// <summary>Highest blend-factor index in the source shader blend factor table.</summary>
    private const float SourceGrabMaxBlendFactor = 10.0f;

    /// <summary>
    /// Selects the late-pass contract for a source toon material that renders after the opaque scene.
    /// A material with an active GrabPass gets the scene-color contract. A material without one gets the
    /// ordered contract. Each source option that a contract cannot reproduce is added to
    /// <see cref="SourceOrderedSurfacePlan.DegradedFeatures"/>. The importer then drops only that option,
    /// so one unsupported option never stops the material conversion.
    /// </summary>
    private static SourceOrderedSurfacePlan ClassifySourceOrderedSurface(
        SerializedMaterialDocument document,
        string? shaderPath)
    {
        if (SourceToonRenderStateConverter.IsHornsSoftOutlineSource(document))
            return default;

        List<string> degraded = [];
        bool hasGrab = ResolveSourceGrabPass(document, shaderPath, degraded);
        int sourceQueue = document.CustomRenderQueue;
        if (sourceQueue <= 2500)
        {
            if (hasGrab)
                degraded.Add("The source GrabPass needs a render queue above 2500. The material renders without its scene-color sample.");
            return new(default, ToDegradedList(degraded));
        }

        if (document.TryGetPositive("_EnableOutlines") || document.TryGetPositive("_EnableSoftOutlines") ||
            document.TryGetPositive("_OutlinesEnabled") ||
            document.TryGetPositive("_UseOutline") || document.TryGetPositive("_RenderingEarlyZEnabled") ||
            document.TryGetPositive("_AlphaToCoverage"))
        {
            degraded.Add(hasGrab
                ? "The source surface has an outline, EarlyZ, or alpha-to-coverage pass that the ordered late lane cannot replay. The material renders as sorted transparency without its scene-color sample."
                : "The source surface has an outline, EarlyZ, or alpha-to-coverage pass that the ordered late lane cannot replay. The material renders as sorted transparency.");
            return new(default, ToDegradedList(degraded));
        }

        if (!hasGrab)
            return new(EAdvancedSourceSurfaceContract.OrderedToonV1, ToDegradedList(degraded));

        AddSourceGrabDegradedFeatures(document, degraded);
        return new(EAdvancedSourceSurfaceContract.SceneColorToonV1, ToDegradedList(degraded));
    }

    /// <summary>
    /// Decides if the source material has an active GrabPass. The decision uses the
    /// <c>_EnableGrabpass</c> toggle and the GrabPass declaration in the source shader file.
    /// </summary>
    private static bool ResolveSourceGrabPass(
        SerializedMaterialDocument document,
        string? shaderPath,
        List<string> degraded)
    {
        if (!document.TryGetPositive("_EnableGrabpass"))
            return false;

        if (string.IsNullOrWhiteSpace(shaderPath) || !File.Exists(shaderPath))
        {
            degraded.Add("The source GrabPass toggle is on, but the source shader file is not available to confirm a GrabPass. The material renders without its scene-color sample.");
            return false;
        }

        switch (ReadSourceGrabShaderShape(shaderPath))
        {
            case SourceGrabShaderShape.Absent:
                return false;
            case SourceGrabShaderShape.Unknown:
                degraded.Add("The source shader declares a GrabPass without the named _PoiGrab2 contract. The importer applies the source toon scene-color mapping without shader verification.");
                return true;
            default:
                return true;
        }
    }

    /// <summary>
    /// Adds a degraded-feature entry for each GrabPass option that the Uber scene-color lane does not
    /// reproduce. Refraction, chromatic aberration, all eleven source blend factors, tint, brightness,
    /// and alpha-based blending are supported, so they add no entry.
    /// </summary>
    private static void AddSourceGrabDegradedFeatures(SerializedMaterialDocument document, List<string> degraded)
    {
        if (document.TryGetPositive("_EnableBlur"))
            degraded.Add("Source GrabPass blur is not supported. The scene-color sample is not blurred.");
        if (document.TryGetPositive("_GrabPassHueShiftToggle"))
            degraded.Add("Source GrabPass hue shift is not supported. The scene-color sample keeps its hue.");
        if (document.TryGetPositive("_GrabPixelationEnabled"))
            degraded.Add("Source GrabPass pixelation is not supported. The scene-color sample is not pixelated.");
        if (document.TryGetPositive("_GrabRampEnabled"))
            degraded.Add("Source GrabPass color ramp is not supported. The scene-color sample keeps its colors.");
        if (GetSourceFloat(document, "_GrabSaturation", 1.0f) != 1.0f || GetSourceFloat(document, "_GrabVibrance") != 0.0f)
            degraded.Add("Source GrabPass saturation and vibrance are not supported. The scene-color sample keeps its saturation.");
        if (HasSourceTexture(document, "_GrabPassBlendMap") || GetSourceFloat(document, "_GrabPassMaskType") != 0.0f)
            degraded.Add("Source GrabPass mask textures and vertex-color masks are not supported. Refraction, color, and alpha masks use full strength.");
        if (GetSourceFloat(document, "_GrabPassBlendGlobalMask") != 0.0f ||
            GetSourceFloat(document, "_GrabRefractionGlobalMask") != 0.0f ||
            GetSourceFloat(document, "_GrabColorGlobalMask") != 0.0f ||
            GetSourceFloat(document, "_GrabAlphaGlobalMask") != 0.0f)
            degraded.Add("Source GrabPass global masks are not supported. Refraction, color, and alpha masks use full strength.");
        if (document.TryGetPositive("_RefractionEnabled") && GetSourceFloat(document, "_RefractionCABlurHelp") != 0.0f)
            degraded.Add("Source refraction chromatic-aberration blur is not supported. Each color channel uses one sample.");
        if (!IsSourceGrabBlendFactor(GetSourceFloat(document, "_GrabSrcBlend", SourceGrabDefaultSrcBlend)) ||
            !IsSourceGrabBlendFactor(GetSourceFloat(document, "_GrabDstBlend", SourceGrabDefaultDstBlend)))
            degraded.Add("The source GrabPass blend factors are outside the source factor table. The importer uses SrcAlpha and OneMinusSrcAlpha.");
        if (GetSourceFloat(document, "_AlphaFresnelGlobalMask") != 0.0f ||
            document.TryGetPositive("_AlphaDistanceFade") ||
            document.TryGetPositive("_AlphaAngular") ||
            document.TryGetPositive("_AlphaAudioLinkEnabled"))
            degraded.Add("Source alpha masks, distance fade, angular alpha, and AudioLink alpha are not applied to the GrabPass blend.");
        if (!document.TryGetVector("_GrabpassColor", out Vector4 grabColor) || !IsFinite(grabColor) ||
            !float.IsFinite(GetSourceFloat(document, "_GrabPassBrightness", 1.0f)) ||
            !float.IsFinite(GetSourceFloat(document, "_GrabPassAddBrightness")) ||
            !IsUnitInterval(GetSourceFloat(document, "_GrabPassBlendFactor", 1.0f)) ||
            !float.IsFinite(GetSourceFloat(document, "_GrabPassUseAlpha", 1.0f)) ||
            !float.IsFinite(GetSourceFloat(document, "_RefractionIndex", 1.333333f)) ||
            !float.IsFinite(GetSourceFloat(document, "_RefractionChromaticAberration")) ||
            !float.IsFinite(GetSourceFloat(document, "_RefractionFresnelPower")) ||
            !float.IsFinite(GetSourceFloat(document, "_AlphaMod")) ||
            GetSourceFloat(document, "_AlphaFresnel") is not (0.0f or 1.0f) ||
            !IsUnitInterval(GetSourceFloat(document, "_AlphaFresnelAlpha")) ||
            !IsUnitInterval(GetSourceFloat(document, "_AlphaFresnelSharpness", 0.5f)) ||
            !IsUnitInterval(GetSourceFloat(document, "_AlphaFresnelWidth", 0.5f)) ||
            GetSourceFloat(document, "_AlphaFresnelInvert") is not (0.0f or 1.0f))
            degraded.Add("Some source GrabPass, refraction, or alpha controls are missing, non-finite, or out of range. The importer replaces them with the source shader defaults or clamps them.");
        if (GetSourceFloat(document, "_LightingMode") != 5.0f)
            degraded.Add("The GrabPass lighting divisor is verified only for the source Flat lighting mode. Other lighting modes can make the scene-color sample brighter or darker than the source.");
        if (GetSourceFloat(document, "_Mode") != 0.0f ||
            (GetSourceFloat(document, "_AlphaForceOpaque") != 1.0f && GetSourceFloat(document, "_Cutoff") != 0.0f))
            degraded.Add("The source GrabPass surface is not opaque with ForceOpaque or a zero cutoff. Alpha clipping can differ from the source.");
    }

    /// <summary>
    /// Writes the selected late-pass contract into the Uber material. For the scene-color contract,
    /// this enables the <c>source-scene-color</c> Uber feature and copies the GrabPass tint, brightness,
    /// blend, alpha, and refraction controls into the Uber uniforms. Each degraded source option becomes
    /// a conversion warning.
    /// </summary>
    private static void BindSourceOrderedSurface(
        XRMaterial material,
        SerializedMaterialDocument document,
        SourceOrderedSurfacePlan plan,
        ICollection<MaterialConversionDiagnostic> diagnostics,
        ICollection<string> warnings)
    {
        if (plan.DegradedFeatures is { Count: > 0 } degradedFeatures)
        {
            foreach (string degradedFeature in degradedFeatures)
            {
                MaterialConversionDiagnostic degradedDiagnostic = new(
                    MaterialConversionDiagnosticCodes.SourceValueNotPreserved,
                    MaterialConversionDiagnosticSeverity.Warning,
                    degradedFeature,
                    "_EnableGrabpass");
                diagnostics.Add(degradedDiagnostic);
                warnings.Add(degradedDiagnostic.ToString());
            }
        }

        if (!plan.HasSceneColor)
            return;

        MaterialConversionDiagnostic lightingDiagnostic = new(
            MaterialConversionDiagnosticCodes.IntentionalNativeDifference,
            MaterialConversionDiagnosticSeverity.Warning,
            "The source GrabPass lighting divisor uses the primary directional light. Source vertex lights and additional light responses need live validation for this material.",
            "_EnableGrabpass");
        diagnostics.Add(lightingDiagnostic);
        warnings.Add(lightingDiagnostic.ToString());

        material.SetUberFeatureEnabled("source-scene-color", true);
        SetFloat(material, "_SourceSceneColorMode", 1.0f);
        Vector4 grabColor = document.TryGetVector("_GrabpassColor", out Vector4 sourceGrabColor) && IsFinite(sourceGrabColor)
            ? sourceGrabColor
            : Vector4.One;
        SetVector4(material, "_SourceGrabColor", grabColor);
        SetFloat(material, "_SourceGrabBrightness", FiniteOr(GetSourceFloat(document, "_GrabPassBrightness", 1.0f), 1.0f));
        SetFloat(material, "_SourceGrabAddBrightness", FiniteOr(GetSourceFloat(document, "_GrabPassAddBrightness"), 0.0f));
        SetFloat(material, "_SourceGrabBlendFactor", Saturate(FiniteOr(GetSourceFloat(document, "_GrabPassBlendFactor", 1.0f), 1.0f)));
        SetFloat(material, "_SourceGrabUseAlpha", FiniteOr(GetSourceFloat(document, "_GrabPassUseAlpha", 1.0f), 1.0f));
        float srcBlend = GetSourceFloat(document, "_GrabSrcBlend", SourceGrabDefaultSrcBlend);
        float dstBlend = GetSourceFloat(document, "_GrabDstBlend", SourceGrabDefaultDstBlend);
        bool validBlend = IsSourceGrabBlendFactor(srcBlend) && IsSourceGrabBlendFactor(dstBlend);
        SetFloat(material, "_SourceGrabSrcBlend", validBlend ? srcBlend : SourceGrabDefaultSrcBlend);
        SetFloat(material, "_SourceGrabDstBlend", validBlend ? dstBlend : SourceGrabDefaultDstBlend);
        BindSourceRefraction(material, document);
        SetFloat(material, "_AlphaFresnel", GetSourceFloat(document, "_AlphaFresnel") == 1.0f ? 1.0f : 0.0f);
        SetFloat(material, "_AlphaFresnelAlpha", Saturate(FiniteOr(GetSourceFloat(document, "_AlphaFresnelAlpha"), 0.0f)));
        SetFloat(material, "_AlphaFresnelSharpness", Saturate(FiniteOr(GetSourceFloat(document, "_AlphaFresnelSharpness", 0.5f), 0.5f)));
        SetFloat(material, "_AlphaFresnelWidth", Saturate(FiniteOr(GetSourceFloat(document, "_AlphaFresnelWidth", 0.5f), 0.5f)));
        SetFloat(material, "_AlphaFresnelInvert", GetSourceFloat(document, "_AlphaFresnelInvert") == 1.0f ? 1.0f : 0.0f);
    }

    /// <summary>
    /// Copies the source toon refraction controls into the Uber scene-color lane. The shader offsets the
    /// scene-color sample by the view-space surface normal, scaled by <c>(index - 1)</c>, the projection
    /// scale, and the inverse camera distance. Chromatic aberration moves the green and blue samples
    /// closer to and farther from the pixel. Source ranges: index 0 to 3, aberration 0 to 1,
    /// fresnel power 0 to 10.
    /// </summary>
    private static void BindSourceRefraction(XRMaterial material, SerializedMaterialDocument document)
    {
        bool enabled = document.TryGetPositive("_RefractionEnabled");
        SetFloat(material, "_SourceRefractionEnabled", enabled ? 1.0f : 0.0f);
        SetFloat(material, "_SourceRefractionIndex",
            Math.Clamp(FiniteOr(GetSourceFloat(document, "_RefractionIndex", 1.333333f), 1.333333f), 0.0f, 3.0f));
        SetFloat(material, "_SourceRefractionChromaticAberration",
            Saturate(FiniteOr(GetSourceFloat(document, "_RefractionChromaticAberration"), 0.0f)));
        SetFloat(material, "_SourceRefractionFresnelPower",
            Math.Clamp(FiniteOr(GetSourceFloat(document, "_RefractionFresnelPower"), 0.0f), 0.0f, 10.0f));
    }

    private static IReadOnlyList<string>? ToDegradedList(List<string> degraded)
        => degraded.Count == 0 ? null : degraded;

    private static bool IsSourceGrabBlendFactor(float value)
        => float.IsFinite(value) && value >= 0.0f && value <= SourceGrabMaxBlendFactor && value == MathF.Round(value);

    private static float FiniteOr(float value, float fallback)
        => float.IsFinite(value) ? value : fallback;

    private static float Saturate(float value)
        => Math.Clamp(value, 0.0f, 1.0f);

    private static SourceGrabShaderShape ReadSourceGrabShaderShape(string shaderPath)
    {
        try
        {
            bool hasNamedGrab = false;
            bool hasGrabFunction = false;
            bool hasGrabDeclaration = false;
            bool insideGrabDeclaration = false;
            bool hasExternalGrabSource = false;
            foreach (string line in File.ReadLines(shaderPath))
            {
                string trimmed = line.Trim();
                hasGrabFunction |= line.Contains("applyGrabEffects(", StringComparison.Ordinal);
                hasExternalGrabSource |= trimmed.StartsWith("UsePass", StringComparison.Ordinal) ||
                    trimmed.StartsWith("Fallback", StringComparison.Ordinal) ||
                    ((trimmed.StartsWith("#define", StringComparison.Ordinal) ||
                      trimmed.StartsWith("#pragma", StringComparison.Ordinal)) &&
                     trimmed.Contains("POI_GRABPASS", StringComparison.Ordinal));
                if (trimmed.StartsWith("GrabPass", StringComparison.Ordinal))
                {
                    hasGrabDeclaration = true;
                    insideGrabDeclaration = true;
                }
                if (insideGrabDeclaration && trimmed.Contains("\"_PoiGrab2\"", StringComparison.Ordinal))
                    hasNamedGrab = true;
                if (insideGrabDeclaration && trimmed == "}")
                    insideGrabDeclaration = false;
            }
            if (hasNamedGrab && hasGrabFunction && hasGrabDeclaration)
                return SourceGrabShaderShape.Named;
            return hasNamedGrab || hasGrabFunction || hasGrabDeclaration || hasExternalGrabSource
                ? SourceGrabShaderShape.Unknown
                : SourceGrabShaderShape.Absent;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return SourceGrabShaderShape.Unknown;
        }
    }

    private static bool HasSourceTexture(SerializedMaterialDocument document, string name)
        => document.Textures.TryGetValue(name, out SerializedTexturePropertyDocument? texture) &&
           (texture.TextureReference.HasExternalGuid || texture.TextureReference.FileId != 0);

    private static float GetSourceFloat(SerializedMaterialDocument document, string name, float fallback = 0.0f)
        => document.TryGetFloat(name, out float value) ? value : fallback;

    private static bool IsFinite(Vector4 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) &&
           float.IsFinite(value.Z) && float.IsFinite(value.W);

    private static bool IsUnitInterval(float value)
        => float.IsFinite(value) && value is >= 0.0f and <= 1.0f;
}
