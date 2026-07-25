namespace XREngine.Rendering;

public enum RendererReloadFailureKind
{
    None,
    Build,
    Staging,
    ModuleValidation,
    Teardown,
    Unload,
    CandidateInitialization,
    FirstFrame,
    Rollback,
    Cancelled,
    ReloadBoundary,
}

