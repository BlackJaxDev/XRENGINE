using System.Text.RegularExpressions;

namespace XREngine.ShaderTranslation.Compilation;

public static partial class GlslPostProcessor
{
    /// <summary>
    /// Changes the index of each per-vertex output write in a tessellation control stage to <c>gl_InvocationID</c>.
    /// </summary>
    /// <remarks>
    /// SPIRV-Cross copies <c>gl_InvocationID</c> to a local, for example <c>uint _105 = uint(gl_InvocationID);</c>,
    /// and indexes the outputs with the local. GLSL lets a tessellation control stage write a per-vertex output
    /// only at the index <c>gl_InvocationID</c>, and strict compilers reject the local. The method replaces each
    /// index with such a local by <c>gl_InvocationID</c>, which has the same value.
    /// </remarks>
    /// <param name="glsl">The tessellation control stage.</param>
    public static string IndexOutputsByInvocation(string glsl)
    {
        ArgumentNullException.ThrowIfNull(glsl);
        foreach (Match local in InvocationLocalPattern().Matches(glsl))
            glsl = glsl.Replace($"[{local.Groups["name"].Value}]", "[gl_InvocationID]", StringComparison.Ordinal);

        return glsl;
    }

    /// <summary>
    /// Sets the spacing of a tessellation evaluation stage from the HLSL <c>partitioning</c> attribute of the hull stage.
    /// </summary>
    /// <remarks>
    /// HLSL puts the partitioning on the hull stage. GLSL reads the spacing only from the tessellation evaluation
    /// stage, and SPIRV-Cross writes <c>equal_spacing</c> there because the domain SPIR-V has no spacing.
    /// </remarks>
    /// <param name="domainGlsl">The tessellation evaluation stage.</param>
    /// <param name="hullHlsl">The translation unit of the hull stage.</param>
    /// <param name="hullEntryPoint">The hull entry point function.</param>
    public static string ApplyHullPartitioning(string domainGlsl, string hullHlsl, string hullEntryPoint)
    {
        ArgumentNullException.ThrowIfNull(domainGlsl);
        ArgumentNullException.ThrowIfNull(hullHlsl);
        ArgumentNullException.ThrowIfNull(hullEntryPoint);

        string? spacing = FindHullPartitioning(hullHlsl, hullEntryPoint) switch
        {
            "fractional_odd" => "fractional_odd_spacing",
            "fractional_even" => "fractional_even_spacing",
            "integer" or "pow2" => "equal_spacing",
            _ => null,
        };
        return spacing is null ? domainGlsl : SpacingPattern().Replace(domainGlsl, spacing, 1);
    }

    /// <summary>Returns the partitioning of the attributes directly before the hull entry point, or null.</summary>
    private static string? FindHullPartitioning(string hlsl, string entryPoint)
    {
        // The attributes and the return type come before the name. A short window keeps the
        // attributes of another function out of the search.
        const int attributeWindow = 600;
        foreach (Match declaration in Regex.Matches(hlsl, $@"\b{Regex.Escape(entryPoint)}\s*\(", RegexOptions.CultureInvariant))
        {
            int start = Math.Max(0, declaration.Index - attributeWindow);
            string before = hlsl[start..declaration.Index];
            int lastStatementEnd = Math.Max(before.LastIndexOf(';'), before.LastIndexOf('}'));
            MatchCollection attributes = PartitioningPattern().Matches(before);
            if (attributes.Count > 0 && attributes[^1].Index > lastStatementEnd)
                return attributes[^1].Groups["mode"].Value;
        }

        return null;
    }

    [GeneratedRegex(@"^\s*uint (?<name>_\d+) = uint\(gl_InvocationID\);$", RegexOptions.Multiline)]
    private static partial Regex InvocationLocalPattern();

    [GeneratedRegex(@"\b(?:equal|fractional_odd|fractional_even)_spacing\b")]
    private static partial Regex SpacingPattern();

    [GeneratedRegex(@"partitioning\s*\(\s*""(?<mode>\w+)""\s*\)")]
    private static partial Regex PartitioningPattern();
}
