using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.CompilerServices;
using Extensions;
using XREngine.Audio;
using XREngine.Components;
using XREngine.Components.Animation;
using XREngine.Components.Scene.Mesh;
using XREngine.Data.Core;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Data.Trees;
using XREngine.Rendering.Info;
using XREngine.Rendering.Picking;
using XREngine.Scene;
using XREngine.Scene.Prefabs;
using XREngine.Scene.Transforms;
using static XREngine.Engine;

namespace XREngine.Rendering
{
    /// <summary>
    /// This class handles all information pertaining to the rendering of a world.
    /// This object is assigned to a window and the window's renderer is responsible for applying the world's render data to the rendering API for that window.
    /// </summary>
    public partial class XRWorldInstance : XRObjectBase
    {
        private const float EdgeBarycentricThreshold = 0.12f;
        private const float VertexBarycentricThreshold = 0.08f;

        public static Dictionary<XRWorld, XRWorldInstance> WorldInstances { get; } = [];

        public EventList<ListenerContext> Listeners { get; private set; } = [];
        
        public XREventGroup<GameMode> CurrentGameModeChanged;
        public XREvent<XRWorldInstance>? PreBeginPlay;
        public XREvent<XRWorldInstance>? PostBeginPlay;
        public XREvent<XRWorldInstance>? PreEndPlay;
        public XREvent<XRWorldInstance>? PostEndPlay;

        protected VisualScene3D _visualScene;
        public VisualScene3D VisualScene => _visualScene;

        protected AbstractPhysicsScene _physicsScene;
        public AbstractPhysicsScene PhysicsScene => _physicsScene;

        protected RootNodeCollection _rootNodes = [];
        public RootNodeCollection RootNodes => _rootNodes;
        public Lights3DCollection Lights { get; }

        // Physics raycasts must run on the fixed-update (physics) thread.
        private readonly ConcurrentQueue<PhysicsRaycastRequest> _pendingPhysicsRaycasts = new();
        private readonly ConcurrentQueue<PhysicsRaycastRequest> _physicsRaycastRequestPool = new();

        private sealed class PhysicsRaycastRequest
        {
            public Segment Segment;
            public LayerMask LayerMask;
            public AbstractPhysicsScene.IAbstractQueryFilter? Filter;
            public SortedDictionary<float, List<(XRComponent? item, object? data)>> Results = null!;
            public Action<SortedDictionary<float, List<(XRComponent? item, object? data)>>>? FinishedCallback;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void Set(
                Segment segment,
                LayerMask layerMask,
                AbstractPhysicsScene.IAbstractQueryFilter? filter,
                SortedDictionary<float, List<(XRComponent? item, object? data)>> results,
                Action<SortedDictionary<float, List<(XRComponent? item, object? data)>>>? finishedCallback)
            {
                Segment = segment;
                LayerMask = layerMask;
                Filter = filter;
                Results = results;
                FinishedCallback = finishedCallback;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void Clear()
            {
                Results = null!;
                Filter = null;
                FinishedCallback = null;
                Segment = default;
                LayerMask = default;
            }
        }

        #region Prefab instancing

        public SceneNode InstantiatePrefab(XRPrefabSource prefab,
                                           SceneNode? parent = null,
                                           bool maintainWorldTransform = false,
                                           bool addToWorldRootWhenNoParent = true)
        {
            ArgumentNullException.ThrowIfNull(prefab);
            var instance = SceneNodePrefabService.Instantiate(prefab, this, parent, maintainWorldTransform);
            FinalizePrefabSpawn(instance, parent, addToWorldRootWhenNoParent);
            return instance;
        }

        public SceneNode? InstantiatePrefab(Guid prefabAssetId,
                                             SceneNode? parent = null,
                                             bool maintainWorldTransform = false,
                                             bool addToWorldRootWhenNoParent = true)
        {
            if (prefabAssetId == Guid.Empty)
                return null;

            var assets = Engine.Assets;
            if (assets is null)
                return null;

            SceneNode? instance = assets.InstantiatePrefab(prefabAssetId, this, parent, maintainWorldTransform);
            FinalizePrefabSpawn(instance, parent, addToWorldRootWhenNoParent);
            return instance;
        }

        public SceneNode? InstantiatePrefab(string assetPath,
                                             SceneNode? parent = null,
                                             bool maintainWorldTransform = false,
                                             bool addToWorldRootWhenNoParent = true)
        {
            if (string.IsNullOrWhiteSpace(assetPath))
                return null;

            var assets = Engine.Assets;
            if (assets is null)
                return null;

            SceneNode? instance = assets.InstantiatePrefab(assetPath, this, parent, maintainWorldTransform);
            FinalizePrefabSpawn(instance, parent, addToWorldRootWhenNoParent);
            return instance;
        }

        [RequiresUnreferencedCode("Prefab override reflection requires runtime metadata.")]
        public SceneNode InstantiateVariant(XRPrefabVariant variant,
                                            SceneNode? parent = null,
                                            bool maintainWorldTransform = false,
                                            bool addToWorldRootWhenNoParent = true)
        {
            ArgumentNullException.ThrowIfNull(variant);
            var instance = SceneNodePrefabService.InstantiateVariant(variant, this, parent, maintainWorldTransform);
            FinalizePrefabSpawn(instance, parent, addToWorldRootWhenNoParent);
            return instance;
        }

