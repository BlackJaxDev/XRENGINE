namespace XREngine.ShaderTranslation.Translation;

/// <summary>
/// Identifies the pragma that declared a keyword set.
/// </summary>
public enum ShaderKeywordDirective
{
    /// <summary><c>multi_compile</c>: Unity builds every keyword of the set.</summary>
    MultiCompile,

    /// <summary><c>shader_feature</c>: Unity builds only keywords that materials use.</summary>
    ShaderFeature,

    /// <summary><c>dynamic_branch</c>: Unity uses a uniform branch instead of a variant.</summary>
    DynamicBranch,
}
