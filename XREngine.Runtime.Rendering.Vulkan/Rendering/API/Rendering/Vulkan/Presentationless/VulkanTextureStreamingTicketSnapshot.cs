namespace XREngine.Rendering.Vulkan;

/// <summary>Terminal/readiness evidence for one real imported upload ticket.</summary>
public readonly record struct VulkanTextureStreamingTicketSnapshot(
    VulkanTextureStreamingUploadTicket Ticket,
    bool Found,
    bool Ready,
    bool TerminalFailure,
    bool TransferSubmitted,
    int ChunksSubmitted,
    int ChunksCompleted,
    string State,
    string? Detail);