        [RequiresUnreferencedCode("Prefab override reflection requires runtime metadata.")]
        public SceneNode? InstantiateVariant(Guid variantAssetId,
                                             SceneNode? parent = null,
                                             bool maintainWorldTransform = false,
                                             bool addToWorldRootWhenNoParent = true)
        {
            if (variantAssetId == Guid.Empty)
                return null;

            var assets = Engine.Assets;
            if (assets is null)
                return null;

            SceneNode? instance = assets.InstantiateVariant(variantAssetId, this, parent, maintainWorldTransform);
            FinalizePrefabSpawn(instance, parent, addToWorldRootWhenNoParent);
            return instance;
        }

        [RequiresUnreferencedCode("Prefab override reflection requires runtime metadata.")]
        public SceneNode? InstantiateVariant(string assetPath,
                                             SceneNode? parent = null,
                                             bool maintainWorldTransform = false,
                                             bool addToWorldRootWhenNoParent = true)
        {
            if (string.IsNullOrWhiteSpace(assetPath))
                return null;

            var assets = Engine.Assets;
            if (assets is null)
                return null;

            SceneNode? instance = assets.InstantiateVariant(assetPath, this, parent, maintainWorldTransform);
            FinalizePrefabSpawn(instance, parent, addToWorldRootWhenNoParent);
            return instance;
        }

        private void FinalizePrefabSpawn(SceneNode? instance, SceneNode? parent, bool addToWorldRootWhenNoParent)
        {
            if (instance is null)
                return;

            if (parent is null && addToWorldRootWhenNoParent)
                RootNodes.Add(instance);
        }

        #endregion

        /// <summary>
        /// Sequences are used to track the order of operations for debugging purposes.
        /// </summary>
        private Dictionary<int, List<string>> Sequences { get; } = [];

        public GameMode? GameMode { get; internal set; }

        public XRWorldInstance() : this(Engine.Rendering.NewVisualScene(), Engine.Rendering.NewPhysicsScene()) { }
        public XRWorldInstance(VisualScene3D visualScene, AbstractPhysicsScene physicsScene)
        {
            _visualScene = visualScene;
            _physicsScene = physicsScene;
            Lights = new Lights3DCollection(this);

            TickLists = [];
            TickLists.Add(ETickGroup.Normal, []);
            TickLists.Add(ETickGroup.Late, []);
            TickLists.Add(ETickGroup.PrePhysics, []);
            TickLists.Add(ETickGroup.DuringPhysics, []);
            TickLists.Add(ETickGroup.PostPhysics, []);
        }
        public XRWorldInstance(XRWorld world) : this()
            => TargetWorld = world;
        public XRWorldInstance(XRWorld world, VisualScene3D visualScene, AbstractPhysicsScene physicsScene) : this(visualScene, physicsScene)
            => TargetWorld = world;

        private void TickDuring() 
            => TickGroup(ETickGroup.DuringPhysics);

        private bool _physicsEnabled = false;
        /// <summary>
        /// Whether physics simulation is currently active for this world.
        /// Physics is automatically enabled/disabled when entering/exiting play mode.
        /// </summary>
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

        /// <summary>
        /// Fired when physics simulation is enabled or disabled.
        /// </summary>
        public event Action<bool>? PhysicsEnabledChanged;

        public void FixedUpdate()
        {
            TickGroup(ETickGroup.PrePhysics);
            // Only step physics if enabled (typically only during play mode)
            if (PhysicsEnabled)
                PhysicsScene.StepSimulation();
            ProcessQueuedPhysicsRaycasts();
            TickGroup(ETickGroup.DuringPhysics);
            TickGroup(ETickGroup.PostPhysics);
        }

        public enum EPlayState
        {
            Stopped,
            BeginningPlay,
            Playing,
            EndingPlay,
            Paused
        }

        private EPlayState _playState = EPlayState.Stopped;
        public EPlayState PlayState 
        {
            get => _playState;
            private set => SetField(ref _playState, value);
        }

        public bool TransitioningPlay => 
            PlayState == EPlayState.BeginningPlay || 
            PlayState == EPlayState.EndingPlay;

        public void PausePlay()
        {
            if (PlayState != EPlayState.Playing)
                return;
            PlayState = EPlayState.Paused;
        }
        public void ResumePlay()
        {
            if (PlayState != EPlayState.Paused)
                return;
            PlayState = EPlayState.Playing;
        }

        public async Task BeginPlay()
        {
            using var profilerScope = Engine.Profiler.Start("WorldInstance.BeginPlay");

            PlayState = EPlayState.BeginningPlay;
            PreBeginPlay?.Invoke(this);
            VisualScene.Initialize();
            PhysicsScene.Initialize();
            await BeginPlayInternal();
            LinkTimeCallbacks();
            PostBeginPlay?.Invoke(this);
            PlayState = EPlayState.Playing;
        }

        protected virtual async Task BeginPlayInternal()
        {
            VisualScene.GenericRenderTree.Swap();

            //Recalculate all transforms before activating nodes, in case any cross-dependencies exist
            foreach (SceneNode node in RootNodes)
                await node.Transform.RecalculateMatrixHeirarchy(true, true, Engine.Rendering.Settings.RecalcChildMatricesLoopType);

            foreach (SceneNode node in RootNodes)
                node.OnBeginPlay();

            foreach (SceneNode node in RootNodes)
                if (node.IsActiveSelf)
                    node.OnActivated();
        }

