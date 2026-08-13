using XREngine.Rendering.Profiling;

namespace XREngine.Runtime.Automation.Profiling;

/// <summary>Non-blocking status for a bounded profile matrix.</summary>
public sealed record RenderProfileMatrixStatus(
    string JobId,
    RenderProfileState State,
    int CompletedVariants,
    int TotalVariants,
    IReadOnlyList<string> SessionIds,
    string? Error);
