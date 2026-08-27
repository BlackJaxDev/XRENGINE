namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Whether the descriptor lifetime ledger is known to match the native set.
/// Unknown sets are quarantined until the owning descriptor set is recreated.
/// </summary>
internal enum EVulkanDescriptorNativePublicationState
{
    Known,
    Unknown,
}
