using System.Numerics;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Scene.Importers.SourceToon;
using XREngine.Scene.Prefabs;

namespace XREngine.Scene.Importers;

public static partial class SerializedMaterialImporter
{
    /// <summary>Bind source dissolve values after exact-name properties.</summary>
    private static void BindSourceToonDissolve(
        XRMaterial material,
        SerializedMaterialDocument document,
        ICollection<MaterialConversionDiagnostic> diagnostics)
    {
        SetFloat(material, "_DissolveType", SourceToonEnumMapper.DissolveMode(document, diagnostics));
        float amount = GetFloat(document, "_DissolveAlpha");
        for (int index = 0; index < 10; ++index)
            amount += GetFloat(document, "_DissolveAlpha" + index);
        SetFloat(material, "_DissolveProgress", Math.Clamp(amount, 0.0f, 1.0f));
        SetFloat(material, "_DissolveInvert", GetFloat(document, "_DissolveInvertNoise"));
        SetVector4(material, "_SourceDissolveParams", new Vector4(
            GetFloat(document, "_DissolveEdgeHardness", 0.5f),
            GetFloat(document, "_DissolveInvertDetailNoise"),
            GetFloat(document, "_DissolveDetailEdgeSmoothing"),
            GetFloat(document, "_ContinuousDissolve")));
        SetVector4(material, "_DissolveTextureColor",
            document.TryGetVector("_DissolveTextureColor", out Vector4 color) ? color : Vector4.One);
        SetFloat(material, "_DissolveToEmissionStrength", GetFloat(document, "_DissolveToEmissionStrength"));
    }
}
