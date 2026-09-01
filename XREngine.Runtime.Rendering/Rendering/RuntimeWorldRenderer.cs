using System.Collections.Concurrent;
using System.Diagnostics;
using XREngine.Components.Scene.Mesh;
using XREngine.Data.Colors;
using XREngine.Data.Geometry;
using XREngine.Rendering.Info;
using XREngine.Rendering.Picking;
using XREngine.Rendering.Physics.DebugVisualization;
using XREngine.Scene.Physics;
using XREngine.Scene.Physics.DebugVisualization;
using XREngine.Scene.Transforms;

namespace XREngine.Rendering;

/// <summary>
/// Rendering composition for one backend-neutral runtime world.  This type owns
/// only visual publication and render-thread state; lifecycle, scene ownership,
/// ticks, and physics remain on <see cref="WorldContext"/>.
/// </summary>
public sealed partial class RuntimeWorldRenderer : IRuntimeRenderWorld, IRuntimeRenderInfo3DRegistrationTarget, IDisposable
{
    private readonly RuntimeWorldRenderState _state;
    private readonly ConcurrentQueue<(TransformBase Transform, Matrix4x4 Matrix)> _pendingMatrices = [];
    private PhysicsDebugFrameRenderer _physicsDebugRenderer = new();
    private long _nextEditPhysicsDebugCollectionTimestamp;
    private Func<object?>? _targetWorld;
    private Func<string?>? _targetWorldName;
    private Func<object?>? _gameMode;
    private Func<IReadOnlyList<SceneNode>>? _rootNodes;
    private bool _disposed;
    private readonly IDisposable? _renderWorldCapabilityLease;
    private readonly IDisposable? _renderRegistrationCapabilityLease;

    public RuntimeWorldRenderer(IRuntimeWorldContext worldContext, VisualScene3D visualScene)
    {
        WorldContext = worldContext ?? throw new ArgumentNullException(nameof(worldContext));
        _state = new RuntimeWorldRenderState(this, visualScene ?? throw new ArgumentNullException(nameof(visualScene)));
        if (WorldContext is RuntimeWorld runtimeWorld)
        {
            runtimeWorld.RuntimeWorldMatrixChangeQueued += OnRuntimeWorldMatrixChangeQueued;
            _renderWorldCapabilityLease = runtimeWorld.RegisterCapability<IRuntimeRenderWorld>(this);
            _renderRegistrationCapabilityLease = runtimeWorld.RegisterCapability<IRuntimeRenderInfo3DRegistrationTarget>(this);
            if (runtimeWorld.TryGetCapability<IRuntimeEditorSceneQuery>(out IRuntimeEditorSceneQuery? editorSceneQuery))
                EditorSceneQuery = editorSceneQuery;
        }
        RuntimeRenderWorldRegistry.Attach(this);
    }

    public IRuntimeWorldContext WorldContext { get; }
    public VisualScene3D VisualScene => _state.VisualScene;
    public Lights3DCollection Lights => _state.Lights;
    public EventList<CameraComponent> FramebufferCameras { get; } = [];
    public PhysicsDebugFrameRenderer PhysicsDebugRenderer => _physicsDebugRenderer;
    public IRuntimeEditorSceneQuery? EditorSceneQuery { get; set; }
    public object? TargetWorldObject => _targetWorld?.Invoke();
    public string? TargetWorldName => _targetWorldName?.Invoke();
    public object? GameModeObject => _gameMode?.Invoke();
    public IRuntimeAmbientSettings? AmbientSettings => _state.AmbientSettings;
    public IReadOnlyList<SceneNode> RootNodes => _rootNodes?.Invoke() ?? [];
    public bool PreviewOctrees => GetSettings()?.PreviewOctrees ?? false;
    public bool PreviewQuadtrees => GetSettings()?.PreviewQuadtrees ?? false;

