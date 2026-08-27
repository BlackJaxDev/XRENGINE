namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Exactly-once diagnostic published when a genuine permanent PresentNow
/// readiness failure changes renderer state from accepting to paused.
/// </summary>
internal readonly record struct VulkanPresentNowTerminalTransitionRecord(
    long TransitionId,
    long Timestamp,
    ulong FrameId,
    int FrameSlot,
    ulong AcceptedSceneEpoch,
    ulong OutputGeneration,
    EVulkanPresentNowReadinessStage ReadinessStage,
    string ActiveTicket,
    string DependencyChain,
    TimeSpan Elapsed,
    TimeSpan SinceLastProgress,
    EVulkanPresentNowFailureDisposition Disposition,
    int MeshRequestCount,
    int RequiredTextureCount,
    int RequiredUploadCount,
    bool ImageAcquired,
    bool Submitted,
    bool PresentDispatched,
    long ForegroundEpoch,
    long BackgroundYieldCount,
    long BackgroundResumeCount,
    string FailureType,
    string Detail)
{
    internal bool IsValid =>
        TransitionId > 0L &&
        FrameId > 0UL &&
        Disposition == EVulkanPresentNowFailureDisposition.RendererTerminal;
}
