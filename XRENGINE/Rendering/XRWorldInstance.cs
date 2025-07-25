﻿using Extensions;
using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.CompilerServices;
using XREngine.Audio;
using XREngine.Components;
using XREngine.Components.Scene.Mesh;
using XREngine.Data.Core;
using XREngine.Data.Geometry;
using XREngine.Data.Trees;
using XREngine.Rendering.Info;
using XREngine.Scene;
using XREngine.Components.Animation;
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
        public void FixedUpdate()
        {
            TickGroup(ETickGroup.PrePhysics);
            PhysicsScene.StepSimulation();
            //Task.WaitAll(Task.Run(PhysicsScene.StepSimulation), Task.Run(TickDuring));
            TickGroup(ETickGroup.PostPhysics);
        }

        public bool IsPlaying { get; private set; }

        public async Task BeginPlay()
        {
            PreBeginPlay?.Invoke(this);
            PhysicsScene.Initialize();
            await BeginPlayInternal();
            LinkTimeCallbacks();
            PostBeginPlay?.Invoke(this);
        }

        protected virtual async Task BeginPlayInternal()
        {
            VisualScene.GenericRenderTree.Swap();

            //Recalculate all transforms before activating nodes, in case any cross-dependencies exist
            foreach (SceneNode node in RootNodes)
                await node.Transform.RecalculateMatrixHeirarchy(true, true, Engine.Rendering.Settings.RecalcChildMatricesLoopType);

            foreach (SceneNode node in RootNodes)
                if (node.IsActiveSelf)
                    node.OnActivated();

            IsPlaying = true;
        }

        public void EndPlay()
        {
            PreEndPlay?.Invoke(this);
            UnlinkTimeCallbacks();
            PhysicsScene.Destroy();
            EndPlayInternal();
            PostEndPlay?.Invoke(this);
        }
        protected virtual void EndPlayInternal()
        {
            VisualScene.GenericRenderTree.Swap();
            foreach (SceneNode node in RootNodes)
                if (node.IsActiveSelf)
                    node.OnDeactivated();
            IsPlaying = false;
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
            Lights.CollectVisibleItems();
        }

        private void ApplyRenderMatrixChanges()
        {
            //using var t = Engine.Profiler.Start();
            //var arr = ArrayPool<(TransformBase tfm, Matrix4x4 renderMatrix)>.Shared.Rent(_pushToRenderSnapshot.Count);
            //await Task.WhenAll(_pushToRenderSnapshot.Select(x => x.tfm.SetRenderMatrix(x.renderMatrix, false)));
            //_pushToRenderSnapshot.Clear();
            while (_pushToRenderSnapshot.TryDequeue(out (TransformBase tfm, Matrix4x4 renderMatrix) item))
                item.tfm.SetRenderMatrix(item.renderMatrix, false);
        }

        private void GlobalSwapBuffers()
        {
            //using var t = Engine.Profiler.Start();

            ApplyRenderMatrixChanges();
            VisualScene.GlobalSwapBuffers();
            //PhysicsScene.SwapDebugBuffers();
            Lights.SwapBuffers();
        }

        /// <summary>
        /// Called by windows on the render thread, right before rendering all viewports.
        /// </summary>
        public void GlobalPreRender()
        {
            VisualScene.GlobalPreRender();
            Lights.RenderShadowMaps(false);
        }

        /// <summary>
        /// Called by windows on the render thread, right after rendering all viewports.
        /// </summary>
        public void GlobalPostRender()
        {
            //using var d = Profiler.Start();
            VisualScene.GlobalPostRender();
            //Lights.CaptureLightProbes();
        }

        private void PreUpdate()
        {
            _pushToRenderWrite.Clear();
            _invalidTransforms.ForEach(x => x.Value.Clear());
        }

        private void PostUpdate()
        {
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
            => _pushToRenderWrite.Enqueue((transform, worldMatrix));

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
        private static async Task RecalcTransformDepthAsync(IEnumerable<TransformBase> bag)
        {
            await Task.WhenAll([.. bag.Select(x => Task.Run(() => x.RecalculateMatrices(true, false)))]);
        }

        private static async Task RecalcTransformDepthParallel(IEnumerable<TransformBase> bag)
        {
            if (!bag.Any())
                return;

            await Task.Run(() => Parallel.ForEach(bag, async (TransformBase tfm) =>
            {
                await tfm.RecalculateMatrixHeirarchy(true, false, Engine.Rendering.ELoopType.Parallel);
            }));
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
                            foreach (var scene in TargetWorld.Scenes)
                                UnloadScene(scene);
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
            PhysicsScene.RaycastSingleAsync(worldSegment, layerMask, filter, orderedResults, finishedCallback);
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
            Action<SortedDictionary<float, List<(RenderInfo3D item, object? data)>>> finishedCallback)
            => RaycastOctreeAsync(
                cameraComponent.Camera.GetWorldSegment(normalizedScreenPoint),
                orderedResults,
                finishedCallback);

        /// <summary>
        /// Raycasts the octree and updates a sorted list of items that were hit.
        /// Clears the list before adding results.
        /// </summary>
        /// <param name="worldSegment"></param>
        /// <param name="orderedResults"></param>
        public void RaycastOctreeAsync(
            Segment worldSegment,
            SortedDictionary<float, List<(RenderInfo3D item, object? data)>> orderedResults,
            Action<SortedDictionary<float, List<(RenderInfo3D item, object? data)>>> finishedCallback)
        {
            //orderedResults.Clear();
            VisualScene.RaycastAsync(worldSegment, orderedResults, DirectItemTest, finishedCallback);
        }

        private static (float? distance, object? data) DirectItemTest(RenderInfo3D item, Segment segment)
        {
            if (item is not RenderInfo renderable)
                return (null, null);
            
            switch (renderable.Owner)
            {
                //case ModelComponent model:
                //    {
                //        float? dist = model.Intersect(segment, out Triangle? tri);
                //        return (dist, tri);
                //    }
                case TransformBase transform:
                    {
                        //Debug.Out($"DirectItemTest: {transform.Name}");

                        var viewNode = Engine.State.MainPlayer.ControlledPawn?.GetCamera()?.Transform;
                        if ((viewNode != null && transform == viewNode) || transform.Capsule is null)
                            return (null, null);

                        Capsule c = transform.Capsule.Value;
                        if (!c.IntersectsSegment(segment, out Vector3[] points))
                            break;
                        
                        //Get closest point distance
                        float min = float.MaxValue;
                        Vector3 bestPoint = Vector3.Zero;
                        for (int i = 0; i < points.Length; i++)
                        {
                            float dist = (points[i] - segment.Start).LengthSquared();
                            if (dist < min)
                            {
                                min = dist;
                                bestPoint = points[i];
                            }
                        }
                        return ((float)Math.Sqrt(min), bestPoint);
                    }
            }
            return (null, null);
        }

        public static XRWorldInstance GetOrInitWorld(XRWorld targetWorld)
        {
            if (!WorldInstances.TryGetValue(targetWorld, out var instance))
                WorldInstances.Add(targetWorld, instance = new(targetWorld));
            return instance;
        }
    }
}
