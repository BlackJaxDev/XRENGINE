namespace XREngine.ShaderTranslation.Diagnostics;

/// <summary>
/// Reports one translation finding with its source location.
/// </summary>
/// <param name="Code">The stable diagnostic code, for example <c>UNITYSHADER0003</c>.</param>
/// <param name="Severity">The effect on the result.</param>
/// <param name="Message">The finding in plain words.</param>
/// <param name="SourcePath">The shader or include path, or null when the finding has no file.</param>
/// <param name="Line">The one-based source line, or zero when the finding has no line.</param>
/// <param name="Pass">The pass name or index text, or null for a shader-level finding.</param>
public sealed record ShaderTranslationDiagnostic(
    string Code,
    ShaderTranslationSeverity Severity,
    string Message,
    string? SourcePath = null,
    int Line = 0,
    string? Pass = null)
{
    /// <inheritdoc/>
    public override string ToString()
    {
        string location = SourcePath is null ? string.Empty : Line > 0 ? $"{SourcePath}({Line}): " : $"{SourcePath}: ";
        string pass = Pass is null ? string.Empty : $"[{Pass}] ";
        return $"{location}{Severity.ToString().ToLowerInvariant()} {Code}: {pass}{Message}";
    }
}