        public void EndPlay()
        {
            using var profilerScope = Engine.Profiler.Start("WorldInstance.EndPlay");
            
            PlayState = EPlayState.EndingPlay;
            PreEndPlay?.Invoke(this);
            UnlinkTimeCallbacks();
            PhysicsScene.Destroy();
            VisualScene.Destroy();
            EndPlayInternal();
            PostEndPlay?.Invoke(this);
            PlayState = EPlayState.Stopped;
        }
        protected virtual void EndPlayInternal()
        {
            VisualScene.GenericRenderTree.Swap();

            foreach (SceneNode node in RootNodes)
                node.OnEndPlay();

            foreach (SceneNode node in RootNodes)
                if (node.IsActiveSelf)
                    node.OnDeactivated();
        }

        private void LinkTimeCallbacks()
        {
            Time.Timer.PreUpdateFrame += PreUpdate;
            Time.Timer.UpdateFrame += Update;
            Time.Timer.PostUpdateFrame += PostUpdate;
            Time.Timer.FixedUpdate += FixedUpdate;
            Time.Timer.SwapBuffers += GlobalSwapBuffers;
            Time.Timer.CollectVisible += GlobalCollectVisible;
            //Time.Timer.PostUpdateFrame += ProcessTransformQueue;
        }

        private void UnlinkTimeCallbacks()
        {
            Time.Timer.PreUpdateFrame -= PreUpdate;
            Time.Timer.UpdateFrame -= Update;
            Time.Timer.PostUpdateFrame -= PostUpdate;
            Time.Timer.FixedUpdate -= FixedUpdate;
            Time.Timer.SwapBuffers -= GlobalSwapBuffers;
            Time.Timer.CollectVisible -= GlobalCollectVisible;
            //Time.Timer.PostUpdateFrame -= ProcessTransformQueue;
        }

        public void GlobalCollectVisible()
        {
            using var profilerScope = Engine.Profiler.Start("WorldInstance.GlobalCollectVisible");
            
            Lights.CollectVisibleItems();
        }

        private void ApplyRenderMatrixChanges()
        {
            using var profilerScope = Engine.Profiler.Start("WorldInstance.ApplyRenderMatrixChanges");
            
            //var arr = ArrayPool<(TransformBase tfm, Matrix4x4 renderMatrix)>.Shared.Rent(_pushToRenderSnapshot.Count);
            //await Task.WhenAll(_pushToRenderSnapshot.Select(x => x.tfm.SetRenderMatrix(x.renderMatrix, false)));
            //_pushToRenderSnapshot.Clear();
            while (_pushToRenderSnapshot.TryDequeue(out (TransformBase tfm, Matrix4x4 renderMatrix) item))
                item.tfm.SetRenderMatrix(item.renderMatrix, false);
        }

        private void GlobalSwapBuffers()
        {
            using var profilerScope = Engine.Profiler.Start("WorldInstance.GlobalSwapBuffers");

            //using var t = Engine.Profiler.Start();

            using (Engine.Profiler.Start("WorldInstance.GlobalSwapBuffers.ApplyRenderMatrices"))
            {
                ApplyRenderMatrixChanges();
            }

            using (Engine.Profiler.Start("WorldInstance.GlobalSwapBuffers.VisualScene"))
            {
                VisualScene.GlobalSwapBuffers();
            }
            //PhysicsScene.SwapDebugBuffers();
            using (Engine.Profiler.Start("WorldInstance.GlobalSwapBuffers.Lights"))
            {
                Lights.SwapBuffers();
            }
        }

        /// <summary>
        /// Called by windows on the render thread, right before rendering all viewports.
        /// </summary>
        public void GlobalPreRender()
        {
            using var profilerScope = Engine.Profiler.Start("WorldInstance.GlobalPreRender");

            VisualScene.GlobalPreRender();
            Lights.RenderShadowMaps(false);
        }

        /// <summary>
        /// Called by windows on the render thread, right after rendering all viewports.
        /// </summary>
        public void GlobalPostRender()
        {
            using var profilerScope = Engine.Profiler.Start("WorldInstance.GlobalPostRender");

            //using var d = Profiler.Start();
            VisualScene.GlobalPostRender();
            //Lights.CaptureLightProbes();
        }

        internal void ApplyRenderDispatchPreference(bool useGpu)
        {
            VisualScene?.ApplyRenderDispatchPreference(useGpu);
        }

        private void PreUpdate()
        {
            using var profilerScope = Engine.Profiler.Start("WorldInstance.PreUpdate");

            _pushToRenderWrite.Clear();
            _invalidTransforms.ForEach(x => x.Value.Clear());
        }

        private void PostUpdate()
        {
            using var profilerScope = Engine.Profiler.Start("WorldInstance.PostUpdate");

            var loopType = Engine.Rendering.Settings.RecalcChildMatricesLoopType;
            Func<IEnumerable<TransformBase>, Task> recalcDepth = loopType switch
            {
                Engine.Rendering.ELoopType.Asynchronous => RecalcTransformDepthAsync,
                Engine.Rendering.ELoopType.Parallel => RecalcTransformDepthParallel,
                _ => RecalcTransformDepthSequential,
            };

            //Sequentially iterate through each depth of modified transforms, in order
            //This will set each transforms' WorldMatrix, which will push it into the _pushToRenderWrite queue
            foreach (var item in _invalidTransforms.OrderBy(x => x.Key))
                recalcDepth(item.Value).Wait();

            //Capture of a snapshot of the queue to be processed in the render thread
            _pushToRenderWrite = Interlocked.Exchange(ref _pushToRenderSnapshot, _pushToRenderWrite);
        }

