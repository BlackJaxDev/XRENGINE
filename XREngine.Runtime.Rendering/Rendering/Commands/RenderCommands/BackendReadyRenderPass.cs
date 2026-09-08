namespace XREngine.Rendering.Commands;

/// <summary>
/// Prepared pass membership and dependency summary published to the render
/// consumer without another scene or material traversal.
/// </summary>
public readonly record struct BackendReadyRenderPass(
    int PassIndex,
    int CommandCount,
    int MeshCommandCount,
    uint SceneColorSnapshotConsumerCount,
    ulong CommandSetSignature,
    ulong DependencySignature,
    IReadOnlyCollection<RenderCommand> Commands);
