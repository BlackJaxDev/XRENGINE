namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Stable, renderer-facing projection of the latest terminal PresentNow
/// readiness transition. String identities keep the diagnostic public without
/// exposing frame-loop policy enums as part of the backend API.
/// </summary>
public readonly record struct VulkanPresentNowTerminalDiagnostic(
    long TransitionId,
    ulong FrameId,
    int FrameSlot,
    ulong AcceptedSceneEpoch,
    ulong OutputGeneration,
    string ReadinessStage,
    string ActiveTicket,
    string DependencyChain,
    string Disposition,
    double ElapsedMilliseconds,
    double SinceLastProgressMilliseconds,
    int MeshRequestCount,
    string FailureType,
    string Detail)
{
    /// <summary>Whether a terminal transition has been published.</summary>
    public bool IsValid => TransitionId != 0;
}
