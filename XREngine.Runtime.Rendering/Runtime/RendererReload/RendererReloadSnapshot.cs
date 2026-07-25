namespace XREngine.Rendering;

/// <summary>
/// Immutable cold-path status displayed by editor tooling.
/// </summary>
public sealed record RendererReloadSnapshot(
    RendererBackendId BackendId,
    long Generation,
    RendererReloadState State,
    RendererReloadFailureKind FailureKind,
    string Status,
    string? LastError,
    DateTimeOffset UpdatedAt,
    long SuccessfulReloads,
    long FailedReloads,
    long LastGoodRollbacks,
    long UnloadLeaks,
    IReadOnlyDictionary<string, TimeSpan> PhaseDurations)
{
    public static RendererReloadSnapshot Idle { get; } = new(
        default,
        0,
        RendererReloadState.Idle,
        RendererReloadFailureKind.None,
        "Renderer reload is idle.",
        null,
        DateTimeOffset.UtcNow,
        0,
        0,
        0,
        0,
        new Dictionary<string, TimeSpan>());
}

