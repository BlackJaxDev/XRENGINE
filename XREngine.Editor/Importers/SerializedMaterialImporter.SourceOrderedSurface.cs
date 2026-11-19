using System.Numerics;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Scene.Importers.SourceToon;

namespace XREngine.Scene.Importers;

public static partial class SerializedMaterialImporter
{
    private static SourceOrderedSurfacePlan ClassifySourceOrderedSurface(
        SerializedMaterialDocument document,
        string? shaderPath,
        bool isLockedShader)
    {
        bool grabFlag = document.TryGetPositive("_EnableGrabpass");
        SourceGrabShaderShape grabShape = grabFlag && isLockedShader &&
            !string.IsNullOrWhiteSpace(shaderPath) && File.Exists(shaderPath)
            ? ReadSourceGrabShaderShape(shaderPath)
            : SourceGrabShaderShape.Unknown;
        bool hasGrab = grabFlag && grabShape != SourceGrabShaderShape.Absent;
        int sourceQueue = document.CustomRenderQueue;
        if (SourceToonRenderStateConverter.IsHornsSoftOutlineSource(document))
            return default;
        if (sourceQueue <= 2500 && !hasGrab)
            return default;
        if (sourceQueue <= 2500)
            return new(default, "An active source GrabPass requires a source queue above 2500.");

        if (document.TryGetPositive("_EnableOutlines") || document.TryGetPositive("_EnableSoftOutlines") ||
            document.TryGetPositive("_OutlinesEnabled") ||
            document.TryGetPositive("_UseOutline") || document.TryGetPositive("_RenderingEarlyZEnabled") ||
            document.TryGetPositive("_AlphaToCoverage"))
            return new(default, "An ordered source surface has an outline, EarlyZ, or alpha-to-coverage pass that the late lane cannot replay.");

        if (!hasGrab)
            return new(EAdvancedSourceSurfaceContract.OrderedToonV1, null);

        if (grabShape != SourceGrabShaderShape.Named)
            return new(default, "The active source GrabPass has no verified named _PoiGrab2 shader contract.");
        if (document.TryGetPositive("_RefractionEnabled") || document.TryGetPositive("_EnableBlur") ||
            document.TryGetPositive("_GrabPassHueShiftToggle") ||
            GetSourceFloat(document, "_GrabPassBlendGlobalMask") != 0.0f ||
            HasSourceTexture(document, "_GrabPassBlendMap"))
            return new(default, "The active source GrabPass uses refraction, blur, hue, a global mask, or a blend map that has no exact scene-color mapping.");
        if (GetSourceFloat(document, "_GrabSrcBlend") != 0.0f ||
            GetSourceFloat(document, "_GrabDstBlend", 10.0f) != 10.0f)
            return new(default, "The source GrabPass blend factors are outside the verified zero/one-minus-source-alpha contract.");
        if (GetSourceFloat(document, "_AlphaFresnelGlobalMask") != 0.0f ||
            document.TryGetPositive("_AlphaDistanceFade") ||
            document.TryGetPositive("_AlphaAngular") ||
            document.TryGetPositive("_AlphaAudioLinkEnabled"))
            return new(default, "The source alpha path has an active mask, distance, angular, or AudioLink option without an exact scene-color mapping.");
        if (!document.TryGetVector("_GrabpassColor", out Vector4 grabColor) ||
            !IsFinite(grabColor) ||
            !float.IsFinite(GetSourceFloat(document, "_GrabPassBrightness", 1.0f)) ||
            !float.IsFinite(GetSourceFloat(document, "_GrabPassAddBrightness")) ||
            !IsUnitInterval(GetSourceFloat(document, "_GrabPassBlendFactor", 1.0f)) ||
            !float.IsFinite(GetSourceFloat(document, "_GrabPassUseAlpha", 1.0f)) ||
            !float.IsFinite(GetSourceFloat(document, "_AlphaMod")) ||
            GetSourceFloat(document, "_AlphaFresnel") is not (0.0f or 1.0f) ||
            !IsUnitInterval(GetSourceFloat(document, "_AlphaFresnelAlpha")) ||
            !IsUnitInterval(GetSourceFloat(document, "_AlphaFresnelSharpness", 0.5f)) ||
            !IsUnitInterval(GetSourceFloat(document, "_AlphaFresnelWidth", 0.5f)) ||
            GetSourceFloat(document, "_AlphaFresnelInvert") is not (0.0f or 1.0f))
            return new(default, "The source GrabPass color, blend, or alpha controls are missing, non-finite, or outside their verified ranges.");
        if (GetSourceFloat(document, "_LightingMode") != 5.0f)
            return new(default, "The active source GrabPass needs a verified lighting divisor for this shading mode.");
        if (GetSourceFloat(document, "_Mode") != 0.0f ||
            GetSourceFloat(document, "_AlphaForceOpaque") != 1.0f ||
            GetSourceFloat(document, "_Cutoff") != 0.0f)
            return new(default, "The active source GrabPass needs the verified opaque, late-ForceOpaque, zero-cutoff surface state.");

        return new(EAdvancedSourceSurfaceContract.SceneColorToonV1, null);
    }

    private static void BindSourceOrderedSurface(
        XRMaterial material,
        SerializedMaterialDocument document,
        SourceOrderedSurfacePlan plan,
        ICollection<MaterialConversionDiagnostic> diagnostics,
        ICollection<string> warnings)
    {
        if (plan.UnsupportedReason is not null)
            throw new SourceVisualImportException(plan.UnsupportedReason);
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
        MapVector4(document, material, "_SourceGrabColor", "_GrabpassColor");
        SetFloat(material, "_SourceGrabBrightness", GetSourceFloat(document, "_GrabPassBrightness", 1.0f));
        SetFloat(material, "_SourceGrabAddBrightness", GetSourceFloat(document, "_GrabPassAddBrightness"));
        SetFloat(material, "_SourceGrabBlendFactor", GetSourceFloat(document, "_GrabPassBlendFactor", 1.0f));
        SetFloat(material, "_SourceGrabUseAlpha", GetSourceFloat(document, "_GrabPassUseAlpha", 1.0f));
        SetFloat(material, "_AlphaFresnel", GetSourceFloat(document, "_AlphaFresnel"));
        SetFloat(material, "_AlphaFresnelAlpha", GetSourceFloat(document, "_AlphaFresnelAlpha"));
        SetFloat(material, "_AlphaFresnelSharpness", GetSourceFloat(document, "_AlphaFresnelSharpness", 0.5f));
        SetFloat(material, "_AlphaFresnelWidth", GetSourceFloat(document, "_AlphaFresnelWidth", 0.5f));
        SetFloat(material, "_AlphaFresnelInvert", GetSourceFloat(document, "_AlphaFresnelInvert"));
    }

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
