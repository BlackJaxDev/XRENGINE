namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Names one independently diagnosable component of a recorded-command
/// identity.
/// </summary>
internal enum EVulkanCommandIdentityComponent : byte
{
    None = 0,
    OrderedNodes,
    ResourceGenerations,
    RenderScopeInheritance,
    QueueAssumptions,
    NestedArtifacts,
    PrimaryOnly,
    SecondaryOnly,
    DataContent,
}
