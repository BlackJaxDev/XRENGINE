namespace XREngine;

/// <summary>
/// Bounded GPU visibility resources for one in-flight frame slot. Mutable
/// counts and command contents are deliberately excluded from structural
/// identity so recorded dispatch ranges remain reusable.
/// </summary>
public readonly record struct GpuMaskedVisibilityFrameSlot(
    uint FrameSlot,
    uint CandidateCapacity,
    uint ViewCapacity,
    ulong TopologyGeneration,
    ulong BindingGeneration,
    ulong ResourceGeneration,
    ulong CandidateBufferKey,
    ulong ExactMaskBufferKey,
    ulong CommandBufferKey,
    ulong CountBufferKey,
    ulong OverflowBufferKey)
{
    public ulong StructuralIdentity
    {
        get
        {
            HashCode hash = new();
            hash.Add(FrameSlot);
            hash.Add(CandidateCapacity);
            hash.Add(ViewCapacity);
            hash.Add(TopologyGeneration);
            hash.Add(BindingGeneration);
            hash.Add(ResourceGeneration);
            hash.Add(CandidateBufferKey);
            hash.Add(ExactMaskBufferKey);
            hash.Add(CommandBufferKey);
            hash.Add(CountBufferKey);
            hash.Add(OverflowBufferKey);
            return unchecked((ulong)hash.ToHashCode());
        }
    }
}
