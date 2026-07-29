namespace XREngine.Rendering.Vulkan;

internal sealed class CommandChainQueueSchedule(
    bool multiQueueEnabled,
    bool singleQueueFallbackAvailable,
    ReadOnlyMemory<CommandChainQueueNode> nodes,
    ReadOnlyMemory<CommandChainQueueDependency> dependencies,
    string diagnostics)
{
    public bool MultiQueueEnabled { get; } = multiQueueEnabled;
    public bool SingleQueueFallbackAvailable { get; } = singleQueueFallbackAvailable;
    public ReadOnlyMemory<CommandChainQueueNode> Nodes { get; } = nodes;
    public ReadOnlyMemory<CommandChainQueueDependency> Dependencies { get; } = dependencies;
    public string Diagnostics { get; } = diagnostics;
}
