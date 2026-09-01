namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Describes an expected pre-acquire admission retry without allocating or
/// throwing an exception.
/// </summary>
internal readonly record struct VulkanPresentNowReadinessRetry(
    ulong FrameId,
    EVulkanPresentNowReadinessStage Stage,
    string ActiveTicket,
    string DependencyChain,
    TimeSpan Elapsed,
    TimeSpan SinceLastProgress,
    string Detail)
{
    internal bool IsValid => ActiveTicket is not null;
}
