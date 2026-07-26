namespace XREngine.Rendering;

public sealed record RendererReplacementResult(
    bool Succeeded,
    RendererBackendRegistration ActiveRegistration,
    RendererReloadFailureKind FailureKind = RendererReloadFailureKind.None,
    string? Error = null,
    bool RolledBack = false);

