namespace XREngine.Rendering;

/// <summary>A problem at a location in a render pipeline script.</summary>
/// <param name="Severity">The severity.</param>
/// <param name="Line">The 1-based line, or 0 when the location is not known.</param>
/// <param name="Column">The 1-based column, or 0 when the location is not known.</param>
/// <param name="Message">The problem text, without the location.</param>
public readonly record struct RenderPipelineScriptProblem(
    ERenderPipelineScriptProblemSeverity Severity,
    int Line,
    int Column,
    string Message)
{
    /// <summary>Gets true for an error.</summary>
    public bool IsError => Severity == ERenderPipelineScriptProblemSeverity.Error;

    /// <inheritdoc />
    public override string ToString()
        => Line > 0 ? $"{Message} (line {Line}, column {Column})" : Message;
}
