namespace XREngine.ShaderTranslation.Translation;

/// <summary>
/// Describes the inputs of one shader translation.
/// </summary>
public sealed class ShaderTranslationRequest
{
    /// <summary>Gets the keywords that the material enables (<c>m_ValidKeywords</c> and <c>m_ShaderKeywords</c>).</summary>
    public IReadOnlySet<string> MaterialKeywords { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>
    /// Gets the include search directories after the shader file's own directory, in priority order.
    /// The first entry is normally the compatibility include folder; the Unity project root follows.
    /// </summary>
    public required IReadOnlyList<string> IncludeDirectories { get; init; }

    /// <summary>Gets the directory for compiler job files. Each translation uses a new subdirectory.</summary>
    public required string WorkDirectory { get; init; }

    /// <summary>Gets a value that keeps job files after the translation, for inspection.</summary>
    public bool KeepWorkFiles { get; init; }
}
