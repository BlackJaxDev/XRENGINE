namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Native state sealed from prepared mesh draws immediately before a scheduled
/// secondary is selected for reuse. Missing prepared state is deliberately not
/// comparable so it cannot authorize reuse.
/// </summary>
internal readonly record struct VulkanPreparedCommandChainKey(
    ulong PipelineIdentity,
    ulong DescriptorSetIdentity,
    int DescriptorSetCount,
    RecordedPacketKey RecordedPacketKey,
    bool IsComplete)
{
    private readonly RecordedPacketKey _recordedPacketKey = RecordedPacketKey;

    public RecordedPacketKey RecordedPacketKey
    {
        get => _recordedPacketKey;
        init => _recordedPacketKey = value;
    }

    internal static ref readonly RecordedPacketKey GetRecordedPacketKeyReference(
        in VulkanPreparedCommandChainKey key)
        => ref key._recordedPacketKey;

    internal static readonly VulkanPreparedCommandChainKey Incomplete = default;

    internal bool Matches(in VulkanPreparedCommandChainKey other)
    {
        ref readonly RecordedPacketKey recordedPacketKey =
            ref GetRecordedPacketKeyReference(in this);
        ref readonly RecordedPacketKey otherRecordedPacketKey =
            ref GetRecordedPacketKeyReference(in other);
        return PipelineIdentity == other.PipelineIdentity &&
            DescriptorSetIdentity == other.DescriptorSetIdentity &&
            DescriptorSetCount == other.DescriptorSetCount &&
            IsComplete == other.IsComplete &&
            recordedPacketKey.Matches(in otherRecordedPacketKey);
    }
}
