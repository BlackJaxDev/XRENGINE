using System.Numerics;
using XREngine.Rendering;

namespace XREngine.Scene.Importers;

public static partial class SerializedMaterialImporter
{
    /// <summary>The keyword that the source shader defines when its post-processing section is on.</summary>
    private const string SourcePostProcessKeyword = "POSTPROCESS";

    /// <summary>
    /// Converts the source post-processing section to <c>_PostProcessSettings</c> and <c>_PostProcessHue</c>.
    /// The forward and native Uber shaders apply it to the lit color, before emission is added: hue shift,
    /// saturation, HDR curve, contrast, brightness multiply, and lightness add, in the source order.
    /// </summary>
    /// <remarks>
    /// A locked source shader bakes the section on or off with a <c>#define POSTPROCESS</c> line, and the
    /// material toggle can then be stale. So a locked shader file decides when the importer can read it.
    /// The post-processing mask and posterization have no Uber equivalent. A material that uses them
    /// gets a warning, and the importer converts the rest of the section.
    /// </remarks>
    private static void BindSourcePostProcess(
        XRMaterial material,
        SerializedMaterialDocument document,
        SourceAssetResolver resolver,
        List<string> warnings)
    {
        bool enabled = IsSourcePostProcessEnabled(document, resolver.Resolve(document.Shader.Guid));
        SetVector4(material, "_PostProcessSettings", new Vector4(
            enabled ? 1.0f : 0.0f,
            GetFloat(document, "_PPBrightness", 1.0f),
            GetFloat(document, "_PPContrast", 1.0f),
            GetFloat(document, "_PPSaturation", 1.0f)));
        SetVector4(material, "_PostProcessHue", new Vector4(
            GetFloat(document, "_PPHue"),
            GetFloat(document, "_PPLightness"),
            GetFloat(document, "_PPHDR"),
            GetFloat(document, "_PPHueShiftColorSpace")));
        if (!enabled)
            return;

        material.SetUberFeatureEnabled("surface-extensions", true);
        if (HasExternalTexture(document, "_PPMask"))
            warnings.Add($"Source post-processing mask on '{document.Name}' has no Uber conversion. The effect applies to the whole surface.");
        if (document.TryGetPositive("_PPPosterization"))
            warnings.Add($"Source post-processing posterization on '{document.Name}' has no Uber conversion.");
    }

    /// <summary>
    /// True when the source post-processing section is on. A locked shader file answers with its
    /// <c>#define POSTPROCESS</c> line; otherwise the material toggle or its keyword answers.
    /// </summary>
    private static bool IsSourcePostProcessEnabled(SerializedMaterialDocument document, string? shaderPath)
    {
        if (TryReadLockedShaderDefine(shaderPath, SourcePostProcessKeyword, out bool defined))
            return defined;

        return document.TryGetPositive("_PostProcess") ||
            document.ValidKeywords.Contains(SourcePostProcessKeyword);
    }

    /// <summary>
    /// Reads whether a locked source shader file defines <paramref name="keyword"/>. Returns false when
    /// the file is missing, unreadable, or not a locked shader, so the caller uses the material values.
    /// </summary>
    private static bool TryReadLockedShaderDefine(string? shaderPath, string keyword, out bool defined)
    {
        defined = false;
        if (string.IsNullOrWhiteSpace(shaderPath) || !File.Exists(shaderPath))
            return false;

        try
        {
            bool locked = false;
            string define = "#define " + keyword;
            foreach (string line in File.ReadLines(shaderPath))
            {
                string trimmed = line.Trim();
                if (!locked)
                {
                    if (trimmed.Length == 0 || trimmed.StartsWith("//", StringComparison.Ordinal))
                        continue;
                    if (!trimmed.StartsWith("Shader \"Hidden/Locked/", StringComparison.Ordinal))
                        return false;
                    locked = true;
                    continue;
                }

                if (trimmed.Equals(define, StringComparison.Ordinal))
                {
                    defined = true;
                    return true;
                }
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
