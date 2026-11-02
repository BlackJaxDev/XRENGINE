using System.Text.RegularExpressions;

namespace XREngine.Scene.Importers.SourceToon;

/// <summary>Proves unused texture inputs only for verified source shader operations.</summary>
internal static partial class SourceToonTextureUsageClassifier
{
    /// <summary>Checks saved controls and every AO use in a locked shader.</summary>
    internal static bool IsInactiveLockedAo(
        SerializedMaterialDocument document,
        string slot,
        SourceToonShaderMatchResult match,
        string shaderSource)
    {
        if (slot != "_LightingAOMaps" || !match.IsAccepted || !match.IsLocked ||
            match.Version != new SourceToonShaderVersion(10, 0, 25))
            return false;

        foreach (char channel in "RGBA")
        {
            string property = "_LightDataAOStrength" + channel;
            if (!document.TryGetFloat(property, out float strength) || strength != 0.0f ||
                document.OverrideTags.TryGetValue(property + "Animated", out string? mode) && mode != "0")
                return false;
        }

        // Locked controls must also be zero in the compiled source. Saved values alone
        // do not describe optimizer constants or animated property aliases.
        int samples = 0;
        int reductions = 0;
        bool inProgram = false;
        foreach (string sourceLine in shaderSource.Split('\n'))
        {
            string line = sourceLine.Trim();
            if (line is "CGINCLUDE" or "CGPROGRAM" or "HLSLINCLUDE" or "HLSLPROGRAM")
                inProgram = true;
            else if (line is "ENDCG" or "ENDHLSL")
                inProgram = false;
            if (!inProgram)
                continue;

            if (AoTextureIdentifierRegex().IsMatch(line))
            {
                if (line == "Texture2D _LightingAOMaps;")
                    continue;
                if (!AoSampleRegex().IsMatch(line))
                    return false;
                samples++;
            }
            else if (AoValueIdentifierRegex().IsMatch(line))
            {
                if (!ZeroAoReductionRegex().IsMatch(line))
                    return false;
                reductions++;
            }
        }

        return samples > 0 && samples == reductions;
    }

    [GeneratedRegex(@"\b_LightingAOMaps\b", RegexOptions.CultureInvariant)]
    private static partial Regex AoTextureIdentifierRegex();

    [GeneratedRegex(@"\bAOMaps\b", RegexOptions.CultureInvariant)]
    private static partial Regex AoValueIdentifierRegex();

    [GeneratedRegex(@"^float4\s+AOMaps\s*=\s*POI2D_SAMPLER_PAN\(_LightingAOMaps,\s*_MainTex,\s*poiUV\(poiMesh\.uv\[[0-9.]+\],\s*float4\([0-9., -]+\)\),\s*float4\([0-9., -]+\)\);$", RegexOptions.CultureInvariant)]
    private static partial Regex AoSampleRegex();

    [GeneratedRegex(@"^poiLight\.occlusion\s*=\s*min\(min\(min\(lerp\(1,\s*AOMaps\.r,\s*0\.0\),\s*lerp\(1,\s*AOMaps\.g,\s*0\.0\)\),\s*lerp\(1,\s*AOMaps\.b,\s*0\.0\)\),\s*lerp\(1,\s*AOMaps\.a,\s*0\.0\)\);$", RegexOptions.CultureInvariant)]
    private static partial Regex ZeroAoReductionRegex();
}
