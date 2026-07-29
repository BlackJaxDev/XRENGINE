namespace XREngine.Rendering;

/// <summary>
/// Allocation-free current/previous slot rotation and GPU-completion reuse rules.
/// </summary>
public static class AdvancedFrameSlotContract
{
    /// <summary>
    /// Two slots are the minimum required to keep current writes separate from previous reads.
    /// </summary>
    public const uint MinimumSlotCount = 2u;

    /// <summary>
    /// Three slots are the default so ordinary double-buffered presentation does not force
    /// the CPU to wait on the immediately preceding frame.
    /// </summary>
    public const uint DefaultSlotCount = 3u;

    /// <summary>
    /// Resolves the writable current slot and read-only previous slot for a frame ordinal.
    /// </summary>
    public static AdvancedFrameSlotPair Resolve(ulong frameOrdinal, uint slotCount)
    {
        ValidateSlotCount(slotCount);
        uint current = (uint)(frameOrdinal % slotCount);
        uint previous = (current + slotCount - 1u) % slotCount;
        return new AdvancedFrameSlotPair(current, previous);
    }

    /// <summary>
    /// A slot is reusable only when it has never been submitted or its last completion
    /// value is at or below the backend's completed fence/timeline value.
    /// </summary>
    public static bool CanReuse(ulong lastSubmittedCompletionValue, ulong completedValue)
        => lastSubmittedCompletionValue == 0UL ||
           completedValue >= lastSubmittedCompletionValue;

    /// <summary>
    /// Selects the completion primitive without changing the logical slot contract.
    /// </summary>
    public static EAdvancedFrameSlotCompletionMode ResolveCompletionMode(
        in AdvancedRenderPipelineCapabilities capabilities)
    {
        if (!capabilities.SupportsFrameSlotStorage)
            return EAdvancedFrameSlotCompletionMode.None;

        return capabilities.Backend switch
        {
            RuntimeGraphicsApiKind.OpenGL
                when capabilities.Synchronization ==
                     EAdvancedSynchronizationMode.OpenGlMemoryBarrier
                => EAdvancedFrameSlotCompletionMode.OpenGlFence,
            RuntimeGraphicsApiKind.Vulkan
                when capabilities.SupportsTimelineSemaphores
                => EAdvancedFrameSlotCompletionMode.VulkanTimelineSemaphore,
            RuntimeGraphicsApiKind.Vulkan
                when capabilities.Synchronization is
                    EAdvancedSynchronizationMode.VulkanLegacyBarriers or
                    EAdvancedSynchronizationMode.VulkanSynchronization2
                => EAdvancedFrameSlotCompletionMode.VulkanFence,
            _ => EAdvancedFrameSlotCompletionMode.None,
        };
    }

    /// <summary>
    /// Rejects slot counts that would alias current writes with previous reads.
    /// </summary>
    public static void ValidateSlotCount(uint slotCount)
    {
        if (slotCount < MinimumSlotCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(slotCount),
                slotCount,
                $"Advanced rendering requires at least {MinimumSlotCount} frame slots.");
        }
    }
}
