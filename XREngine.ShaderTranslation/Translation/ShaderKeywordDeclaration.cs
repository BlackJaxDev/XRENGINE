namespace XREngine.ShaderTranslation.Translation;

/// <summary>
/// Holds one keyword set from a <c>multi_compile</c>, <c>shader_feature</c>, or <c>dynamic_branch</c> pragma.
/// </summary>
/// <param name="Directive">The pragma that declared the set.</param>
/// <param name="Keywords">The keywords in source order. A null entry is the "no keyword" option (<c>_</c> or <c>__</c>).</param>
/// <param name="Stage">The only stage that receives the keyword, or null for all stages.</param>
/// <param name="Line">The one-based source line of the pragma, or zero for a shorthand expansion.</param>
public sealed record ShaderKeywordDeclaration(
    ShaderKeywordDirective Directive,
    IReadOnlyList<string?> Keywords,
    ShaderProgramStage? Stage,
    int Line)
{
    /// <summary>Returns true when the set has a "no keyword" option.</summary>
    public bool HasEmptyOption
    {
        get
        {
            foreach (string? keyword in Keywords)
                if (keyword is null)
                    return true;
            return false;
        }
    }
}
