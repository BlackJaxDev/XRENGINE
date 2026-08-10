namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Immutable command-chain input published before a serial or worker encoder
/// begins recording. Mutable <see cref="CommandChain"/> state remains an output
/// channel for lifecycle publication and is not the authority for these inputs.
/// </summary>
internal readonly record struct VulkanPreparedCommandChain(
    CommandChainKey Key,
    int SourceStartIndex,
    int SourceCount,
    int PreparedDrawStartIndex,
    int PacketIndex,
    ulong PreparedFrameGeneration,
    VulkanPreparedCommandChainAuthority Authority,
    VulkanRecordedCommandInheritance Inheritance,
    VulkanRecordedCommandArtifactReference WritableArtifact,
    EVulkanCommandChainWorkerEligibility WorkerEligibility)
{
    /// <summary>
    /// Verifies that the mutable lifecycle owner still represents the exact
    /// artifact lease and dependency identity frozen for this encoding job.
    /// </summary>
    internal bool Matches(CommandChain chain, RenderPacket packet)
    {
        ref readonly VulkanPreparedCommandChainKey authorityKey = ref Authority.PreparedKey;
        ref readonly VulkanPreparedCommandChainKey chainPreparedKey =
            ref chain.PreparedKeyReference;
        return chain.Key == Key &&
            chain.SourceStartIndex == SourceStartIndex &&
            chain.SourceCount == SourceCount &&
            ReferenceEquals(chain.PacketSnapshot, packet) &&
            chainPreparedKey.Matches(in authorityKey) &&
            chain.RecordedArtifact.Generation ==
                WritableArtifact.ArtifactGeneration &&
            chain.RecordedArtifact.NativeBuffer.Handle ==
                WritableArtifact.NativeBuffer.Handle;
    }
}
