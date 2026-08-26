using System.Collections.Concurrent;
using System.Numerics;
using XREngine.Components;
using XREngine.Data.Core;
using XREngine.Data.Geometry;
using XREngine.Scene;
using XREngine.Scene.Physics;
using XREngine.Scene.Transforms;

namespace XREngine;

/// <summary>
/// The backend-neutral runtime identity assigned to every node in one live
/// world.  Rendering, input, editor and bootstrap hosts compose their focused
/// capabilities onto this context rather than owning a second world facade.
/// </summary>
public sealed partial class RuntimeWorld : IRuntimeWorldContext, IRuntimePhysicsWorldContext, IRuntimeWorldCapabilityProvider, IDisposable
{
    private readonly ConcurrentDictionary<Type, object> _capabilities = [];
    private readonly HashSet<XRScene> _loadedScenes = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<XRScene, HashSet<SceneNode>> _visibleRootsByScene = new(ReferenceEqualityComparer.Instance);
    private readonly ConcurrentQueue<PhysicsRaycastRequest> _pendingPhysicsRaycasts = new();
    private readonly ConcurrentQueue<PhysicsRaycastRequest> _physicsRaycastRequestPool = new();
    private readonly ConcurrentQueue<IAbstractDynamicRigidBody> _pendingMinYPlaneResetRequests = new();
    private readonly ConcurrentDictionary<IAbstractDynamicRigidBody, byte> _pendingMinYPlaneResetRequestSet =
        new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<IAbstractDynamicRigidBody, PhysicsResetState> _initialDynamicBodyPoses =
        new(ReferenceEqualityComparer.Instance);
    private readonly RuntimeWorldLifecycle _lifecycle;
    private bool _physicsEnabled;
    private bool _physicsResetCacheValid;
    private XRWorld? _targetWorld;
    private IRuntimeWorldScenePolicy? _scenePolicy;
    private GameMode? _gameMode;
    private bool _disposed;

    public RuntimeWorld(AbstractPhysicsScene physicsScene, XRWorld? targetWorld = null)
    {
        PhysicsScene = physicsScene ?? throw new ArgumentNullException(nameof(physicsScene));
        _lifecycle = new RuntimeWorldLifecycle(this, OnRootNodeDestroying, ShouldParticipateInPlay);
        RetargetWorld(targetWorld);
    }

    /// <summary>The source world asset represented by this live runtime context.</summary>
    public XRWorld? TargetWorld => _targetWorld;

    /// <summary>
    /// Changes the serialized world represented by this live context. Bootstrap
    /// is the sole caller so it can rekey its host registry atomically.
    /// </summary>
    internal void RetargetWorld(XRWorld? targetWorld, Action? afterTargetAssigned = null)
    {
        ThrowIfDisposed();
        if (ReferenceEquals(_targetWorld, targetWorld))
            return;

        UnloadTargetWorld();
        _targetWorld = targetWorld;
        afterTargetAssigned?.Invoke();
        if (_targetWorld is not null)
            foreach (XRScene scene in _targetWorld.Scenes)
                LoadScene(scene);
    }

    public AbstractPhysicsScene PhysicsScene { get; }
    public RootNodeCollection RootNodes => _lifecycle.RootNodes;
    public RuntimeWorldPlayState PlayState
    {
        get => _lifecycle.PlayState;
        private set => _lifecycle.PlayState = value;
    }

    /// <summary>
    /// Optional host policy for roots that belong to editor or other
    /// host-specific scenes.  Assign this before beginning a play session.
    /// </summary>
    public IRuntimeWorldScenePolicy? ScenePolicy
    {
        get => _scenePolicy;
        set
        {
            ThrowIfDisposed();
            if (ReferenceEquals(_scenePolicy, value))
                return;
            if (PlayState != RuntimeWorldPlayState.Stopped)
            {
                throw new InvalidOperationException(
                    "A runtime world scene policy cannot be replaced during an active play or edit session.");
            }

            // Reconcile host-owned roots against the new routing policy.
            foreach (XRScene scene in _loadedScenes.ToArray())
                if (scene.IsVisible)
                    UnloadVisibleScene(scene);

            _scenePolicy = value;
            foreach (XRScene scene in _loadedScenes)
                if (scene.IsVisible)
                    LoadVisibleScene(scene);
        }
    }