    /// <summary>Configures read-only Core-owned context used by rendering diagnostics.</summary>
    public void BindWorldState(
        Func<object?> targetWorld,
        Func<string?> targetWorldName,
        Func<object?> gameMode,
        Func<IReadOnlyList<SceneNode>> rootNodes)
    {
        _targetWorld = targetWorld ?? throw new ArgumentNullException(nameof(targetWorld));
        _targetWorldName = targetWorldName ?? throw new ArgumentNullException(nameof(targetWorldName));
        _gameMode = gameMode ?? throw new ArgumentNullException(nameof(gameMode));
        _rootNodes = rootNodes ?? throw new ArgumentNullException(nameof(rootNodes));
    }

    public void BindSettings(WorldSettings? settings)
    {
        _state.BindSettings(settings);
        if (settings is not null)
            VisualScene.SetBounds(settings.Bounds);
    }

    public void AddRenderable3D(IRuntimeRenderInfo3DRegistrationItem renderable)
        => _state.AddRenderable(renderable);

    public void RemoveRenderable3D(IRuntimeRenderInfo3DRegistrationItem renderable)
        => _state.RemoveRenderable(renderable);

    public void AddWorldObject(RuntimeWorldObjectBase worldObject) => _state.AddWorldObject(worldObject);
    public void RemoveWorldObject(RuntimeWorldObjectBase worldObject) => _state.RemoveWorldObject(worldObject);

    public void EnqueueRenderTransformChange(TransformBase transform, Matrix4x4 worldMatrix)
    {
        ArgumentNullException.ThrowIfNull(transform);
        if (transform.ShouldEnqueueRenderMatrix(worldMatrix))
            _pendingMatrices.Enqueue((transform, worldMatrix));
    }

    public void GlobalPreCollectVisible()
    {
        RuntimeEngine.Rendering.Stats.FrameOutputs.RecordSceneSnapshot();
        ApplyRenderMatrixChanges();
        RenderableMesh.ProcessPendingRenderMatrixUpdates();
        VisualScene.GlobalCollectVisible();
    }

    public void GlobalCollectVisible() => Lights.CollectVisibleItems();

    public void GlobalSwapBuffers()
    {
        ApplyRenderMatrixChanges();
        RenderableMesh.ProcessPendingRenderMatrixUpdates();
        if (VisualScene.GPUCommands.AdvancedPublicationRequested)
        {
            VisualScene.GPUCommands.SetAdvancedGlobalResources(
                AdvancedGlobalResourceCapture.Capture(
                    RuntimeEngine.Rendering.State.RenderFrameId,
                    this));
        }
        VisualScene.GlobalSwapBuffers();
        RuntimeEngine.Rendering.Stats.SkinnedBounds.SwapSkinnedBoundsStats();
        RuntimeEngine.Rendering.Stats.Octree.SwapOctreeStats();
        Lights.SwapBuffers();
        RuntimeEngine.Rendering.Stats.RenderMatrix.SwapRenderMatrixStats();
    }

    public void GlobalPreRender()
    {
        VisualScene.GlobalPreRender();
        Lights.RenderShadowMaps(false);
    }

    public void GlobalPostRender() => VisualScene.GlobalPostRender();

    public void ApplyRenderDispatchPreference(bool useGpu) => VisualScene.ApplyRenderDispatchPreference(useGpu);

    public void ApplyCpuSceneCullingStructurePreference(ECpuSceneCullingStructure structure)
        => VisualScene.ApplyCpuSceneCullingStructurePreference(structure);

    public void DebugRenderPhysics(PhysicsDebugDepthMode depthMode)
    {
        if (WorldContext is not IRuntimePhysicsWorldContext physicsWorld)
            return;

        if (depthMode == PhysicsDebugDepthMode.DepthTested && RuntimeEngine.Rendering.State.RenderingCamera is { } camera)
            physicsWorld.PhysicsScene.IncludeDebugRenderViewBounds(camera.WorldFrustum().GetAABB(false));

        if (depthMode == PhysicsDebugDepthMode.DepthTested && !physicsWorld.PhysicsEnabled)
        {
            long now = Stopwatch.GetTimestamp();
            if (now >= _nextEditPhysicsDebugCollectionTimestamp)
            {
                _nextEditPhysicsDebugCollectionTimestamp = now + Stopwatch.Frequency / 30;
                physicsWorld.PhysicsScene.DebugRenderCollect();
            }
        }

        _physicsDebugRenderer.Render(physicsWorld.PhysicsScene.DebugFrames, depthMode);
    }

