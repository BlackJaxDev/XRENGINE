namespace XREngine.Rendering;

/// <summary>
/// Backend-neutral render API diagnostic surfaced to tooling.
/// </summary>
public sealed record RenderBackendDiagnosticError(
    RendererBackendId BackendId,
    int? Id,
    int Count,
    string Severity,
    string Type,
    string Source,
    DateTime LastSeenUtc,
    string Message);