    public bool IsPlaySessionActive => _lifecycle.IsPlaySessionActive;
    public bool TransitioningPlay => _lifecycle.TransitioningPlay;
    public bool PhysicsEnabled
    {
        get => _physicsEnabled;
        set
        {
            if (_physicsEnabled == value)
                return;

            _physicsEnabled = value;
            PhysicsEnabledChanged?.Invoke(value);
        }
    }

    public float PhysicsResetMinYDistance => TargetWorld?.Settings.PhysicsResetMinYDist ?? 0.0f;
    public Vector3 PhysicsGravity => PhysicsScene.Gravity;

    public event Action<bool>? PhysicsEnabledChanged;
    public event Action<RuntimeWorld>? PreBeginPlay;
    public event Action<RuntimeWorld>? PostBeginPlay;
    public event Action<RuntimeWorld>? PreEndPlay;
    public event Action<RuntimeWorld>? PostEndPlay;
    /// <summary>
    /// Raised while the world is still valid, before scenes, capabilities, and
    /// roots are released. Optional composition layers use this to detach
    /// deterministic lifetime state that must not outlive the Core world.
    /// </summary>
    public event Action<RuntimeWorld>? Disposing;
    public event Action<GameMode?>? CurrentGameModeChanged;
    public event Action<RuntimeWorldObjectBase>? DirtyRuntimeObjectQueued;
    public event Action<RuntimeWorldObjectBase, Matrix4x4>? RuntimeWorldMatrixChangeQueued;

    /// <summary>Current game-mode instance selected by the bootstrap host.</summary>
    public GameMode? GameMode
    {
        get => _gameMode;
        set
        {
            ThrowIfDisposed();
            if (ReferenceEquals(_gameMode, value))
                return;

            if (_gameMode is not null && ReferenceEquals(_gameMode.WorldInstance, this))
                _gameMode.WorldInstance = null;
            _gameMode = value;
            if (_gameMode is not null)
                _gameMode.WorldInstance = this;
            CurrentGameModeChanged?.Invoke(_gameMode);
        }
    }

