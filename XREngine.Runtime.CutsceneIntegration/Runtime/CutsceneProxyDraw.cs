using XREngine.Components.Scene.Mesh;
using XREngine.Rendering;

namespace XREngine.Cutscenes;

/// <summary>
/// One T0 draw of a cutscene open: a renderable mesh of the bound closure and the material of its T0 draw (the material
/// override, or the material of the lowest LOD).
/// </summary>
/// <param name="Renderable">The renderable mesh.</param>
/// <param name="Material">The material of the T0 draw.</param>
internal readonly record struct CutsceneProxyDraw(RenderableMesh Renderable, XRMaterial Material)
{
    /// <summary>
    /// The renderer of the T0 draw: the renderer of the lowest LOD while it is resident, otherwise the current LOD renderer
    /// of a renderable with no LOD table, or null.
    /// </summary>
    public XRMeshRenderer? ResolveRenderer()
        => Renderable.LowestLodRenderer ?? (Renderable.LodTableCount == 0 ? Renderable.CurrentLODRenderer : null);
}
