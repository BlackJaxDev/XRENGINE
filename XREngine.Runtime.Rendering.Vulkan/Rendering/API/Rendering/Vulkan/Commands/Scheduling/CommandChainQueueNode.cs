namespace XREngine.Rendering.Vulkan;

internal sealed class CommandChainQueueNode(
    CommandChainQueueKind queueKind,
    CommandChainQueueEligibility eligibility,
    ReadOnlyMemory<int> groupIndices,
    ulong timelineWaitValue,
    ulong timelineSignalValue,
    string diagnosticLabel)
{
    public CommandChainQueueKind QueueKind { get; } = queueKind;
    public CommandChainQueueEligibility Eligibility { get; } = eligibility;
    public ReadOnlyMemory<int> GroupIndices { get; } = groupIndices;
    public ulong TimelineWaitValue { get; } = timelineWaitValue;
    public ulong TimelineSignalValue { get; } = timelineSignalValue;
    public string DiagnosticLabel { get; } = diagnosticLabel;
}