    /// <summary>
    /// Releases debug-frame GPU resources tied to a destroyed physics scene and
    /// prepares a fresh renderer for the next edit/play lifecycle.
    /// </summary>
    public void ResetPhysicsDebugRenderer()
    {
        _physicsDebugRenderer.Dispose();
        _physicsDebugRenderer = new PhysicsDebugFrameRenderer();
        _nextEditPhysicsDebugCollectionTimestamp = 0;
    }

    public bool IsInEditorScene(SceneNode? node) => EditorSceneQuery?.IsInEditorScene(node) ?? false;

    public void RaycastOctreeAsync(CameraComponent cameraComponent, Vector2 normalizedScreenPoint,
        SortedDictionary<float, List<(RenderInfo3D item, object? data)>> orderedResults,
        Action<SortedDictionary<float, List<(RenderInfo3D item, object? data)>>> finishedCallback,
        ERaycastHitMode hitMode = ERaycastHitMode.Faces, bool useUnjitteredProjection = false)
        => RaycastOctreeAsync(cameraComponent.Camera.GetWorldSegment(normalizedScreenPoint, useUnjitteredProjection), orderedResults, finishedCallback, hitMode);

    public void RaycastOctreeAsync(Segment worldSegment,
        SortedDictionary<float, List<(RenderInfo3D item, object? data)>> orderedResults,
        Action<SortedDictionary<float, List<(RenderInfo3D item, object? data)>>> finishedCallback,
        ERaycastHitMode hitMode = ERaycastHitMode.Faces)
        => VisualScene.RaycastAsync(worldSegment, orderedResults, (item, segment) => DirectItemTest(item, segment, hitMode, GpuMeshBvhPickingEnabled), finishedCallback);

    public ColorF3 GetEffectiveAmbientColor()
        => GetSettings()?.GetEffectiveAmbientColor() ?? new ColorF3(0.03f, 0.03f, 0.03f);

    private WorldSettings? GetSettings() => TargetWorldObject is XRWorld world ? world.Settings : null;

    private void ApplyRenderMatrixChanges()
    {
        int applied = 0;
        while (_pendingMatrices.TryDequeue(out (TransformBase Transform, Matrix4x4 Matrix) item))
        {
            item.Transform.SetRenderMatrix(item.Matrix, false);
            ++applied;
        }
        RuntimeEngine.Rendering.Stats.RenderMatrix.RecordRenderMatrixApplied(applied);
    }

    private void OnRuntimeWorldMatrixChangeQueued(RuntimeWorldObjectBase worldObject, Matrix4x4 worldMatrix)
    {
        if (worldObject is TransformBase transform)
            EnqueueRenderTransformChange(transform, worldMatrix);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (WorldContext is RuntimeWorld runtimeWorld)
            runtimeWorld.RuntimeWorldMatrixChangeQueued -= OnRuntimeWorldMatrixChangeQueued;
        _renderRegistrationCapabilityLease?.Dispose();
        _renderWorldCapabilityLease?.Dispose();
        RuntimeRenderWorldRegistry.Detach(WorldContext, out _);
        // Publication producers release on the authoring thread; accepted frames
        // independently retain any immutable shadow bytes they still consume.
        if (RuntimeEngine.IsRenderThread)
            Lights.Clear();
        else
            RuntimeEngine.EnqueueRenderThreadTask(Lights.Clear, "RuntimeWorldRenderer.ClearLights", RenderThreadJobKind.RenderPipelineResource);
        _physicsDebugRenderer.Dispose();
    }
}

/// <summary>Editor-owned hidden-scene policy consumed by rendering without an Editor dependency.</summary>
public interface IRuntimeEditorSceneQuery
{
    bool IsInEditorScene(SceneNode? node);
}
