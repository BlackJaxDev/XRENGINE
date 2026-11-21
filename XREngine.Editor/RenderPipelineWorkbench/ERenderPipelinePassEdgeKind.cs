namespace XREngine.Editor.RenderPipelineWorkbench;

/// <summary>
/// Classifies an edge in the Passes view.
/// </summary>
public enum ERenderPipelinePassEdgeKind
{
    /// <summary>The consumer reads a resource that the producer wrote. The view draws it solid.</summary>
    ReadAfterWrite,
    /// <summary>The consumer writes a resource that the producer read. The view draws it dashed.</summary>
    WriteAfterRead,
    /// <summary>Both passes write the same resource. The view draws it dashed.</summary>
    WriteAfterWrite,
    /// <summary>The consumer declares an explicit dependency with no shared resource. The view draws it dotted.</summary>
    Explicit,
}
