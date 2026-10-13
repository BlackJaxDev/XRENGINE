using System.Numerics;
using XREngine.Rendering;
using XREngine.Scene.Importers.SourceToon;

namespace XREngine.Scene.Importers;

public static partial class SerializedMaterialImporter
{
    private static void BindSourceClearCoatParameters(
        XRMaterial material,
        SerializedMaterialDocument document,
        ICollection<MaterialConversionDiagnostic> diagnostics,
        List<string> warnings)
    {
        if (!document.TryGetPositive("_ClearCoatBRDF"))
            return;

        string? unsupported = GetUnsupportedSourceClearCoatOption(document);
        if (unsupported is not null)
        {
            AddDiagnostic(diagnostics, warnings,
                MaterialConversionDiagnosticCodes.IntentionalNativeDifference,
                $"Source clear coat option '{unsupported}' on '{document.Name}' uses approximate Uber shading.",
                unsupported);
            SetFloat(material, "_ClearCoat", GetFloat(document, "_ClearCoatStrength", 1.0f));
            SetFloat(material, "_ClearCoatSmoothness", GetFloat(document, "_ClearCoatSmoothness", 1.0f));
            return;
        }

        SetVector4(material, "_SourceClearCoat", new Vector4(
            1.0f,
            GetFloat(document, "_ClearCoatSmoothness", 1.0f),
            GetFloat(document, "_ClearCoatReflectionStrength", 1.0f),
            GetFloat(document, "_ClearCoatSpecularStrength", 1.0f)));
        SetVector4(material, "_SourceClearCoatGsaa", new Vector4(
            GetFloat(document, "_ClearCoatGSAAEnabled"),
            GetFloat(document, "_ClearCoatGSAAVariance", 0.15f),
            GetFloat(document, "_ClearCoatGSAAThreshold", 0.1f),
            GetFloat(document, "_ClearCoatStrength", 1.0f)));
        SetVector4(material, "_SourceClearCoatReflection", new Vector4(
            GetFloat(document, "_ClearcoatFresnelStrength"),
            GetFloat(document, "_ClearcoatExposureOcclusion"),
            0.0f,
            0.0f));
    }

    private static string? GetUnsupportedSourceClearCoatOption(SerializedMaterialDocument document)
    {
        if (MathF.Abs(GetFloat(document, "_ClearCoatNormalSelect", 1.0f) - 1.0f) > 0.0001f)
            return "_ClearCoatNormalSelect";
        if (!document.TryGetPositive("_ClearcoatFresnel"))
            return "_ClearcoatFresnel";
        if (document.TryGetPositive("_CCIgnoreCastedShadows"))
            return "_CCIgnoreCastedShadows";
        if (HasExternalTexture(document, "_ClearCoatFallback"))
            return "_ClearCoatFallback";
        foreach (string property in new[]
        {
            "_ClearCoatMapsStochastic", "_ClearCoatForceFallback", "_ClearCoatGlobalMask",
            "_ClearCoatSmoothnessGlobalMask", "_ClearCoatReflectionStrengthGlobalMask",
            "_ClearCoatSpecularStrengthGlobalMask", "_ClearCoatTPSDepthMaskEnabled",
            "_ClearCoatMaskInvert", "_ClearCoatSmoothnessMapInvert",
            "_ClearCoatReflectionMaskInvert", "_ClearCoatSpecularMaskInvert",
            "_ClearCoatSpecularTintThemeIndex", "_ClearCoatReflectionTintThemeIndex"
        })
        {
            if (document.TryGetPositive(property))
                return property;
        }
        foreach ((string property, int expected) in new[]
        {
            ("_ClearCoatMapsUV", 0), ("_ClearCoatMapsClearCoatMaskChannel", 0),
            ("_ClearCoatMapsRoughnessChannel", 1),
            ("_ClearCoatMapsReflectionMaskChannel", 2),
            ("_ClearCoatMapsSpecularMaskChannel", 3)
        })
        {
            if (GetInt(document, property, expected) != expected)
                return property;
        }
        if (document.TryGetVector("_ClearCoatMapsPan", out Vector4 pan) &&
            (MathF.Abs(pan.X) > 0.0001f || MathF.Abs(pan.Y) > 0.0001f))
            return "_ClearCoatMapsPan";
        foreach (string property in new[] { "_ClearCoatSpecularTint", "_ClearCoatReflectionTint" })
        {
            if (document.TryGetVector(property, out Vector4 tint) &&
                (MathF.Abs(tint.X - 1.0f) > 0.0001f ||
                 MathF.Abs(tint.Y - 1.0f) > 0.0001f ||
                 MathF.Abs(tint.Z - 1.0f) > 0.0001f))
                return property;
        }
        return null;
    }
}
