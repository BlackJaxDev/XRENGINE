namespace XREngine.Rendering;

/// <summary>
/// Identifies the backend-neutral semantic family of a render query.
/// </summary>
public enum ERenderQueryKind
{
    Occlusion,
    Timestamp,
    ElapsedTime,
    PipelineStatistics,
    TransformFeedback,
    PrimitivesGenerated,
    MeshPrimitivesGenerated,
    AccelerationStructureProperty,
    MicromapProperty,
    PerformanceCounter,
    VideoResultStatus,
}
