namespace XREngine.Rendering;

public enum RendererReloadState
{
    Idle,
    BuildPending,
    ReplacementRequested,
    Quiescing,
    DrainingGpu,
    DestroyingWrappers,
    CleaningBackend,
    UnloadingModule,
    LoadingCandidate,
    InitializingCandidate,
    RehydratingResources,
    AwaitingFirstValidFrame,
    Resuming,
    Failed,
    RollingBack,
    FailedStopped,
}

