using System.Numerics;
using XREngine.Components;
using XREngine.Data.Colors;
using XREngine.Data.Core;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Rendering.Info;
using XREngine.Rendering.Picking;
using XREngine.Scene;
using XREngine.Scene.Physics.DebugVisualization;

namespace XREngine.Rendering.Tools;

/// <summary>
/// Owns capture-only scene publications without registering temporary geometry in the source world.
/// Lighting and ambient resources remain borrowed from that world for the duration of the capture.
/// </summary>
internal sealed class SceneCaptureWorld : IRuntimeRenderWorld, IDisposable
{
    private readonly IRuntimeRenderWorld _source;
    private readonly RenderInfo3D[] _renderables;
    private bool _disposed;

    internal SceneCaptureWorld(IRuntimeRenderWorld source, RenderInfo3D[] renderables, AABB bounds)
    {
        _source = source;
        _renderables = renderables;
        VisualScene = new VisualScene3D();
        try
        {
            VisualScene.SetBounds(bounds);
            VisualScene.Initialize();
            foreach (RenderInfo3D renderable in renderables)
                VisualScene.AddRenderable(renderable);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public IRuntimeWorldContext WorldContext => _source.WorldContext;
    public object? TargetWorldObject => _source.TargetWorldObject;
    public string? TargetWorldName => _source.TargetWorldName;
    public object? GameModeObject => _source.GameModeObject;
    public IRuntimeAmbientSettings? AmbientSettings => _source.AmbientSettings;
    public bool PreviewOctrees => false;
    public bool PreviewQuadtrees => false;
    public bool GpuMeshBvhPickingEnabled { get; set; }
    public IReadOnlyList<SceneNode> RootNodes => [];
    public ReadOnlySpan<SceneNode> RootNodeSnapshot => [];
    public VisualScene3D VisualScene { get; }
    public Lights3DCollection Lights => _source.Lights;
    public EventList<CameraComponent> FramebufferCameras { get; } = [];
    internal SceneCaptureLightingSnapshot? Lighting { get; set; }
    public ColorF3 GetEffectiveAmbientColor() => _source.GetEffectiveAmbientColor();
    public void ApplyRenderDispatchPreference(bool useGpu) => VisualScene.ApplyRenderDispatchPreference(useGpu);
    public void ApplyCpuSceneCullingStructurePreference(ECpuSceneCullingStructure structure)
        => VisualScene.ApplyCpuSceneCullingStructurePreference(structure);
    public void GlobalPreCollectVisible() => VisualScene.GlobalCollectVisible();
    public void GlobalPreRender() { }
    public void GlobalPostRender() { }
    public void DebugRenderPhysics(PhysicsDebugDepthMode depthMode) { }
    public bool IsInEditorScene(SceneNode? node) => false;

    internal void Publish(ulong frameId)
    {
        if (VisualScene.GPUCommands.AdvancedPublicationRequested)
        {
            AdvancedGlobalResourceCapture globals = AdvancedGlobalResourceCapture.Capture(frameId, this, includeShadows: false);
            if (Lighting is { } lighting) globals = lighting.ApplyTo(in globals);
            VisualScene.GPUCommands.SetAdvancedGlobalResources(frameId, in globals);
        }
        VisualScene.GPUCommands.SetMeshSubmissionFrameId(frameId);
        VisualScene.GlobalSwapBuffers();
    }

    public void RaycastOctreeAsync(CameraComponent camera, Vector2 point,
        SortedDictionary<float, List<(RenderInfo3D item, object? data)>> results,
        Action<SortedDictionary<float, List<(RenderInfo3D item, object? data)>>> completed,
        ERaycastHitMode hitMode = ERaycastHitMode.Faces, bool useUnjitteredProjection = false)
        => throw new NotSupportedException("SceneCapture.PickingUnsupported: capture geometry is private to its output.");

    public void RaycastOctreeAsync(Segment segment,
        SortedDictionary<float, List<(RenderInfo3D item, object? data)>> results,
        Action<SortedDictionary<float, List<(RenderInfo3D item, object? data)>>> completed,
        ERaycastHitMode hitMode = ERaycastHitMode.Faces)
        => throw new NotSupportedException("SceneCapture.PickingUnsupported: capture geometry is private to its output.");

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        List<Exception>? failures = null;
        void Release(Action release)
        {
            try { release(); }
            catch (Exception error) { (failures ??= []).Add(error); }
        }
        foreach (RenderInfo3D renderable in _renderables)
            Release(() => VisualScene.RemoveRenderable(renderable));
        Release(VisualScene.GlobalCollectVisible);
        Release(VisualScene.Destroy);
        Release(() => FramebufferCameras.Destroy(true));
        if (failures is not null) throw new AggregateException("Capture world cleanup failed.", failures);
    }
}
