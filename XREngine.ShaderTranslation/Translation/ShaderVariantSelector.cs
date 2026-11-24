namespace XREngine.ShaderTranslation.Translation;

/// <summary>
/// Selects the keywords that one variant of a translated program defines.
/// </summary>
/// <remarks>
/// For each keyword set, the first keyword that the material or the engine pass enables wins.
/// When none is enabled, the set uses its "no keyword" option if it has one; otherwise it uses
/// its first keyword, as Unity does.
/// </remarks>
public static class ShaderVariantSelector
{
    /// <summary>Returns the keywords to define for one stage, in declaration order and without duplicates.</summary>
    /// <param name="pragmas">The program pragmas.</param>
    /// <param name="enabledKeywords">The keywords that the material and the engine pass enable.</param>
    /// <param name="stage">The stage being compiled.</param>
    public static IReadOnlyList<string> Select(
        ShaderProgramPragmas pragmas,
        IReadOnlySet<string> enabledKeywords,
        ShaderProgramStage stage)
    {
        ArgumentNullException.ThrowIfNull(pragmas);
        ArgumentNullException.ThrowIfNull(enabledKeywords);

        List<string> selected = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (ShaderKeywordDeclaration declaration in pragmas.Keywords)
        {
            if (declaration.Stage is ShaderProgramStage only && only != stage)
                continue;

            string? choice = SelectFromSet(declaration, enabledKeywords);
            if (choice is not null && seen.Add(choice))
                selected.Add(choice);
        }

        return selected;
    }

    private static string? SelectFromSet(ShaderKeywordDeclaration declaration, IReadOnlySet<string> enabledKeywords)
    {
        foreach (string? keyword in declaration.Keywords)
            if (keyword is not null && enabledKeywords.Contains(keyword))
                return keyword;

        if (declaration.HasEmptyOption)
            return null;

        return declaration.Keywords[0];
    }
}
