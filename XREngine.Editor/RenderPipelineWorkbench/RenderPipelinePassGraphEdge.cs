namespace XREngine.Editor.RenderPipelineWorkbench;

/// <summary>
/// One cached edge of the Passes view.
/// </summary>
/// <param name="FromNode">The index of the producer node in <see cref="RenderPipelinePassGraphLayout.Nodes"/>.</param>
/// <param name="ToNode">The index of the consumer node.</param>
/// <param name="Kind">The hazard or dependency kind.</param>
/// <param name="ResourceName">The shared resource, or an empty string for an explicit dependency.</param>
public readonly record struct RenderPipelinePassGraphEdge(
    int FromNode,
    int ToNode,
    ERenderPipelinePassEdgeKind Kind,
    string ResourceName);
