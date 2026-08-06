namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Immutable post-binding authority for one prepared secondary artifact. The
/// reference is small enough for frame-local arrays while its exact packet key
/// may contain large fixed-capacity native identity buffers.
/// </summary>
internal sealed class VulkanPreparedCommandChainAuthority
{
    internal VulkanPreparedCommandChainAuthority(in VulkanPreparedCommandChainKey preparedKey)
    {
        if (!preparedKey.IsComplete || !preparedKey.RecordedPacketKey.IsComplete)
            throw new ArgumentException("Prepared command-chain authority requires a complete exact native key.", nameof(preparedKey));

        PreparedKey = preparedKey;
    }

    internal VulkanPreparedCommandChainKey PreparedKey { get; }
}
