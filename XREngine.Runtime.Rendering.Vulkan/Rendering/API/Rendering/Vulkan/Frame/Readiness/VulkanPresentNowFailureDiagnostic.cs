namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Stable renderer-facing projection of the latest PresentNow readiness
/// rejection, including retryable failures that do not pause the renderer.
/// </summary>
public readonly record struct VulkanPresentNowFailureDiagnostic(
    long Sequence,
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
    /// <summary>Whether a readiness failure has been observed.</summary>
    public bool IsValid => Sequence != 0;
}
