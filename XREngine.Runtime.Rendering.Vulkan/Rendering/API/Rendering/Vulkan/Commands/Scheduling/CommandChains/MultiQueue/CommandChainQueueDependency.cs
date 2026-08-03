namespace XREngine.Rendering.Vulkan;

internal readonly record struct CommandChainQueueDependency(
    int SourceNodeIndex,
    int DestinationNodeIndex,
    ulong TimelineSignalValue,
    bool RequiresQueueFamilyOwnershipTransfer);