        public void EnqueueRenderTransformChange(TransformBase transform, Matrix4x4 worldMatrix)
        {
            _pushToRenderWrite.Enqueue((transform, worldMatrix));
            AnyTransformWorldMatrixChanged?.Invoke(this, transform, worldMatrix);
        }

        public event Action<XRWorldInstance, TransformBase, Matrix4x4>? AnyTransformWorldMatrixChanged;

        private static async Task RecalcTransformDepthSequential(IEnumerable<TransformBase> bag)
        {
            foreach (var transform in bag)
                await transform.RecalculateMatrixHeirarchy(true, false, Engine.Rendering.ELoopType.Sequential);
        }

        /// <summary>
        /// Recalculates the transform matrices of all transforms in the bag.
        /// A bag represents one level of transformations at a certain depth, so all transforms do not depend on one another.
        /// </summary>
        /// <param name="bag"></param>
        /// <returns></returns>
        private static Task RecalcTransformDepthAsync(IEnumerable<TransformBase> bag)
            => Task.WhenAll(bag.Select(tfm => tfm.RecalculateMatrixHeirarchy(
                forceWorldRecalc: true,
                setRenderMatrixNow: false,
                childRecalcType: Engine.Rendering.ELoopType.Asynchronous)));

        private static Task RecalcTransformDepthParallel(IEnumerable<TransformBase> bag)
        {
            if (!bag.Any())
                return Task.CompletedTask;

            return Task.Run(() => Parallel.ForEach(bag, tfm =>
                tfm.RecalculateMatrixHeirarchy(
                    forceWorldRecalc: true,
                    setRenderMatrixNow: false,
                    childRecalcType: Engine.Rendering.ELoopType.Parallel)
                .GetAwaiter().GetResult()));
        }

        private ConcurrentQueue<(TransformBase tfm, Matrix4x4 renderMatrix)> _pushToRenderWrite = new();
        private ConcurrentQueue<(TransformBase tfm, Matrix4x4 renderMatrix)> _pushToRenderSnapshot = new();
        private readonly ConcurrentDictionary<int, ConcurrentHashSet<TransformBase>> _invalidTransforms = [];

        /// <summary>
        /// Enqueues a transform to be recalculated at the end of the update after user code has been executed.
        /// Returns true if a new depth was added to the queue.
        /// </summary>
        public void AddDirtyTransform(TransformBase transform)
        {
            if (transform.ForceManualRecalc)
                return;

            _invalidTransforms.GetOrAdd(transform.Depth, i => []).Add(transform);
        }

        private XRWorld? _targetWorld;
        /// <summary>
        /// The world that this instance is rendering.
        /// </summary>
        public XRWorld? TargetWorld
        {
            get => _targetWorld;
            set => SetField(ref _targetWorld, value);
        }

        protected override bool OnPropertyChanging<T>(string? propName, T field, T @new)
        {
            bool change = base.OnPropertyChanging(propName, field, @new);
            if (change)
            {
                switch (propName)
                {
                    case nameof(TargetWorld):
                        if (TargetWorld != null)
                        {
                            // Unsubscribe from previous settings changes
                            TargetWorld.Settings.PropertyChanged -= OnWorldSettingsChanged;
                            foreach (var scene in TargetWorld.Scenes)
                                UnloadScene(scene);
                        }
                  break;
                }
            }
            return change;
        }
        
        protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
        {
            base.OnPropertyChanged(propName, prev, field);
            switch (propName)
            {
                case nameof(TargetWorld):
                    if (TargetWorld != null)
                    {
                        foreach (var scene in TargetWorld.Scenes)
                            LoadScene(scene);
                        if (VisualScene.GenericRenderTree is I3DRenderTree tree)
                            tree.Remake(TargetWorld.Settings.Bounds);
   
                        // Apply world settings when the target world changes
                        ApplySettings(TargetWorld.Settings);
   
                        // Subscribe to settings changes
                        TargetWorld.Settings.PropertyChanged += OnWorldSettingsChanged;
                    }
                    break;
                case nameof(PlayState):
                    if (field is EPlayState newState)
                    {
                        switch (newState)
                        {
                            case EPlayState.Playing:
                                PhysicsEnabled = true;
                                break;
                            case EPlayState.Stopped:
                                PhysicsEnabled = false;
                                break;
                        }
                    }
                    break;
            }
        }

        public EventList<CameraComponent> FramebufferCameras { get; } = [];

        public void LoadScene(XRScene scene)
        {
            if (scene.IsVisible)
                LoadVisibleScene(scene);

            scene.PropertyChanged += ScenePropertyChanged;
        }

        public void UnloadScene(XRScene scene)
        {
            scene.PropertyChanged -= ScenePropertyChanged;

            if (scene.IsVisible)
                UnloadVisibleScene(scene);
        }

        void ScenePropertyChanged(object? sender, IXRPropertyChangedEventArgs args)
        {
            if (sender is not XRScene scene)
                return;

            switch (args.PropertyName)
            {
                case nameof(XRScene.IsVisible):
                    if (scene.IsVisible)
                        LoadVisibleScene(scene);
                    else
                        UnloadVisibleScene(scene);
                    break;
            }
        }

