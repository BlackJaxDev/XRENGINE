using System.Numerics;
using XREngine.Data.Colors;
using XREngine.Rendering;

namespace XREngine.Scene.Importers;

public static partial class SerializedMaterialImporter
{
    private static void BindSourceStylizedSpecular(
        XRMaterial material,
        SerializedMaterialDocument document,
        SourceAssetResolver resolver,
        List<string> warnings)
    {
        if (!document.TryGetPositive("_StylizedSpecular"))
            return;

        string? shaderPath = resolver.Resolve(document.Shader.Guid);
        string? unsupported = GetUnsupportedSourceStylizedSpecularOption(document, shaderPath);
        if (unsupported is not null)
            throw new SourceVisualImportException(
                $"Active source stylized specular option '{unsupported}' on '{document.Name}' has no exact Uber conversion.");

        SetVector4(material, "_SourceStylizedSpecularSettings", new Vector4(
            1.0f,
            GetFloat(document, "_Is_SpecularToHighColor"),
            GetFloat(document, "_StylizedSpecularNormalStrength", 1.0f),
            GetFloat(document, "_UseLightColor", 1.0f)));
        SetVector4(material, "_SourceStylizedSpecularLayers", new Vector4(
            GetFloat(document, "_HighColor_Power", 0.2f),
            GetFloat(document, "_Layer1Strength", 1.0f),
            GetFloat(document, "_Layer2Size"),
            GetFloat(document, "_Layer2Strength")));
        SetVector4(material, "_SourceStylizedSpecularMask", new Vector4(
            GetFloat(document, "_StylizedSpecularFeather"),
            GetFloat(document, "_StylizedSpecular2Feather"),
            GetFloat(document, "_StylizedSpecularStrength", 1.0f),
            GetFloat(document, "_Set_HighColorMaskChannel", 1.0f)));
        SetVector4(material, "_SourceStylizedSpecularOcclusion", new Vector4(
            GetFloat(document, "_StylizedSpecularInvertMask"),
            GetFloat(document, "_Tweak_HighColorMaskLevel"),
            GetFloat(document, "_StylizedSpecularIgnoreNormal"),
            GetFloat(document, "_StylizedSpecularIgnoreShadow")));
        SetVector4(material, "_SourceStylizedSpecularTint",
            document.TryGetVector("_HighColor", out Vector4 tint) ? tint : Vector4.One);

        BindSourceStylizedSpecularTexture(material, document, resolver, warnings,
            "_HighColor_Tex", "_SourceHighColorTex");
        BindSourceStylizedSpecularTexture(material, document, resolver, warnings,
            "_Set_HighColorMask", "_SourceHighColorMask");

        // Source specular uses the high-color maps and its own lobe. The generic
        // specular evaluator must not add a second highlight from _SpecularMap.
        SetFloat(material, "_SpecularStrength", 0.0f);
    }

    private static void BindSourceStylizedSpecularTexture(
        XRMaterial material,
        SerializedMaterialDocument document,
        SourceAssetResolver resolver,
        List<string> warnings,
        string source,
        string destination)
    {
        XRTexture2D texture = ResolveUberTexture(document, resolver, warnings, source, destination)
            ?? GetDefaultUberSamplerTexture(destination, ColorF4.White);
        ReplaceMaterialSampler(material, destination, texture);
        ApplySourceToonMeshTextureTransform(material, document, source, destination);
    }

    private static string? GetUnsupportedSourceStylizedSpecularOption(
        SerializedMaterialDocument document,
        string? shaderPath)
    {
        if (GetInt(document, "_StylizedReflectionMode") != 0)
            return "_StylizedReflectionMode";
        if (GetInt(document, "_Is_BlendAddToHiColor", 1) != 1)
            return "_Is_BlendAddToHiColor";
        if (GetInt(document, "_HighColorThemeIndex") != 0)
            return "_HighColorThemeIndex";
        if (document.TryGetPositive("_SSIgnoreCastedShadows"))
            return "_SSIgnoreCastedShadows";
        foreach (string name in new[] { "_HighColor_Tex", "_Set_HighColorMask" })
        {
            if (!HasExternalTexture(document, name) && !HasWhiteShaderTextureDefault(shaderPath, name))
                return name + " default";
            if (GetInt(document, name + "UV") != 0)
                return name + "UV";
            if (document.TryGetVector(name + "Pan", out Vector4 pan) && pan != Vector4.Zero)
                return name + "Pan";
        }
        return null;
    }
}
