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
    private readonly Lock _collectPublicationLock = new();
    private bool _collectPublicationOpen;
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
            TransformRecords = new Commands.TransformPublicationRecords(runtimeWorld.TransformHierarchy);
            runtimeWorld.TransformHierarchy.SetPublicationSink(TransformRecords);
            runtimeWorld.RuntimeWorldMatrixChangeQueued += OnRuntimeWorldMatrixChangeQueued;
            _renderWorldCapabilityLease = runtimeWorld.RegisterCapability<IRuntimeRenderWorld>(this);
            _renderRegistrationCapabilityLease = runtimeWorld.RegisterCapability<IRuntimeRenderInfo3DRegistrationTarget>(this);
            if (runtimeWorld.TryGetCapability<IRuntimeEditorSceneQuery>(out IRuntimeEditorSceneQuery? editorSceneQuery))
                EditorSceneQuery = editorSceneQuery;
        }
        RuntimeRenderWorldRegistry.Attach(this);
    }

    public Commands.TransformPublicationRecords? TransformRecords { get; }
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
    public ReadOnlySpan<SceneNode> RootNodeSnapshot
        => WorldContext is RuntimeWorld world ? world.RootNodes.Snapshot : [];
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

    /// <summary>
    /// Opens or closes collect-time publication for the owning host's render session.
    /// </summary>
    /// <remarks>
    /// The host opens publication only after the visual scene is initialized and its
    /// roots are active, and closes it before deactivating roots and tearing the scene
    /// down. Closing waits for any in-flight <see cref="GlobalPreCollectVisible"/> call,
    /// so teardown never overlaps collect-thread scene mutation. While closed, staged
    /// renderable and matrix changes stay queued for the next session.
    /// </remarks>
    public void SetCollectPublicationOpen(bool open)
    {
        lock (_collectPublicationLock)
            _collectPublicationOpen = open;
    }

    public void GlobalPreCollectVisible()
    {
        lock (_collectPublicationLock)
        {
            // Callers outside the host's timer subscription, such as XR runtimes that
            // republish late transforms before stereo collection, must not mutate a
            // scene whose session is closed.
            if (!_collectPublicationOpen)
                return;

            RuntimeEngine.Rendering.Stats.FrameOutputs.RecordSceneSnapshot();
            ApplyRenderMatrixChanges();
            RenderableMesh.ProcessPendingRenderMatrixUpdates();
            VisualScene.GlobalCollectVisible();
        }
    }

    public void GlobalCollectVisible() => Lights.CollectVisibleItems();

    /// <summary>
    /// Publishes world-owned render buffers using the ambient engine frame identity.
    /// This remains the normal window-driven swap path.
    /// </summary>
    public void GlobalSwapBuffers()
        => GlobalSwapBuffersCore(RuntimeEngine.Rendering.State.RenderFrameId, requireCanonicalFrameId: false);

    /// <summary>
    /// Publishes world-owned render buffers for an explicitly scheduled output frame.
    /// Manual output lifecycles use this before they enter <c>BeginRenderFrame</c>,
    /// when the ambient engine frame identity has not advanced yet.
    /// </summary>
    public void GlobalSwapBuffers(ulong canonicalFrameId)
    {
        if (canonicalFrameId == 0UL)
            throw new ArgumentOutOfRangeException(nameof(canonicalFrameId), "An explicit canonical frame ID must be nonzero.");

        GlobalSwapBuffersCore(canonicalFrameId, requireCanonicalFrameId: true);
    }

    private void GlobalSwapBuffersCore(ulong frameId, bool requireCanonicalFrameId)
    {
        ApplyRenderMatrixChanges();
        RenderableMesh.ProcessPendingRenderMatrixUpdates();
        // The Advanced global capture must observe the same published atlas and
        // last-rendered shadow snapshots consumed by the frame package.
        Lights.SwapBuffers();
        if (VisualScene.GPUCommands.AdvancedPublicationRequested)
        {
            AdvancedGlobalResourceCapture globalResources =
                AdvancedGlobalResourceCapture.Capture(frameId, this);
            if (requireCanonicalFrameId)
                VisualScene.GPUCommands.SetAdvancedGlobalResources(frameId, in globalResources);
            else
                VisualScene.GPUCommands.SetAdvancedGlobalResources(in globalResources);
        }
        VisualScene.GlobalSwapBuffers();
        RuntimeEngine.Rendering.Stats.SkinnedBounds.SwapSkinnedBoundsStats();
        RuntimeEngine.Rendering.Stats.Octree.SwapOctreeStats();
        RuntimeEngine.Rendering.Stats.RenderMatrix.SwapRenderMatrixStats();
    }

    public void GlobalPreRender()
    {
        Lights.PublishCompletedLightProbeOutputs();
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
        int applied = WorldContext is RuntimeWorld world ? world.TransformHierarchy.PublishRenderMatrices() : 0;
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
        {
            runtimeWorld.RuntimeWorldMatrixChangeQueued -= OnRuntimeWorldMatrixChangeQueued;
            runtimeWorld.TransformHierarchy.SetPublicationSink(null);
        }
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
