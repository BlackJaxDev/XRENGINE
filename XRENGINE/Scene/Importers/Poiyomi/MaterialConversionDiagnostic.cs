namespace XREngine.Scene.Importers.Poiyomi;

/// <summary>
/// A stable, actionable diagnostic emitted while recognizing or converting a source material.
/// </summary>
/// <param name="Code">Stable diagnostic identifier.</param>
/// <param name="Severity">Diagnostic severity.</param>
/// <param name="Message">Human-readable explanation and remediation.</param>
/// <param name="SourceProperty">Optional source property associated with the diagnostic.</param>
public sealed record MaterialConversionDiagnostic(
    string Code,
    MaterialConversionDiagnosticSeverity Severity,
    string Message,
    string? SourceProperty = null)
{
    /// <inheritdoc />
    public override string ToString()
        => SourceProperty is null
            ? $"[{Code}] {Message}"
            : $"[{Code}] {SourceProperty}: {Message}";
}
