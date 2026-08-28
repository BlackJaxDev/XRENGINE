namespace XREngine.Rendering.Vulkan;

/// <summary>Fail-closed reasons for the Vulkan set-1 visibility producer.</summary>
internal enum EVulkanAdvancedVisibilityResourceFailure : byte
{
    None,
    RuntimeUnavailable,
    InvalidFrameOwner,
    InvalidPreparation,
    CapacityExceeded,
    TransactionIntegrityFailure,
    FrameSlotStillInUse,
    NativeFault,
}
