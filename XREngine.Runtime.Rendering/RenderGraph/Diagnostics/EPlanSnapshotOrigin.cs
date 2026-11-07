namespace XREngine.Rendering.RenderGraph.Diagnostics;

/// <summary>Tells which source produced a <see cref="RenderGraphPlanSnapshot"/>.</summary>
public enum EPlanSnapshotOrigin
{
    /// <summary>The backend-neutral compiler planned the metadata with no renderer. The snapshot has no physical data.</summary>
    OfflineCompile,
    /// <summary>A Vulkan renderer published the plan that it records with, including barriers and memory aliasing.</summary>
    Vulkan,
    /// <summary>An OpenGL renderer published the authored execution sequence. OpenGL runs passes in that order and has no barriers.</summary>
    OpenGL,
}
