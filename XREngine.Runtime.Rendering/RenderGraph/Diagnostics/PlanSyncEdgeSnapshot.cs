namespace XREngine.Rendering.RenderGraph.Diagnostics;

/// <summary>One logical synchronization edge between two passes.</summary>
/// <param name="ProducerPassIndex">The pass that writes the resource first.</param>
/// <param name="ConsumerPassIndex">The pass that waits for the producer.</param>
/// <param name="ResourceName">The logical resource name, or an empty string for an explicit dependency.</param>
/// <param name="DependencyOnly">Whether the edge orders the passes with no resource hazard.</param>
/// <param name="ProducerState">The stage, access, and layout of the producer.</param>
/// <param name="ConsumerState">The stage, access, and layout of the consumer.</param>
public sealed record PlanSyncEdgeSnapshot(
    int ProducerPassIndex,
    int ConsumerPassIndex,
    string ResourceName,
    bool DependencyOnly,
    RenderGraphSyncState ProducerState,
    RenderGraphSyncState ConsumerState);
