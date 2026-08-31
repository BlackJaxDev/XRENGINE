namespace XREngine.Rendering.Vulkan;

/// <summary>Durable state for one exact imported-texture upload ticket.</summary>
internal sealed class VulkanTextureUploadGenerationEntry
{
    internal VulkanTextureUploadTicket Ticket;
    internal long StreamingGeneration;
    internal TextureUploadPriorityClass PriorityClass;
    internal VulkanTextureUploadGenerationState State;
    internal string? Detail;
    internal int PinCount;
    // Bounded ledger retention lets diagnostics report physical progress after
    // a ticket leaves the active queue.
    internal int SubmittedChunks;
    internal int CompletedChunks;

    internal bool IsTerminal =>
        State is VulkanTextureUploadGenerationState.Published or
            VulkanTextureUploadGenerationState.Retired or
            VulkanTextureUploadGenerationState.Canceled or
            VulkanTextureUploadGenerationState.Failed;
}
