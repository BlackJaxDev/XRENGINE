namespace XREngine.Components;

/// <summary>
/// Reports one chain's renderer bindings and captured frame submission counts.
/// Frame counts include the full scene. They do not read GPU buffers.
/// </summary>
public readonly record struct PhysicsChainRenderingDiagnostics(
    int BoundRendererCount,
    int FrameVisibleRendererCount,
    uint FrameAggregateDeformationDispatchCount,
    int FrameLegacySkinningDispatchCount,
    uint FramePreparedIndirectCommandCount,
    bool HasSubmittedIndirectDrawCount,
    long FrameSubmittedIndirectDrawCount,
    int FrameIndirectDrawCallCount,
    int FrameDrawCallCount,
    long FrameCpuVisibleTriangleCount,
    string? ConservativeBoundsStatus,
    string? LastGlobalGpuError);
