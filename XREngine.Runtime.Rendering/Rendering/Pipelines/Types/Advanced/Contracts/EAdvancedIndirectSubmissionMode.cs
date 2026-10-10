namespace XREngine.Rendering;

/// <summary>
/// Indirect geometry submission encoding selected for the advanced pipeline.
/// </summary>
public enum EAdvancedIndirectSubmissionMode
{
    None = 0,
    MultiDrawIndirect,
    MultiDrawIndirectCount,
    MeshTasksIndirectCount,
    /// <summary>GPU triangle compaction and zero-first-instance vertex-pulled indirect draws.</summary>
    GpuVertexPullIndirect,
}
