using System.Numerics;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Scene.Importers;

public static partial class SerializedMaterialImporter
{
    private const float SourceLinearDetailGray = 0.21763764082f;
    private const float SourceLinearColorSpaceDouble = 4.5947938f;

    /// <summary>
    /// Converts the source detail multiplier to the Uber detail controls.
    /// </summary>
    private static void NormalizeSourceToonDetailColor(
        XRMaterial material,
        SerializedMaterialDocument document,
        string? detailTexSource,
        string projectRoot)
    {
        bool linearColorSpace = ReadSourceLinearColorSpace(projectRoot);
        float colorSpaceDouble = linearColorSpace ? SourceLinearColorSpaceDouble : 2.0f;
        float sourceGray = linearColorSpace ? SourceLinearDetailGray : 0.5f;
        float sampleScale = detailTexSource is null ? sourceGray : 1.0f;
        float brightness = document.TryGetFloat("_DetailBrightness", out float authoredBrightness)
            ? authoredBrightness
            : 1.0f;
        Vector3 tint = document.TryGetVector("_DetailTint", out Vector4 authoredTint)
            ? new Vector3(authoredTint.X, authoredTint.Y, authoredTint.Z)
            : Vector3.One;

        // The source multiplies the detail sample, tint, brightness, and color-space factor.
        // The engine adds brightness, so put the source factor in tint and clear the offset.
        SetVector3(material, "_DetailTint", tint * (brightness * colorSpaceDouble * sampleScale));
        SetFloat(material, "_DetailBrightness", 0.0f);
        SetInt(material, "_DetailTexBlendMode", 2);
    }

    private static bool ReadSourceLinearColorSpace(string projectRoot)
    {
        string settingsPath = Path.Combine(projectRoot, "ProjectSettings", "ProjectSettings.asset");
        // Standalone material imports can omit project settings. Use linear color space for them.
        if (!File.Exists(settingsPath))
            return true;

        foreach (string line in File.ReadLines(settingsPath))
        {
            const string key = "m_ActiveColorSpace:";
            string value = line.Trim();
            if (!value.StartsWith(key, StringComparison.Ordinal))
                continue;

            return value[key.Length..].Trim() switch
            {
                "0" => false,
                "1" => true,
                _ => throw new SourceVisualImportException("Unity project color space is unknown; detail color cannot be converted."),
            };
        }

        throw new SourceVisualImportException("Unity project color-space setting is missing; detail color cannot be converted.");
    }
}
