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
using XREngine.Components.Physics;
using XREngine.Components.Scene.Mesh;
using XREngine.Data.Core;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Data.Trees;
using XREngine.Rendering.Info;
using XREngine.Rendering.Lightmapping;
using XREngine.Rendering.Picking;
using XREngine.Scene;
using XREngine.Scene.Physics;
using XREngine.Scene.Prefabs;
using XREngine.Scene.Transforms;
using YamlDotNet.Serialization;
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

        private readonly Dictionary<XRScene, HashSet<SceneNode>> _editorOnlyNodesByScene = [];

        [RuntimeOnly]
        [YamlIgnore]
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

        protected RootNodeCollection _rootNodes;
        public RootNodeCollection RootNodes => _rootNodes;
        public Lights3DCollection Lights { get; }

        #region Editor-Only Hidden Scene

        private XRScene? _editorScene;
        /// <summary>
        /// A hidden scene used for editor-only content such as gizmos, transform tools, and UI.
        /// This scene is not saved with the world and is not displayed in the hierarchy panel.
        /// </summary>
        public XRScene EditorScene
        {
            get
            {
                if (_editorScene is null)
                {
                    _editorScene = new XRScene("__EditorScene__")
                    {
                        IsEditorOnly = true,
                        IsVisible = true
                    };
                    LoadScene(_editorScene);
                }
                return _editorScene;
            }
        }

        /// <summary>
        /// Adds a scene node to the hidden editor scene.
        /// Use this for editor-only content like gizmos, transform tools, and debug visualization.
        /// </summary>
        /// <param name="node">The node to add to the editor scene.</param>
        public void AddToEditorScene(SceneNode node)
        {
            if (node is null)
                return;

            var editorScene = EditorScene;

            void AddAsEditorRoot()
            {
                if (!editorScene.RootNodes.Contains(node))
                    editorScene.RootNodes.Add(node);

                node.World = this;

                if (!RootNodes.Any(existing => ReferenceEquals(existing, node)))
                    RootNodes.Add(node);
            }

            // Detaching a node by directly setting Parent can conflict with transform child list
            // enumeration (EventList uses ReaderWriterLockSlim). Defer the detachment to the
            // engine's post-update parent reassignment queue and only add to the editor scene
            // once the node is actually detached.
            if (node.Transform?.Parent is not null)
            {
                node.Transform.SetParent(null, preserveWorldTransform: false, EParentAssignmentMode.Deferred,
                    onApplied: (_, __) => AddAsEditorRoot());
            }
            else
            {
                AddAsEditorRoot();
            }
        }

        /// <summary>
        /// Removes a scene node from the hidden editor scene.
        /// </summary>
        /// <param name="node">The node to remove from the editor scene.</param>
        public void RemoveFromEditorScene(SceneNode node)
        {
            if (node is null || _editorScene is null)
                return;

            _editorScene.RootNodes.Remove(node);
            RootNodes.Remove(node);
            node.World = null;
        }

        /// <summary>
        /// Checks if a node belongs to the hidden editor scene.
        /// </summary>
        /// <param name="node">The node to check.</param>
        /// <returns>True if the node is in the editor scene.</returns>
        public bool IsInEditorScene(SceneNode? node)
        {
            if (node is null || _editorScene is null)
                return false;

            // Walk up to find the root node
            SceneNode? root = node;
            while (root?.Transform?.Parent?.SceneNode is SceneNode parent)
                root = parent;

            return root is not null && _editorScene.RootNodes.Contains(root);
        }

        #endregion

        // Physics raycasts must run on the fixed-update (physics) thread.
        private readonly ConcurrentQueue<PhysicsRaycastRequest> _pendingPhysicsRaycasts = new();
        private readonly ConcurrentQueue<PhysicsRaycastRequest> _physicsRaycastRequestPool = new();

        #region Prefab instancing

        public SceneNode InstantiatePrefab(
            XRPrefabSource prefab,
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
            _rootNodes = new RootNodeCollection(this);
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
                Debug.Out($"[XRWorldInstance] PhysicsEnabled changing from {_physicsEnabled} to {value}");
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
            {
                PhysicsScene.StepSimulation();

                // Apply any per-body min-Y reset requests after stepping physics.
                // This is intentionally per-body (not global) so only bodies that cross the plane are reset.
                ProcessPhysicsMinYPlaneResetRequests();

                // Process queued physics raycasts only when physics is running
                ProcessQueuedPhysicsRaycasts();
            }
            TickGroup(ETickGroup.DuringPhysics);
            TickGroup(ETickGroup.PostPhysics);
        }

        private bool _physicsResetCacheValid;
        private readonly Dictionary<DynamicRigidBodyComponent, (Vector3 position, Quaternion rotation)> _physicsResetInitialDynamicPoses = [];

        // Min-Y reset is evaluated by transforms and queued here; it is applied on FixedUpdate.
        private readonly ConcurrentQueue<IAbstractDynamicRigidBody> _pendingMinYPlaneResetRequests = new();
        private readonly ConcurrentDictionary<IAbstractDynamicRigidBody, byte> _pendingMinYPlaneResetRequestSet =
            new(System.Collections.Generic.ReferenceEqualityComparer.Instance);

        private readonly Dictionary<IAbstractDynamicRigidBody, DynamicRigidBodyComponent> _physicsResetDynamicBodyLookup =
            new(System.Collections.Generic.ReferenceEqualityComparer.Instance);

        internal void EnqueuePhysicsResetFromMinYPlane(IAbstractDynamicRigidBody body)
        {
            if (!PhysicsEnabled)
                return;

            // De-dupe so a body only appears once until it's processed.
            if (_pendingMinYPlaneResetRequestSet.TryAdd(body, 0))
                _pendingMinYPlaneResetRequests.Enqueue(body);
        }

        private void ProcessPhysicsMinYPlaneResetRequests()
        {
            if (!PhysicsEnabled)
                return;

            if (_pendingMinYPlaneResetRequests.IsEmpty)
                return;

            // Re-check the setting at the time we apply the reset.
            float minYDist = TargetWorld?.Settings?.PhysicsResetMinYDist ?? 0.0f;
            bool enabled = minYDist > 0.0f;

            if (enabled)
                EnsurePhysicsResetCache();

            while (_pendingMinYPlaneResetRequests.TryDequeue(out IAbstractDynamicRigidBody? body))
            {
                if (body is null)
                    continue;

                _pendingMinYPlaneResetRequestSet.TryRemove(body, out _);

                if (!enabled)
                    continue;

                if (!_physicsResetDynamicBodyLookup.TryGetValue(body, out DynamicRigidBodyComponent? bodyComp))
                    continue;

                ResetDynamicPhysicsBodyToInitialPose(bodyComp);
            }
        }

        private void EnsurePhysicsResetCache()
        {
            if (_physicsResetCacheValid)
                return;

            _physicsResetInitialDynamicPoses.Clear();
            _physicsResetDynamicBodyLookup.Clear();

            foreach (SceneNode root in RootNodes)
            {
                foreach (SceneNode node in SceneNodePrefabUtility.EnumerateHierarchy(root))
                {
                    foreach (var comp in node.Components)
                    {
                        if (comp is not DynamicRigidBodyComponent bodyComp)
                            continue;
                        if (bodyComp.World != this)
                            continue;

                        (Vector3 position, Quaternion rotation) pose;
                        if (bodyComp.RigidBody is Rendering.Physics.Physx.PhysxRigidActor physxActor)
                            pose = physxActor.Transform;
                        else
                        {
                            var matrix = bodyComp.Transform.WorldMatrix;
                            Matrix4x4.Decompose(matrix, out _, out pose.rotation, out pose.position);
                        }

                        _physicsResetInitialDynamicPoses[bodyComp] = pose;

                        if (bodyComp.RigidBody is IAbstractDynamicRigidBody dynBody)
                            _physicsResetDynamicBodyLookup[dynBody] = bodyComp;
                    }
                }
            }

            _physicsResetCacheValid = true;
        }

        private void ResetDynamicPhysicsBodyToInitialPose(DynamicRigidBodyComponent bodyComp)
        {
            if (bodyComp?.World != this)
                return;

            if (!_physicsResetInitialDynamicPoses.TryGetValue(bodyComp, out var pose))
                return;

            if (bodyComp.RigidBody is Rendering.Physics.Physx.PhysxRigidActor physxActor)
                physxActor.SetTransform(pose.position, pose.rotation, wake: true);

            bodyComp.RigidBodyTransform.SetPositionAndRotation(pose.position, pose.rotation);
            bodyComp.KinematicTarget = null;
            bodyComp.LinearVelocity = Vector3.Zero;
            bodyComp.AngularVelocity = Vector3.Zero;
        }

        private void ResetDynamicPhysicsBodiesToInitialPoses()
        {
            foreach (var (bodyComp, pose) in _physicsResetInitialDynamicPoses)
            {
                if (bodyComp?.World != this)
                    continue;

                if (bodyComp.RigidBody is Rendering.Physics.Physx.PhysxRigidActor physxActor)
                    physxActor.SetTransform(pose.position, pose.rotation, wake: true);

                bodyComp.RigidBodyTransform.SetPositionAndRotation(pose.position, pose.rotation);
                bodyComp.KinematicTarget = null;
                bodyComp.LinearVelocity = Vector3.Zero;
                bodyComp.AngularVelocity = Vector3.Zero;
            }
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

            // Editor play-mode transitions can reload/clone scene graphs while this instance persists.
            // Ensure cached world lists (lights/probes/capture components) are cleared and repopulated.
            Lights.RebuildCachesFromWorld();

            VisualScene.Initialize();
            PhysicsScene.Initialize();

            _physicsResetCacheValid = false;
            _physicsResetInitialDynamicPoses.Clear();

            await BeginPlayInternal();

            if (PhysicsEnabled)
                PhysicsScene.OnEnterPlayMode();

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
            EndPlayInternal();

            // IMPORTANT: EndPlayInternal deactivates nodes/components which may unregister
            // physics/render objects. Keep scenes alive until after that completes.
            PhysicsScene.Destroy();
            VisualScene.Destroy();

            // After ending play, editor state restoration may be about to swap scene graphs back.
            // Reset caches now to avoid stale references persisting into edit mode.
            Lights.RebuildCachesFromWorld();

            _physicsResetCacheValid = false;
            _physicsResetInitialDynamicPoses.Clear();

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
            Time.Timer.PreCollectVisible += PreCollectVisible;
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
            Time.Timer.PreCollectVisible -= PreCollectVisible;
            Time.Timer.CollectVisible -= GlobalCollectVisible;
            //Time.Timer.PostUpdateFrame -= ProcessTransformQueue;
        }

        private void PreCollectVisible()
        {
            using var profilerScope = Engine.Profiler.Start("WorldInstance.PreCollectVisible");
            
            VisualScene.GlobalCollectVisible();
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
            int applied = 0;
            while (_pushToRenderSnapshot.TryDequeue(out (TransformBase tfm, Matrix4x4 renderMatrix) item))
            {
                item.tfm.SetRenderMatrix(item.renderMatrix, false);
                applied++;
            }

            Engine.Rendering.Stats.RecordRenderMatrixApplied(applied);
        }

        private void GlobalSwapBuffers()
        {
            using var profilerScope = Engine.Profiler.Start("WorldInstance.GlobalSwapBuffers");

            //using var t = Engine.Profiler.Start();

            using (Engine.Profiler.Start("WorldInstance.GlobalSwapBuffers.ApplyRenderMatrices"))
            {
                ApplyRenderMatrixChanges();
            }

            RenderableMesh.ProcessPendingRenderMatrixUpdates();

            using (Engine.Profiler.Start("WorldInstance.GlobalSwapBuffers.VisualScene"))
            {
                VisualScene.GlobalSwapBuffers();
            }
            
            // Swap octree stats after octree commands are consumed.
            Engine.Rendering.Stats.SwapOctreeStats();
            //PhysicsScene.SwapDebugBuffers();
            using (Engine.Profiler.Start("WorldInstance.GlobalSwapBuffers.Lights"))
            {
                Lights.SwapBuffers();
            }

            // Swap render-matrix stats after all SwapBuffers work is done.
            Engine.Rendering.Stats.SwapRenderMatrixStats();
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

        internal void ApplyGpuBvhPreference(bool useGpuBvh)
        {
            if (VisualScene is VisualScene3D scene3D)
                scene3D.ApplyGpuBvhPreference(useGpuBvh);
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
                ELoopType.Asynchronous => RecalcTransformDepthAsync,
                ELoopType.Parallel => RecalcTransformDepthParallel,
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
            if (!transform.ShouldEnqueueRenderMatrix(worldMatrix))
                return;

            _pushToRenderWrite.Enqueue((transform, worldMatrix));
            AnyTransformWorldMatrixChanged?.Invoke(this, transform, worldMatrix);
        }

        public event Action<XRWorldInstance, TransformBase, Matrix4x4>? AnyTransformWorldMatrixChanged;

        private static async Task RecalcTransformDepthSequential(IEnumerable<TransformBase> bag)
        {
            foreach (var transform in bag)
                await transform.RecalculateMatrixHeirarchy(true, false, ELoopType.Sequential);
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
                childRecalcType: ELoopType.Asynchronous)));

        private static Task RecalcTransformDepthParallel(IEnumerable<TransformBase> bag)
        {
            if (!bag.Any())
                return Task.CompletedTask;

            return Task.Run(() => Parallel.ForEach(bag, tfm =>
                tfm.RecalculateMatrixHeirarchy(
                    forceWorldRecalc: true,
                    setRenderMatrixNow: false,
                    childRecalcType: ELoopType.Parallel)
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
                        Debug.Out($"[XRWorldInstance] PlayState changed to {newState}, Engine.PlayMode.State={Engine.PlayMode.State}, SimulatePhysics={Engine.PlayMode.Configuration.SimulatePhysics}");
                        switch (newState)
                        {
                            case EPlayState.Playing:
                                // Keep physics enabled as soon as the world starts playing, even while play mode is still transitioning
                                bool playModeActive = Engine.PlayMode.State is EPlayModeState.Play or EPlayModeState.EnteringPlay;
                                Debug.Out($"[XRWorldInstance] PlayState=Playing, playModeActive={playModeActive}");
                                PhysicsEnabled = playModeActive && Engine.PlayMode.Configuration.SimulatePhysics;
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
            var moved = new HashSet<SceneNode>(System.Collections.Generic.ReferenceEqualityComparer.Instance);

            foreach (var node in scene.RootNodes)
            {
                if (node is null)
                    continue;

                // If this root node is marked editor-only, move the entire subtree into the hidden
                // editor scene instead of the visible world root list.
                if (node.IsEditorOnly)
                {
                    if (!IsInEditorScene(node))
                    {
                        moved.Add(node);
                        AddToEditorScene(node);
                    }
                    continue;
                }

                node.World = this;
                RootNodes.Add(node);
            }

            if (moved.Count > 0)
                _editorOnlyNodesByScene[scene] = moved;
        }

        private void UnloadVisibleScene(XRScene scene)
        {
            foreach (var node in scene.RootNodes)
            {
                if (node is null)
                    continue;

                if (!node.IsEditorOnly)
                {
                    RootNodes.Remove(node);
                    node.World = null;
                }
            }

            if (_editorOnlyNodesByScene.TryGetValue(scene, out var moved))
            {
                foreach (var editorOnly in moved)
                    RemoveFromEditorScene(editorOnly);
                _editorOnlyNodesByScene.Remove(scene);
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

                // Static BVH build is now scheduled through the job system; try again next frame.
                _ = xrMesh.BVHTree;
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
            {
                instance = new XRWorldInstance(targetWorld);
                WorldInstances.Add(targetWorld, instance);
            }
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
