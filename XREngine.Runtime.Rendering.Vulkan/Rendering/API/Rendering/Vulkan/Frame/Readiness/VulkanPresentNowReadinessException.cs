namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Reports a typed foreground-readiness failure and whether it invalidates the
/// current frame or the renderer as a whole.
/// </summary>
internal sealed class VulkanPresentNowReadinessException : InvalidOperationException
{
    internal VulkanPresentNowReadinessException(
        ulong frameId,
        EVulkanPresentNowReadinessStage stage,
        string activeTicket,
        string dependencyChain,
        TimeSpan elapsed,
        TimeSpan sinceLastProgress,
        string detail,
        Exception? innerException = null,
        EVulkanPresentNowFailureDisposition disposition =
            EVulkanPresentNowFailureDisposition.RendererTerminal)
        : base(
            $"Vulkan PresentNow readiness failed: frame={frameId} stage={stage} " +
            $"ticket='{activeTicket}' dependency='{dependencyChain}' " +
            $"elapsedMs={elapsed.TotalMilliseconds:F1} " +
            $"lastProgressMs={sinceLastProgress.TotalMilliseconds:F1}. {detail}",
            innerException)
    {
        FrameId = frameId;
        Stage = stage;
        ActiveTicket = activeTicket;
        DependencyChain = dependencyChain;
        Elapsed = elapsed;
        SinceLastProgress = sinceLastProgress;
        Disposition = disposition;
    }

    internal ulong FrameId { get; }
    internal EVulkanPresentNowReadinessStage Stage { get; }
    internal string ActiveTicket { get; }
    internal string DependencyChain { get; }
    internal TimeSpan Elapsed { get; }
    internal TimeSpan SinceLastProgress { get; }
    internal EVulkanPresentNowFailureDisposition Disposition { get; }
}