        private void LoadVisibleScene(XRScene scene)
        {
            foreach (var node in scene.RootNodes)
            {
                node.World = this;
                RootNodes.Add(node);
            }
        }

        private void UnloadVisibleScene(XRScene scene)
        {
            foreach (var node in scene.RootNodes)
            {
                RootNodes.Remove(node);
                node.World = null;
            }
        }

        /// <summary>
        /// Physics group > order (arbitrarily set by user> > list of objects to tick
        /// </summary>
        private readonly Dictionary<ETickGroup, SortedDictionary<int, TickList>> TickLists;

        /// <summary>
        /// Registers a method to execute in a specific order every update tick.
        /// </summary>
        /// <param name="group">The first grouping of when to tick: before, after, or during the physics tick update.</param>
        /// <param name="order">The order to execute the function within its group.</param>
        /// <param name="function">The function to execute per update tick.</param>
        /// <param name="pausedBehavior">If the function should even execute at all, depending on the pause state.</param>
        public void RegisterTick(ETickGroup group, int order, TickList.DelTick function)
        {
            if (function is null)
                return;

            GetTickList(group, order)?.Add(function);
        }

        /// <summary>
        /// Stops running a tick method that was previously registered with the same parameters.
        /// </summary>
        public void UnregisterTick(ETickGroup group, int order, TickList.DelTick function)
        {
            if (function is null)
                return;

            GetTickList(group, order)?.Remove(function);
        }

        private readonly Lock _listGroupLock = new();

        /// <summary>
        /// Gets a list of items to tick (in no particular order) that were registered with the following parameters.
        /// </summary>
        private TickList GetTickList(ETickGroup group, int order)
        {
            using (_listGroupLock.EnterScope())
            {
                SortedDictionary<int, TickList> dic = TickLists[group];
                if (!dic.TryGetValue(order, out TickList? list))
                    dic.Add(order, list = new TickList(Engine.Rendering.Settings.TickGroupedItemsInParallel));
                return list;
            }
        }

        /// <summary>
        /// Ticks all sorted lists of methods registered to this group.
        /// </summary>
        public void TickGroup(ETickGroup group)
        {
            using (_listGroupLock.EnterScope())
            {
                var tickListDic = TickLists[group];
                List<int> toRemove = [];
                foreach (var kv in tickListDic)
                {
                    kv.Value.Tick();
                    if (kv.Value.Count == 0)
                        toRemove.Add(kv.Key);
                }
                foreach (int key in toRemove)
                    tickListDic.Remove(key);
            }
        }

        //private readonly object _lock = new();

        /// <summary>
        /// Ticks the before, during, and after physics groups. Also steps the physics simulation during the during physics tick group.
        /// Does not tick physics if paused.
        /// </summary>
        private void Update()
        {
#if DEBUG
            ClearMarkers();
#endif
            TickGroup(ETickGroup.Normal);
            TickGroup(ETickGroup.Late);
#if DEBUG
            PrintMarkers();
#endif
        }

        public void SequenceMarker(int id, [CallerMemberName] string name = "")
        {
            if (Sequences.TryGetValue(id, out List<string>? value))
                value.Add(name);
            else
                Sequences[id] = [name];
        }
        private void PrintMarkers()
        {
            foreach (var kv in Sequences)
                Trace.WriteLine($"Sequence {kv.Key}: {kv.Value.ToStringList(",")}");
        }
        private void ClearMarkers()
        {
            Sequences.Clear();
        }

        private static readonly Action<SortedDictionary<float, List<(XRComponent? item, object? data)>>> _noopPhysicsRaycastCallback = _ => { };

        private void ProcessQueuedPhysicsRaycasts()
        {
            while (_pendingPhysicsRaycasts.TryDequeue(out var request))
            {
                try
                {
                    request.Results.Clear();

                    PhysicsScene.RaycastSingleAsync(
                        request.Segment,
                        request.LayerMask,
                        request.Filter,
                        request.Results,
                        _noopPhysicsRaycastCallback);

                    request.FinishedCallback?.Invoke(request.Results);
                }
                catch (Exception ex)
                {
                    XREngine.Debug.LogException(ex, "Queued physics raycast failed.");
                    request.FinishedCallback?.Invoke(request.Results);
                }
                finally
                {
                    request.Clear();
                    _physicsRaycastRequestPool.Enqueue(request);
                }
            }
        }

        public void RaycastPhysicsAsync(
            CameraComponent cameraComponent,
            Vector2 normalizedScreenPoint,
            LayerMask layerMask,
            AbstractPhysicsScene.IAbstractQueryFilter? filter,
            SortedDictionary<float, List<(XRComponent? item, object? data)>> orderedResults,
            Action<SortedDictionary<float, List<(XRComponent? item, object? data)>>> finishedCallback)
            => RaycastPhysicsAsync(
                cameraComponent.Camera.GetWorldSegment(normalizedScreenPoint),
                layerMask,
                filter,
                orderedResults,
                finishedCallback);

        public void RaycastPhysicsAsync(
            Segment worldSegment,
            LayerMask layerMask,
            AbstractPhysicsScene.IAbstractQueryFilter? filter,
            SortedDictionary<float, List<(XRComponent? item, object? data)>> orderedResults,
            Action<SortedDictionary<float, List<(XRComponent? item, object? data)>>> finishedCallback)
        {
            orderedResults.Clear();

            if (!_physicsRaycastRequestPool.TryDequeue(out var request))
                request = new PhysicsRaycastRequest();

            request.Set(worldSegment, layerMask, filter, orderedResults, finishedCallback);
            _pendingPhysicsRaycasts.Enqueue(request);
        }

