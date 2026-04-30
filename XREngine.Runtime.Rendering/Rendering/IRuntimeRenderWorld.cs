using System.Numerics;
using XREngine.Components;
using XREngine.Data.Colors;
using XREngine.Data.Core;
using XREngine.Data.Geometry;
using XREngine.Rendering.Info;
using XREngine.Rendering.Picking;
using XREngine.Scene;

namespace XREngine.Rendering;

public interface IRuntimeRenderWorld : IRuntimeWorldContext
{
    object? TargetWorldObject { get; }
    string? TargetWorldName { get; }
    object? GameModeObject { get; }
    IRuntimeAmbientSettings? AmbientSettings { get; }
    IReadOnlyList<SceneNode> RootNodes { get; }
    VisualScene3D VisualScene { get; }
    Lights3DCollection Lights { get; }
    EventList<CameraComponent> FramebufferCameras { get; }
    ColorF3 GetEffectiveAmbientColor();
    void GlobalPreRender();
    void GlobalPostRender();
    void DebugRenderPhysics();
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

public interface IRuntimeAmbientSettings
{
    ColorF3 AmbientLightColor { get; set; }
    float AmbientLightIntensity { get; set; }
}