    /// <summary>
    /// Registers one host-owned service capability. The returned lease removes
    /// only this exact instance, preventing a stale host from detaching a
    /// replacement capability.
    /// </summary>
    public IDisposable RegisterCapability<TCapability>(TCapability capability)
        where TCapability : class
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(capability);
        if (!_capabilities.TryAdd(typeof(TCapability), capability))
            throw new InvalidOperationException($"A live {typeof(TCapability).Name} capability is already registered.");
        return new CapabilityLease(this, typeof(TCapability), capability);
    }

    public bool TryGetCapability<TCapability>(out TCapability? capability)
        where TCapability : class
    {
        if (_capabilities.TryGetValue(typeof(TCapability), out object? exact)
            && exact is TCapability typed)
        {
            capability = typed;
            return true;
        }

        foreach (object attached in _capabilities.Values)
        {
            if (attached is TCapability assignable)
            {
                capability = assignable;
                return true;
            }
        }

        capability = null;
        return false;
    }

    public void RegisterTick(ETickGroup group, int order, WorldTick tick)
        => _lifecycle.RegisterTick(group, order, tick);

    public void UnregisterTick(ETickGroup group, int order, WorldTick tick)
        => _lifecycle.UnregisterTick(group, order, tick);

    public void TickGroup(ETickGroup group)
        => _lifecycle.TickGroup(group);

    public void PausePlay()
    {
        if (PlayState == RuntimeWorldPlayState.Playing)
            PlayState = RuntimeWorldPlayState.Paused;
    }

    public void ResumePlay()
    {
        if (PlayState == RuntimeWorldPlayState.Paused)
            PlayState = RuntimeWorldPlayState.Playing;
    }

    /// <summary>
    /// Begins the Core lifecycle after composition hosts have initialized their
    /// own capabilities.  Physics initialization remains explicit so hosts can
    /// schedule it on the backend's required thread.
    /// </summary>
    public async Task BeginPlayAsync(
        Func<Task>? beforeNodeActivation = null,
        Func<Task>? afterNodeActivation = null,
        ELoopType childRecalculationLoopType = ELoopType.Sequential)
    {
        ThrowIfDisposed();
        if (PlayState is RuntimeWorldPlayState.BeginningPlay or RuntimeWorldPlayState.Playing or RuntimeWorldPlayState.Paused)
            return;

        PlayState = RuntimeWorldPlayState.BeginningPlay;
        PreBeginPlay?.Invoke(this);
        if (beforeNodeActivation is not null)
            await beforeNodeActivation();

        _physicsResetCacheValid = false;
        ClearPendingPhysicsRequests();

        SceneNode[] roots = [.. RootNodes];
        foreach (SceneNode node in roots)
            await node.Transform.RecalculateMatrixHierarchy(true, true, childRecalculationLoopType);

        foreach (SceneNode node in roots)
            if (ShouldParticipateInPlay(node))
                node.OnBeginPlay();
        foreach (SceneNode node in roots)
            if (ShouldParticipateInPlay(node) && node.IsActiveSelf)
                node.OnActivated();

        if (afterNodeActivation is not null)
            await afterNodeActivation();
        PostBeginPlay?.Invoke(this);
        PlayState = RuntimeWorldPlayState.Playing;
    }

    public void EndPlay(
        Action? afterNodeDeactivation = null,
        Action? afterPersistentRootReactivation = null)
    {
        ThrowIfDisposed();
        if (PlayState == RuntimeWorldPlayState.Stopped)
            return;

        PlayState = RuntimeWorldPlayState.EndingPlay;
        PreEndPlay?.Invoke(this);
        foreach (SceneNode node in RootNodes.ToArray())
        {
            if (ShouldParticipateInPlay(node) && node.HasBegunPlay)
                node.OnEndPlay();
        }

        // Deactivate every active root, including host-persistent editor roots,
        // so render/input registrations are released before backend teardown.
        foreach (SceneNode node in RootNodes.ToArray())
            if (node.IsActiveSelf)
                node.OnDeactivated();

        afterNodeDeactivation?.Invoke();
        foreach (SceneNode node in RootNodes.ToArray())
            if (node.IsActiveSelf && !ShouldParticipateInPlay(node))
                node.OnActivated();
        afterPersistentRootReactivation?.Invoke();
        PhysicsEnabled = false;
        _physicsResetCacheValid = false;
        _initialDynamicBodyPoses.Clear();
        _invalidTransforms.Clear();
        Volatile.Write(ref _dirtyMinDepth, int.MaxValue);
        Volatile.Write(ref _dirtyMaxDepth, int.MinValue);
        ClearPendingPhysicsRequests();
        PostEndPlay?.Invoke(this);
        PlayState = RuntimeWorldPlayState.Stopped;
    }

    public void FixedUpdate()
    {
        ThrowIfDisposed();
        TickGroup(ETickGroup.PrePhysics);
        if (PhysicsEnabled)
        {
            PhysicsScene.StepSimulation();
            ProcessPhysicsMinYPlaneResetRequests();
            ProcessQueuedPhysicsRaycasts();
        }
        TickGroup(ETickGroup.DuringPhysics);
        TickGroup(ETickGroup.PostPhysics);
    }

    public void EnqueuePhysicsResetFromMinYPlane(IAbstractDynamicRigidBody body)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(body);
        if (PhysicsEnabled && _pendingMinYPlaneResetRequestSet.TryAdd(body, 0))
            _pendingMinYPlaneResetRequests.Enqueue(body);
    }

    public void CapturePhysicsResetInitialPoses()
    {
        ThrowIfDisposed();
        _initialDynamicBodyPoses.Clear();
        foreach (SceneNode root in RootNodes)
        {
            root.IterateHierarchy(node =>
            {
                lock (node.Components)
                {
                    foreach (XRComponent component in node.Components)
                    {
                        if (component is not IRuntimeDynamicRigidBodyComponent bodyComponent
                            || bodyComponent.RigidBody is not IAbstractDynamicRigidBody body)
                            continue;

                        _initialDynamicBodyPoses[body] = new PhysicsResetState(bodyComponent, body.Transform);
                    }
                }
            });
        }

        _physicsResetCacheValid = true;
    }

    public void RaycastPhysicsAsync(
        Segment worldSegment,
        LayerMask layerMask,
        AbstractPhysicsScene.IAbstractQueryFilter? filter,
        SortedDictionary<float, List<(XRComponent? Item, object? Data)>> orderedResults,
        Action<SortedDictionary<float, List<(XRComponent? Item, object? Data)>>>? finishedCallback)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(orderedResults);
        orderedResults.Clear();
        if (!_physicsRaycastRequestPool.TryDequeue(out PhysicsRaycastRequest? request))
            request = new PhysicsRaycastRequest();

        request.Set(worldSegment, layerMask, filter, orderedResults, finishedCallback);
        _pendingPhysicsRaycasts.Enqueue(request);
    }

    void IRuntimeWorldContext.AddDirtyRuntimeObject(RuntimeWorldObjectBase worldObject)
    {
        if (worldObject is TransformBase transform)
            AddDirtyTransform(transform);
        DirtyRuntimeObjectQueued?.Invoke(worldObject);
    }

    void IRuntimeWorldContext.EnqueueRuntimeWorldMatrixChange(RuntimeWorldObjectBase worldObject, Matrix4x4 worldMatrix)
        => RuntimeWorldMatrixChangeQueued?.Invoke(worldObject, worldMatrix);

    /// <summary>Loads a scene and observes its visibility until it is unloaded.</summary>
    public void LoadScene(XRScene scene)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(scene);
        if (!_loadedScenes.Add(scene))
            return;

        scene.PropertyChanged += ScenePropertyChanged;
        if (scene.IsVisible)
            LoadVisibleScene(scene);
    }

    /// <summary>Stops observing a scene and removes its currently visible roots.</summary>
    public void UnloadScene(XRScene scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        if (!_loadedScenes.Remove(scene))
            return;

        scene.PropertyChanged -= ScenePropertyChanged;
        UnloadVisibleScene(scene);
    }

    private void OnRootNodeDestroying(SceneNode node)
    {
        _scenePolicy?.OnRootNodeDestroying(this, node);
        _lifecycle.RootNodes.RemoveDuringNodeDestroy(node);
        foreach (XRScene scene in _loadedScenes)
        {
            scene.RootNodes.Remove(node);
            if (_visibleRootsByScene.TryGetValue(scene, out HashSet<SceneNode>? roots))
                roots.Remove(node);
        }
    }

    private bool ShouldParticipateInPlay(SceneNode root)
        => _scenePolicy?.ShouldParticipateInPlay(this, root) ?? true;

    private void ScenePropertyChanged(object? sender, IXRPropertyChangedEventArgs args)
    {
        if (sender is not XRScene scene || args.PropertyName != nameof(XRScene.IsVisible) || !_loadedScenes.Contains(scene))
            return;

        if (scene.IsVisible)
            LoadVisibleScene(scene);
        else
            UnloadVisibleScene(scene);
    }

    private void LoadVisibleScene(XRScene scene)
    {
        if (!_visibleRootsByScene.TryGetValue(scene, out HashSet<SceneNode>? roots))
            _visibleRootsByScene[scene] = roots = new HashSet<SceneNode>(ReferenceEqualityComparer.Instance);

        foreach (SceneNode node in scene.RootNodes)
        {
            if (node is null || !roots.Add(node))
                continue;

            if (_scenePolicy?.TryAttachSceneRoot(this, scene, node) == true)
                continue;

            node.SetWorldContext(this);
            if (!RootNodes.Any(existing => ReferenceEquals(existing, node)))
                RootNodes.Add(node);
        }
    }

    private void UnloadVisibleScene(XRScene scene)
    {
        if (!_visibleRootsByScene.Remove(scene, out HashSet<SceneNode>? roots))
            return;

        foreach (SceneNode node in roots)
        {
            if (_scenePolicy?.TryDetachSceneRoot(this, scene, node) == true)
                continue;
            if (IsVisibleRootOfAnotherLoadedScene(scene, node))
                continue;

            RootNodes.Remove(node);
        }
    }

    private bool IsVisibleRootOfAnotherLoadedScene(XRScene excluded, SceneNode node)
        => _visibleRootsByScene.Any(pair => !ReferenceEquals(pair.Key, excluded) && pair.Value.Contains(node));

    private void UnloadTargetWorld()
    {
        foreach (XRScene scene in _loadedScenes.ToArray())
            UnloadScene(scene);
    }

    private void ProcessPhysicsMinYPlaneResetRequests()
    {
        if (_pendingMinYPlaneResetRequests.IsEmpty)
            return;

        if (PhysicsResetMinYDistance > 0.0f && !_physicsResetCacheValid)
            CapturePhysicsResetInitialPoses();

        while (_pendingMinYPlaneResetRequests.TryDequeue(out IAbstractDynamicRigidBody? body))
        {
            _pendingMinYPlaneResetRequestSet.TryRemove(body, out _);
            if (PhysicsResetMinYDistance <= 0.0f || !_initialDynamicBodyPoses.TryGetValue(body, out PhysicsResetState? resetState))
                continue;

            body.SetTransform(resetState.NativePose.Position, resetState.NativePose.Rotation, wake: true);
            resetState.Component.SynchronizeSceneTransform(resetState.NativePose.Position, resetState.NativePose.Rotation);
            resetState.Component.KinematicTarget = null;
            resetState.Component.LinearVelocity = Vector3.Zero;
            resetState.Component.AngularVelocity = Vector3.Zero;
            body.SetLinearVelocity(Vector3.Zero, wake: true);
            body.SetAngularVelocity(Vector3.Zero, wake: true);
            body.KinematicTarget = null;
        }
    }

    private void ProcessQueuedPhysicsRaycasts()
    {
        while (_pendingPhysicsRaycasts.TryDequeue(out PhysicsRaycastRequest? request))
        {
            try
            {
                request.Results.Clear();
                PhysicsScene.RaycastSingleAsync(request.Segment, request.LayerMask, request.Filter, request.Results, static _ => { });
                request.FinishedCallback?.Invoke(request.Results);
            }
            finally
            {
                request.Clear();
                _physicsRaycastRequestPool.Enqueue(request);
            }
        }
    }

    private void ClearPendingPhysicsRequests()
    {
        _pendingMinYPlaneResetRequests.Clear();
        _pendingMinYPlaneResetRequestSet.Clear();
        while (_pendingPhysicsRaycasts.TryDequeue(out PhysicsRaycastRequest? request))
        {
            request.Clear();
            _physicsRaycastRequestPool.Enqueue(request);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        if (PlayState != RuntimeWorldPlayState.Stopped)
            EndPlay();
        Disposing?.Invoke(this);
        Disposing = null;
        UnloadTargetWorld();
        _targetWorld = null;
        _initialDynamicBodyPoses.Clear();
        ClearPendingPhysicsRequests();
        while (_physicsRaycastRequestPool.TryDequeue(out _))
        {
        }
        _capabilities.Clear();
        _scenePolicy = null;
        GameMode = null;
        _disposed = true;
    }

    private void RemoveCapability(Type type, object capability)
    {
        if (_capabilities.TryGetValue(type, out object? attached) && ReferenceEquals(attached, capability))
            _capabilities.TryRemove(type, out _);
    }

    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(_disposed, this);

    private sealed class CapabilityLease(RuntimeWorld world, Type type, object capability) : IDisposable
    {
        private RuntimeWorld? _world = world;
        private readonly Type _type = type;
        private readonly object _capability = capability;

        public void Dispose()
        {
            RuntimeWorld? world = Interlocked.Exchange(ref _world, null);
            world?.RemoveCapability(_type, _capability);
        }
    }

    private sealed class PhysicsRaycastRequest
    {
        public Segment Segment;
        public LayerMask LayerMask;
        public AbstractPhysicsScene.IAbstractQueryFilter? Filter;
        public SortedDictionary<float, List<(XRComponent? Item, object? Data)>> Results = null!;
        public Action<SortedDictionary<float, List<(XRComponent? Item, object? Data)>>>? FinishedCallback;

        public void Set(Segment segment, LayerMask layerMask, AbstractPhysicsScene.IAbstractQueryFilter? filter,
            SortedDictionary<float, List<(XRComponent? Item, object? Data)>> results,
            Action<SortedDictionary<float, List<(XRComponent? Item, object? Data)>>>? finishedCallback)
        {
            Segment = segment;
            LayerMask = layerMask;
            Filter = filter;
            Results = results;
            FinishedCallback = finishedCallback;
        }

        public void Clear()
        {
            Segment = default;
            LayerMask = default;
            Filter = null;
            Results = null!;
            FinishedCallback = null;
        }
    }

    /// <summary>
    /// Couples a native body to the component that owns its visible scene
    /// transform. A min-Y reset must restore both sides in the same fixed step.
    /// </summary>
    private sealed record PhysicsResetState(
        IRuntimeDynamicRigidBodyComponent Component,
        (Vector3 Position, Quaternion Rotation) NativePose);
}