        /// <summary>
        /// Raycasts the octree and updates a sorted list of items that were hit.
        /// Clears the list before adding results.
        /// </summary>
        /// <param name="cameraComponent"></param>
        /// <param name="normalizedScreenPoint"></param>
        /// <param name="orderedResults"></param>
        public void RaycastOctreeAsync(
            CameraComponent cameraComponent,
            Vector2 normalizedScreenPoint,
            SortedDictionary<float, List<(RenderInfo3D item, object? data)>> orderedResults,
            Action<SortedDictionary<float, List<(RenderInfo3D item, object? data)>>> finishedCallback,
            ERaycastHitMode hitMode = ERaycastHitMode.Faces)
            => RaycastOctreeAsync(
                cameraComponent.Camera.GetWorldSegment(normalizedScreenPoint),
                orderedResults,
                finishedCallback,
                hitMode);

        /// <summary>
        /// Raycasts the octree and updates a sorted list of items that were hit.
        /// Clears the list before adding results.
        /// </summary>
        /// <param name="worldSegment"></param>
        /// <param name="orderedResults"></param>
        public void RaycastOctreeAsync(
            Segment worldSegment,
            SortedDictionary<float, List<(RenderInfo3D item, object? data)>> orderedResults,
            Action<SortedDictionary<float, List<(RenderInfo3D item, object? data)>>> finishedCallback,
            ERaycastHitMode hitMode = ERaycastHitMode.Faces)
        {
            //orderedResults.Clear();
            VisualScene.RaycastAsync(worldSegment, orderedResults, (item, segment) => DirectItemTest(item, segment, hitMode), finishedCallback);
        }

        private static (float? distance, object? data) DirectItemTest(RenderInfo3D item, Segment segment, ERaycastHitMode hitMode)
        {
            if (item is not RenderInfo renderable)
                return (null, null);

            if (renderable.Owner is RenderableComponent renderableComponent &&
                TryIntersectRenderableComponent(renderableComponent, item, segment, hitMode, out float meshDistance, out object? pickResult))
                return (meshDistance, pickResult);
            return (null, null);
        }

        private static bool TryIntersectRenderableComponent(
            RenderableComponent component,
            RenderInfo3D info,
            Segment worldSegment,
            ERaycastHitMode hitMode,
            out float distance,
            out object? result)
        {
            distance = 0.0f;
            result = default;

            if (!TryFindRenderableMesh(component, info, out RenderableMesh? mesh))
                return false;

            if (mesh is null || ShouldIgnoreRenderableMesh(mesh))
                return false;

            if (!TryIntersectRenderableMesh(mesh, worldSegment, out distance, out Triangle worldTriangle, out Vector3 hitPoint, out IndexTriangle triangleIndices, out int triangleIndex))
                return false;

            MeshPickResult faceHit = new(component, mesh, worldTriangle, hitPoint, triangleIndex, triangleIndices);
            return TryBuildPickResult(hitMode, faceHit, out result);
        }

        private static bool TryBuildPickResult(ERaycastHitMode hitMode, MeshPickResult faceHit, out object? result)
        {
            switch (hitMode)
            {
                case ERaycastHitMode.Faces:
                    result = faceHit;
                    return true;
                case ERaycastHitMode.Lines:
                    if (TryBuildEdgePickResult(faceHit, out MeshEdgePickResult edgeHit))
                    {
                        result = edgeHit;
                        return true;
                    }
                    break;
                case ERaycastHitMode.Points:
                    if (TryBuildVertexPickResult(faceHit, out MeshVertexPickResult vertexHit))
                    {
                        result = vertexHit;
                        return true;
                    }
                    break;
            }

            result = null;
            return false;
        }

        private static bool TryBuildEdgePickResult(MeshPickResult faceHit, out MeshEdgePickResult result)
        {
            result = default;
            Triangle tri = faceHit.WorldTriangle;
            if (!tri.TryGetBarycentricCoordinates(faceHit.HitPoint, out Vector3 bary))
                return false;

            float bestWeight = float.MaxValue;
            Vector3 bestStart = default;
            Vector3 bestEnd = default;
            int bestEdgeIndex = -1;

            EvaluateEdge(bary.Z, tri.A, tri.B, 0);
            EvaluateEdge(bary.X, tri.B, tri.C, 1);
            EvaluateEdge(bary.Y, tri.C, tri.A, 2);

            if (bestWeight == float.MaxValue || bestEdgeIndex < 0)
                return false;

            Vector3 closest = ProjectPointOntoSegment(faceHit.HitPoint, bestStart, bestEnd);
            result = new MeshEdgePickResult(faceHit, bestStart, bestEnd, closest, bestEdgeIndex);
            return true;

            void EvaluateEdge(float coord, Vector3 start, Vector3 end, int edgeIndex)
            {
                if (coord > EdgeBarycentricThreshold || coord >= bestWeight)
                    return;
                bestWeight = coord;
                bestStart = start;
                bestEnd = end;
                bestEdgeIndex = edgeIndex;
            }
        }

