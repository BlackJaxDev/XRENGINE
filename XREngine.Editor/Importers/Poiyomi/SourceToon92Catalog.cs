namespace XREngine.Scene.Importers.SourceToon;

/// <summary>
/// Identifies the verified Poiyomi Toon sources used by the 9.2 material converter.
/// </summary>
public static class SourceToon92Catalog
{
    public const string ShaderGuid = "104003ab15a9f7a47b6ef0597225e771";
    public const string ShaderPathSegment = "/_PoiyomiShaders/Shaders/9.2/Toon/";
    public const string ShaderSha256_66 = "7e49b9ff0928ec67ee18374cd4a492d4958d7b9be438303a14559d3356f4fab9";
    public const string ShaderSha256_79 = "8342242bec8f28292fd83bc796ef2ad5518052577742fc3b1bfa0d22687422af";

    public static readonly SourceToonShaderVersion Version66 = new(9, 2, 66);
    public static readonly SourceToonShaderVersion Version79 = new(9, 2, 79);

    public static bool Supports(SourceToonShaderVersion? version)
        => version == Version66 || version == Version79;

    /// <summary>
    /// Reports active 9.2 controls that have no native Uber mapping.
    /// </summary>
    public static void AppendUnmappedFeatureDiagnostics(
        SerializedMaterialDocument document,
        ICollection<MaterialConversionDiagnostic> diagnostics)
    {
        string[] numericProperties = ["_PoiInternalParallax", "_AudioLinkDelay", "_AudioLinkCCStripY"];
        foreach (string property in numericProperties)
        {
            if (!document.TryGetFloat(property, out float value) || MathF.Abs(value) <= 0.000001f)
                continue;

            diagnostics.Add(new MaterialConversionDiagnostic(
                MaterialConversionDiagnosticCodes.RuntimeMappingMissing,
                MaterialConversionDiagnosticSeverity.Warning,
                $"Poiyomi 9.2 property '{property}' is active but has no native Uber mapping. Its value remains in the source descriptor.",
                property));
        }

        if (document.Textures.TryGetValue("_AnisoNoiseMap", out SerializedTexturePropertyDocument? noise) &&
            !string.IsNullOrWhiteSpace(noise.TextureReference.Guid))
        {
            diagnostics.Add(new MaterialConversionDiagnostic(
                MaterialConversionDiagnosticCodes.RuntimeMappingMissing,
                MaterialConversionDiagnosticSeverity.Warning,
                "Poiyomi 9.2 anisotropic noise texture has no native Uber mapping. Its reference remains in the source descriptor.",
                "_AnisoNoiseMap"));
        }
    }
}
