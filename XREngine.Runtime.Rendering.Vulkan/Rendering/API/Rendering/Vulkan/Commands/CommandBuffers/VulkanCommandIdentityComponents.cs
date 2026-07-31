namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Shared identity vocabulary for primary plans, secondary artifacts, and
/// nested primary-to-secondary references.
/// </summary>
internal readonly record struct VulkanCommandIdentityComponents(
    ulong OrderedNodes,
    ulong ResourceGenerations,
    ulong RenderScopeInheritance,
    ulong QueueAssumptions,
    ulong NestedArtifacts,
    ulong PrimaryOnly,
    ulong SecondaryOnly,
    ulong DataContent)
{
    internal ulong Combined
    {
        get
        {
            FrameOpSignatureHasher identity = new();
            AddTo(ref identity);
            return identity.ToHash();
        }
    }

    internal void AddTo(ref FrameOpSignatureHasher identity)
    {
        identity.Add(0x564B434944434F4DUL);
        identity.Add(OrderedNodes);
        identity.Add(ResourceGenerations);
        identity.Add(RenderScopeInheritance);
        identity.Add(QueueAssumptions);
        identity.Add(NestedArtifacts);
        identity.Add(PrimaryOnly);
        identity.Add(SecondaryOnly);
        identity.Add(DataContent);
    }

    internal VulkanCommandIdentityMismatch Compare(
        in VulkanCommandIdentityComponents current)
    {
        if (OrderedNodes != current.OrderedNodes)
            return Mismatch(EVulkanCommandIdentityComponent.OrderedNodes, OrderedNodes, current.OrderedNodes);
        if (ResourceGenerations != current.ResourceGenerations)
            return Mismatch(EVulkanCommandIdentityComponent.ResourceGenerations, ResourceGenerations, current.ResourceGenerations);
        if (RenderScopeInheritance != current.RenderScopeInheritance)
            return Mismatch(EVulkanCommandIdentityComponent.RenderScopeInheritance, RenderScopeInheritance, current.RenderScopeInheritance);
        if (QueueAssumptions != current.QueueAssumptions)
            return Mismatch(EVulkanCommandIdentityComponent.QueueAssumptions, QueueAssumptions, current.QueueAssumptions);
        if (NestedArtifacts != current.NestedArtifacts)
            return Mismatch(EVulkanCommandIdentityComponent.NestedArtifacts, NestedArtifacts, current.NestedArtifacts);
        if (PrimaryOnly != current.PrimaryOnly)
            return Mismatch(EVulkanCommandIdentityComponent.PrimaryOnly, PrimaryOnly, current.PrimaryOnly);
        if (SecondaryOnly != current.SecondaryOnly)
            return Mismatch(EVulkanCommandIdentityComponent.SecondaryOnly, SecondaryOnly, current.SecondaryOnly);
        if (DataContent != current.DataContent)
            return Mismatch(EVulkanCommandIdentityComponent.DataContent, DataContent, current.DataContent);
        return VulkanCommandIdentityMismatch.None;
    }

    private static VulkanCommandIdentityMismatch Mismatch(
        EVulkanCommandIdentityComponent component,
        ulong recorded,
        ulong current)
        => new(component, recorded, current);
}
