using System.Globalization;

namespace XREngine.Scene.Importers;

/// <summary>
/// Reads the source project quality settings that change texture sampling.
/// </summary>
internal static class SourceProjectQualitySettings
{
    private const int ForcedAnisotropyMode = 2;
    private const int ForcedAnisotropyMinimum = 9;

    /// <summary>
    /// Returns the lowest anisotropy level of the current quality level.
    /// The source raises lower texture levels to this value when it forces anisotropic filtering.
    /// </summary>
    public static int ReadMinimumAnisotropy(string projectRoot)
    {
        string path = Path.Combine(projectRoot, "ProjectSettings", "QualitySettings.asset");
        if (!File.Exists(path))
            return 1;

        int currentQuality = -1;
        int qualityIndex = -1;
        foreach (string line in File.ReadLines(path))
        {
            string trimmed = line.Trim();
            if (TryReadInt(trimmed, "m_CurrentQuality:", out int value))
                currentQuality = value;
            else if (trimmed.StartsWith("- serializedVersion:", StringComparison.Ordinal))
                ++qualityIndex;
            else if (qualityIndex == currentQuality && TryReadInt(trimmed, "anisotropicTextures:", out value))
                return value == ForcedAnisotropyMode ? ForcedAnisotropyMinimum : 1;
        }
        return 1;
    }

    private static bool TryReadInt(string line, string prefix, out int value)
    {
        value = 0;
        return line.StartsWith(prefix, StringComparison.Ordinal) &&
            int.TryParse(line.AsSpan(prefix.Length).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }
}
