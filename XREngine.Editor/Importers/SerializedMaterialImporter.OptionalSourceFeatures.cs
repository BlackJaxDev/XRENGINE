using System.Numerics;
using XREngine.Scene.Importers.SourceToon;

namespace XREngine.Scene.Importers;

public static partial class SerializedMaterialImporter
{
    /// <summary>
    /// Runs the binder of one optional source feature, such as a second rim light or a stylized
    /// specular lobe. If the binder finds a source option that has no exact Uber conversion, the
    /// feature stays off and the rest of the material still converts to the Uber shader.
    /// </summary>
    /// <remarks>
    /// A generic fallback material keeps only the main texture and color, so it loses much more of
    /// the source look than one missing feature. Each binder checks its options before it writes
    /// material values, so a refused feature leaves no partial values. The warning and the
    /// <see cref="MaterialConversionDiagnosticCodes.SourceValueNotPreserved"/> diagnostic name the
    /// refused option in the conversion report.
    /// </remarks>
    /// <param name="featureName">The feature name that the warning shows.</param>
    /// <param name="bind">Checks the source options and writes the feature values.</param>
    /// <param name="disable">Turns the feature off after a refusal, or null when the feature is off by default.</param>
    /// <param name="warnings">Receives the warning text.</param>
    /// <param name="diagnostics">Receives the conversion diagnostic.</param>
    /// <returns>True when the feature converted; false when it stays off.</returns>
    private static bool BindOptionalSourceFeature(
        string featureName,
        Action bind,
        Action? disable,
        List<string> warnings,
        ICollection<MaterialConversionDiagnostic> diagnostics)
    {
        try
        {
            bind();
            return true;
        }
        catch (SourceVisualImportException ex)
        {
            disable?.Invoke();
            string message = $"Source {featureName} stays off: {ex.Message}";
            warnings.Add(message);
            diagnostics.Add(new MaterialConversionDiagnostic(
                MaterialConversionDiagnosticCodes.SourceValueNotPreserved,
                MaterialConversionDiagnosticSeverity.Warning,
                message));
            return false;
        }
    }

    /// <summary>
    /// Returns the color that the source <c>poiThemeColor</c> function gives for a theme index
    /// from 1 to 4, when that color is constant: the theme has no hue, saturation, or value
    /// adjustment and no hue animation. Returns null for other indices (color chords and
    /// AudioLink themes) and for adjusted themes.
    /// </summary>
    private static Vector4? ResolveStaticSourceThemeColor(SerializedMaterialDocument document, int themeIndex)
    {
        if (themeIndex is < 1 or > 4)
            return null;

        int theme = themeIndex - 1;
        if (GetFloat(document, $"_GlobalThemeHue{theme}") != 0.0f ||
            GetFloat(document, $"_GlobalThemeHueSpeed{theme}") != 0.0f ||
            GetFloat(document, $"_GlobalThemeSaturation{theme}") != 0.0f ||
            GetFloat(document, $"_GlobalThemeValue{theme}") != 0.0f)
            return null;

        return document.TryGetVector($"_GlobalThemeColor{theme}", out Vector4 color) ? color : null;
    }
}
