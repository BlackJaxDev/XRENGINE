using XREngine.Rendering;

namespace XREngine.Scene.Importers;

public static partial class SerializedMaterialImporter
{
    private const string SoftOutlineColorSampler = "_NoiseDistTexture";
    private const string SoftOutlineMaskSampler = "_MaskTexture";

    /// <summary>
    /// Copies the soft outline values and textures to the base material.
    /// The fin pass variant shares the parameters and textures of the base material.
    /// </summary>
    private static void ApplyHornsSoftOutlineParameters(
        XRMaterial material,
        SerializedMaterialDocument document,
        SourceAssetResolver resolver,
        List<string> warnings)
    {
        MapFloat(document, material, "_Thickness", "_Thickness");
        MapFloat(document, material, "_MaxOutlineSize", "_MaxOutlineSize");
        MapFloat(document, material, "_OutlineLimitBreak", "_OutlineLimitBreak");
        MapFloat(document, material, "_AttenuateFinalEdge", "_AttenuateFinalEdge");
        MapFloat(document, material, "_AttenuateEdge", "_AttenuateEdge");
        MapFloat(document, material, "_EdgeMinValue", "_EdgeMinValue");
        MapFloat(document, material, "_EdgePower", "_EdgePower");
        MapFloat(document, material, "_GradientOffset", "_GradientOffset");
        MapVector4(document, material, "_SmoothFinalEdge", "_SmoothFinalEdge");
        MapVector4(document, material, "_Col1", "_Col1");
        MapInt(document, material, "_InvertGradient", "_InvertGradient");

        BindSoftOutlineTexture(material, document, resolver, warnings, SoftOutlineColorSampler);
        BindSoftOutlineTexture(material, document, resolver, warnings, SoftOutlineMaskSampler);
        MapVector2(document, material, "_TexturePanning", "_TexturePanning");
        MapVector2(document, material, "_MaskPanning", "_MaskPanning");
    }

    /// <summary>
    /// Binds one fin texture with its source sampler name, scale, and offset.
    /// The fin shader samples the texture without a guard, thus a missing texture is an import failure.
    /// </summary>
    private static void BindSoftOutlineTexture(
        XRMaterial material,
        SerializedMaterialDocument document,
        SourceAssetResolver resolver,
        List<string> warnings,
        string samplerName)
    {
        XRTexture2D texture = ResolveUberTexture(document, resolver, warnings, samplerName, samplerName)
            ?? throw new SourceVisualImportException(
                $"The soft outline of material '{document.Name}' requires texture '{samplerName}', and the importer could not resolve it.");

        ReplaceMaterialSampler(material, samplerName, texture);
        ApplyTextureTransform(material, document, samplerName, samplerName);
    }
}
