using System.Numerics;
using XREngine.Rendering;
using XREngine.Scene.Importers.SourceToon;

namespace XREngine.Scene.Importers;

public static partial class SerializedMaterialImporter
{
    private static void BindSourceLightingParameters(
        XRMaterial material,
        SerializedMaterialDocument document,
        ICollection<MaterialConversionDiagnostic> diagnostics,
        List<string> warnings)
    {
        int colorMode = GetInt(document, "_LightingColorMode");
        int directionMode = GetInt(document, "_LightingDirectionMode");
        int shadingMode = GetInt(document, "_LightingMode");
        if (colorMode != 0 && colorMode != 2 || directionMode != 0 ||
            shadingMode != 0 && shadingMode != 2 && shadingMode != 5 ||
            GetInt(document, "_LightingMapMode") > 2 ||
            document.TryGetPositive("_LightingForceColorEnabled") ||
            shadingMode == 2 && HasExternalTexture(document, "_LightingDetailShadowMaps"))
        {
            AddDiagnostic(diagnostics, warnings,
                MaterialConversionDiagnosticCodes.IntentionalNativeDifference,
                $"Source light option on '{document.Name}' uses approximate Uber shading.",
                colorMode != 0 && colorMode != 2 ? "_LightingColorMode" :
                directionMode != 0 ? "_LightingDirectionMode" :
                shadingMode != 0 && shadingMode != 2 && shadingMode != 5 ? "_LightingMode" :
                GetInt(document, "_LightingMapMode") > 2 ? "_LightingMapMode" :
                document.TryGetPositive("_LightingForceColorEnabled")
                    ? "_LightingForceColorEnabled" : "_LightingDetailShadowMaps");
            return;
        }

        SetInt(material, "_SourceLightMode", 1);
        SetVector4(material, "_SourceLightSettings", new Vector4(
            GetFloat(document, "_Unlit_Intensity", 1.0f),
            GetFloat(document, "_LightingCastedShadows", 1.0f),
            GetFloat(document, "_PPLightingMultiplier", 1.0f),
            GetFloat(document, "_PPLightingAddition")));
        SetVector4(material, "_SourceAdditiveSettings", new Vector4(
            GetFloat(document, "_LightingAdditiveEnable", 1.0f),
            GetFloat(document, "_DisableDirectionalInAdd", 1.0f),
            GetFloat(document, "_LightingAdditiveLimited", 1.0f),
            GetFloat(document, "_LightingAdditiveLimit", 1.0f)));
        SetVector4(material, "_SourceAdditiveColor", new Vector4(
            GetFloat(document, "_LightingAdditiveMonochromatic"),
            GetFloat(document, "_LightingAdditivePassthrough"),
            GetInt(document, "_LightingAdditiveType"),
            GetFloat(document, "_LightingAdditiveCastedShadows", 1.0f)));
        SetVector4(material, "_SourceAdditiveGradient", new Vector4(
            GetFloat(document, "_LightingAdditiveGradientStart"),
            GetFloat(document, "_LightingAdditiveGradientEnd", 0.5f),
            0.0f, 0.0f));
        SetVector4(material, "_SourceWrappedColor",
            document.TryGetVector("_LightingWrappedColor", out Vector4 wrappedColor)
                ? wrappedColor : Vector4.One);
        AddDiagnostic(diagnostics, warnings,
            MaterialConversionDiagnosticCodes.IntentionalNativeDifference,
            $"Source lighting on '{document.Name}' uses authored world SH when enabled and constant ambient otherwise. Engine point and spot lights use per-pixel additive evaluation and engine attenuation. Unity vertex-light assignment and light cookies require separate source data.",
            "_LightingColorMode");
    }
}
