namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Renderer-neutral comparison record emitted by direct and CPU-built indirect
/// parity paths. It is evidence-only and cannot select a fallback strategy.
/// </summary>
internal readonly record struct VulkanDirectIndirectParityRecord(
    VulkanResidentDrawTemplateHandle Template,
    uint IndexCount,
    uint InstanceCount,
    uint FirstIndex,
    int VertexOffset,
    uint FirstInstance,
    uint MaterialIndex,
    uint ObjectIndex,
    ulong SubmissionOrder)
{
    internal bool Matches(in VulkanDirectIndirectParityRecord other)
        => Template == other.Template &&
        IndexCount == other.IndexCount &&
        InstanceCount == other.InstanceCount &&
        FirstIndex == other.FirstIndex &&
        VertexOffset == other.VertexOffset &&
        FirstInstance == other.FirstInstance &&
        MaterialIndex == other.MaterialIndex &&
        ObjectIndex == other.ObjectIndex &&
        SubmissionOrder == other.SubmissionOrder;
}
