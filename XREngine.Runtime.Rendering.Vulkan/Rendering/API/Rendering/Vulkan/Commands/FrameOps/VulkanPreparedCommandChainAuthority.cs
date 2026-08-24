namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Immutable post-binding authority for one prepared secondary artifact. The
/// reference is small enough for frame-local arrays while its exact packet key
/// may contain large fixed-capacity native identity buffers.
/// </summary>
internal sealed class VulkanPreparedCommandChainAuthority
{
    private readonly VulkanPreparedCommandChainKey _preparedKey;

    internal VulkanPreparedCommandChainAuthority(in VulkanPreparedCommandChainKey preparedKey)
    {
        ref readonly RecordedPacketKey recordedPacketKey =
            ref VulkanPreparedCommandChainKey.GetRecordedPacketKeyReference(in preparedKey);
        if (!preparedKey.IsComplete || !recordedPacketKey.IsComplete)
            throw new ArgumentException("Prepared command-chain authority requires a complete exact native key.", nameof(preparedKey));

        _preparedKey = preparedKey;
    }

    internal ref readonly VulkanPreparedCommandChainKey PreparedKey
        => ref _preparedKey;
}
