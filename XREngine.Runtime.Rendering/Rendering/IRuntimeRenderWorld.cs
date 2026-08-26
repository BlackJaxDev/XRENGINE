using System.Numerics;
using XREngine.Components;
using XREngine.Data.Colors;
using XREngine.Data.Core;
using XREngine.Data.Geometry;
using XREngine.Rendering.Info;
using XREngine.Rendering.Picking;
using XREngine.Scene;
using XREngine.Scene.Physics.DebugVisualization;

namespace XREngine.Rendering;

/// <summary>
/// Rendering capability attached to a core world context.
///
/// A render world deliberately is not a world context itself: scene nodes retain the
/// backend-neutral <see cref="IRuntimeWorldContext"/> identity supplied by Runtime.Core
/// and rendering resolves this capability through <see cref="RuntimeRenderWorldRegistry"/>.
/// </summary>
public interface IRuntimeRenderWorld
{
    /// <summary>
    /// The backend-neutral identity this rendering capability is attached to.
    /// The default preserves source compatibility for the legacy facade while it
    /// is being removed; new implementations must provide a concrete context.
    /// </summary>
    IRuntimeWorldContext WorldContext
        => throw new NotSupportedException("Legacy render-world facades do not expose a Core world context.");
    object? TargetWorldObject { get; }
    string? TargetWorldName { get; }
    object? GameModeObject { get; }
    IRuntimeAmbientSettings? AmbientSettings { get; }
    bool PreviewOctrees { get; }
    bool PreviewQuadtrees { get; }
    bool GpuMeshBvhPickingEnabled { get; set; }
    IReadOnlyList<SceneNode> RootNodes { get; }
    VisualScene3D VisualScene { get; }
    Lights3DCollection Lights { get; }
    EventList<CameraComponent> FramebufferCameras { get; }
    ColorF3 GetEffectiveAmbientColor();
    void ApplyRenderDispatchPreference(bool useGpu);
    void ApplyCpuSceneCullingStructurePreference(ECpuSceneCullingStructure structure);
    void GlobalPreCollectVisible();
    void GlobalPreRender();
    void GlobalPostRender();
    void DebugRenderPhysics(PhysicsDebugDepthMode depthMode);
    bool IsInEditorScene(SceneNode? node);
    void RaycastOctreeAsync(
        CameraComponent cameraComponent,
        Vector2 normalizedScreenPoint,
        SortedDictionary<float, List<(RenderInfo3D item, object? data)>> orderedResults,
        Action<SortedDictionary<float, List<(RenderInfo3D item, object? data)>>> finishedCallback,
        ERaycastHitMode hitMode = ERaycastHitMode.Faces,
        bool useUnjitteredProjection = false);
    void RaycastOctreeAsync(
        Segment worldSegment,
        SortedDictionary<float, List<(RenderInfo3D item, object? data)>> orderedResults,
        Action<SortedDictionary<float, List<(RenderInfo3D item, object? data)>>> finishedCallback,
        ERaycastHitMode hitMode = ERaycastHitMode.Faces);
}