        private static bool TryBuildVertexPickResult(MeshPickResult faceHit, out MeshVertexPickResult result)
        {
            result = default;
            Triangle tri = faceHit.WorldTriangle;
            if (!tri.TryGetBarycentricCoordinates(faceHit.HitPoint, out Vector3 bary))
                return false;

            float bestDelta = float.MaxValue;
            Vector3 bestVertex = default;
            int bestIndex = -1;

            EvaluateVertex(MathF.Abs(1.0f - bary.X), tri.A, faceHit.Indices.Point0);
            EvaluateVertex(MathF.Abs(1.0f - bary.Y), tri.B, faceHit.Indices.Point1);
            EvaluateVertex(MathF.Abs(1.0f - bary.Z), tri.C, faceHit.Indices.Point2);

            if (bestDelta > VertexBarycentricThreshold || bestIndex < 0)
                return false;

            result = new MeshVertexPickResult(faceHit, bestVertex, bestIndex);
            return true;

            void EvaluateVertex(float delta, Vector3 vertex, int vertexIndex)
            {
                if (delta >= bestDelta)
                    return;
                bestDelta = delta;
                bestVertex = vertex;
                bestIndex = vertexIndex;
            }
        }

        private static Vector3 ProjectPointOntoSegment(Vector3 point, Vector3 start, Vector3 end)
        {
            Vector3 edge = end - start;
            float lengthSq = edge.LengthSquared();
            if (lengthSq <= XRMath.Epsilon)
                return start;

            float t = Vector3.Dot(point - start, edge) / lengthSq;
            t = float.Clamp(t, 0.0f, 1.0f);
            return start + edge * t;
        }

        private static bool ShouldIgnoreRenderableMesh(RenderableMesh mesh)
        {
            foreach (var command in mesh.RenderInfo.RenderCommands)
            {
                if (command.RenderPass == (int)EDefaultRenderPass.Background)
                    return true;
            }

            foreach (var lod in mesh.LODs)
            {
                var material = lod.Renderer.Material;
                if (material is not null && material.RenderPass == (int)EDefaultRenderPass.Background)
                    return true;
            }

            return false;
        }

        private static bool TryFindRenderableMesh(RenderableComponent component, RenderInfo3D info, out RenderableMesh? mesh)
        {
            foreach (var candidate in component.Meshes)
            {
                if (ReferenceEquals(candidate.RenderInfo, info))
                {
                    mesh = candidate;
                    return true;
                }
            }

            mesh = null;
            return false;
        }

        private static bool TryIntersectRenderableMesh(
            RenderableMesh mesh,
            Segment worldSegment,
            out float distance,
            out Triangle worldTriangle,
            out Vector3 hitPoint,
            out IndexTriangle triangleIndices,
            out int triangleIndex)
        {
            distance = 0.0f;
            worldTriangle = default;
            hitPoint = default;
            triangleIndices = new IndexTriangle();
            triangleIndex = -1;

            var lodNode = mesh.CurrentLOD ?? mesh.LODs.First;
            var renderer = lodNode?.Value.Renderer;
            if (renderer is null)
                return false;

            var material = renderer.Material;
            if (material is not null && material.RenderPass == (int)EDefaultRenderPass.Background)
                return false;

            var xrMesh = renderer.Mesh;
            if (xrMesh is null)
                return false;

            bool skinned = xrMesh.HasSkinning && Engine.Rendering.Settings.AllowSkinning;

            var bvh = skinned ? mesh.GetSkinnedBvh() : xrMesh.BVHTree;
            if (bvh is null)
            {
                if (skinned)
                    return false; // Skinned BVH build is asynchronous; try again next frame.

                xrMesh.GenerateBVH();
                bvh = xrMesh.BVHTree;
                if (bvh is null)
                    return false;
            }

            Vector3 segmentSpaceStart;
            Vector3 segmentSpaceEnd;
            Matrix4x4? spaceToWorld = null;

            if (skinned)
            {
                spaceToWorld = mesh.SkinnedBvhLocalToWorldMatrix;
                var worldToLocal = mesh.SkinnedBvhWorldToLocalMatrix;
                segmentSpaceStart = Vector3.Transform(worldSegment.Start, worldToLocal);
                segmentSpaceEnd = Vector3.Transform(worldSegment.End, worldToLocal);
            }
            else
            {
                var transform = mesh.Component.Transform;
                if (transform is null)
                    return false;
                spaceToWorld = transform.WorldMatrix;

                segmentSpaceStart = Vector3.Transform(worldSegment.Start, transform.InverseWorldMatrix);
                segmentSpaceEnd = Vector3.Transform(worldSegment.End, transform.InverseWorldMatrix);
            }

            Vector3 segmentSpaceDiff = segmentSpaceEnd - segmentSpaceStart;
            float segmentSpaceLength = segmentSpaceDiff.Length();
            if (segmentSpaceLength <= 1e-5f)
                return false;

            Vector3 segmentSpaceDir = segmentSpaceDiff / segmentSpaceLength;

            var matches = bvh.Traverse(node => GeoUtil.SegmentIntersectsAABB(segmentSpaceStart, segmentSpaceEnd, node.Min, node.Max, out _, out _));
            if (matches is null)
                return false;

            float bestDistance = float.MaxValue;
            Triangle? bestTriangle = null;

            foreach (var node in matches)
            {
                if (node.gobjects is null)
                    continue;

                foreach (var tri in node.gobjects)
                {
                    if (!GeoUtil.RayIntersectsTriangle(segmentSpaceStart, segmentSpaceDir, tri.A, tri.B, tri.C, out float hitDistance))
                        continue;

                    if (hitDistance < 0.0f || hitDistance > segmentSpaceLength)
                        continue;

                    if (hitDistance < bestDistance)
                    {
                        bestDistance = hitDistance;
                        bestTriangle = tri;
                    }
                }
            }

            if (bestTriangle is null)
                return false;

            Triangle localTriangle = bestTriangle.Value;
            if (xrMesh.TriangleLookup is { } lookup && lookup.TryGetValue(localTriangle, out var indices))
            {
                triangleIndices = indices.Indices;
                triangleIndex = indices.FaceIndex;
            }

            Vector3 spaceHitPoint = segmentSpaceStart + segmentSpaceDir * bestDistance;
            if (spaceToWorld is null)
                return false;

            hitPoint = Vector3.Transform(spaceHitPoint, spaceToWorld.Value);
            worldTriangle = new Triangle(
                Vector3.Transform(localTriangle.A, spaceToWorld.Value),
                Vector3.Transform(localTriangle.B, spaceToWorld.Value),
                Vector3.Transform(localTriangle.C, spaceToWorld.Value));

            distance = (hitPoint - worldSegment.Start).Length();
            float worldLength = (worldSegment.End - worldSegment.Start).Length();
            if (distance < 0.0f || distance > worldLength)
                return false;

            return true;
        }

        public static XRWorldInstance GetOrInitWorld(XRWorld targetWorld)
        {
            if (!WorldInstances.TryGetValue(targetWorld, out var instance))
     WorldInstances.Add(targetWorld, instance = new(targetWorld));
            return instance;
 }

        #region World Settings

        private void OnWorldSettingsChanged(object? sender, IXRPropertyChangedEventArgs args)
        {
            if (sender is not WorldSettings settings)
    return;

        // Apply the specific setting that changed
       ApplySettingByProperty(settings, args.PropertyName);
        }

    /// <summary>
        /// Applies a specific world setting based on the property name that changed.
      /// </summary>
     private void ApplySettingByProperty(WorldSettings settings, string? propertyName)
        {
        switch (propertyName)
          {
             case nameof(WorldSettings.Gravity):
          PhysicsScene.Gravity = settings.Gravity;
    break;
        case nameof(WorldSettings.Bounds):
   if (VisualScene.GenericRenderTree is I3DRenderTree tree)
                tree.Remake(settings.Bounds);
          break;
         case nameof(WorldSettings.MasterVolume):
      case nameof(WorldSettings.SpeedOfSound):
     case nameof(WorldSettings.DopplerFactor):
    case nameof(WorldSettings.DefaultAudioAttenuation):
     ApplyAudioSettings(settings);
   break;
   // For other properties, they are automatically picked up by rendering systems
    // that query the settings directly
        }
 }

        /// <summary>
   /// Applies all settings from the WorldSettings to this world instance.
        /// This is called when a world is first loaded or when you want to refresh all settings.
        /// </summary>
        public void ApplySettings(WorldSettings settings)
      {
            if (settings is null)
                return;

     ApplyPhysicsSettings(settings);
    ApplyAudioSettings(settings);
 ApplyBoundsSettings(settings);
      }

     /// <summary>
        /// Applies physics-related settings to the physics scene.
 /// </summary>
    private void ApplyPhysicsSettings(WorldSettings settings)
 {
     PhysicsScene.Gravity = settings.Gravity;
          
            // Additional physics settings could be applied here when the physics system supports them:
     // PhysicsScene.Timestep = settings.PhysicsTimestep;
            // PhysicsScene.Substeps = settings.PhysicsSubsteps;
            // PhysicsScene.DefaultLinearDamping = settings.DefaultLinearDamping;
   // PhysicsScene.DefaultAngularDamping = settings.DefaultAngularDamping;
            // PhysicsScene.EnableContinuousCollision = settings.EnableContinuousCollision;
        }

        /// <summary>
        /// Applies audio-related settings to audio systems.
        /// </summary>
        private void ApplyAudioSettings(WorldSettings settings)
        {
            // Apply audio settings to listeners in this world
         foreach (var listener in Listeners)
       {
 // listener.SpeedOfSound = settings.SpeedOfSound;
          // listener.DopplerFactor = settings.DopplerFactor;
     }
            
            // Engine-level audio settings could be applied via Engine.Audio
      // Engine.Audio?.SetMasterVolume(settings.MasterVolume);
 }

        /// <summary>
        /// Applies world bounds settings to the visual scene.
    /// </summary>
        private void ApplyBoundsSettings(WorldSettings settings)
    {
            if (VisualScene.GenericRenderTree is I3DRenderTree tree)
        tree.Remake(settings.Bounds);
  }

        /// <summary>
     /// Gets the current world settings, or creates default settings if none exist.
        /// </summary>
        public WorldSettings GetSettings()
        {
            return TargetWorld?.Settings ?? new WorldSettings();
    }

        /// <summary>
        /// Gets the effective ambient light color from world settings, or a default if not available.
        /// </summary>
        public Data.Colors.ColorF3 GetEffectiveAmbientColor()
        {
            var settings = TargetWorld?.Settings;
            return settings?.GetEffectiveAmbientColor() ?? new Data.Colors.ColorF3(0.03f, 0.03f, 0.03f);
        }

 #endregion
    }
}
