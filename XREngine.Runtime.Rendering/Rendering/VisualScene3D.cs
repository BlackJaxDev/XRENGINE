using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Numerics;
using XREngine.Components.Scene.Mesh;
using XREngine.Data;
using XREngine.Data.Geometry;
using XREngine.Data.Profiling;
using XREngine.Data.Rendering;
using XREngine.Data.Trees;
using XREngine.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Compute;
using XREngine.Rendering.Info;
using YamlDotNet.Serialization;

namespace XREngine.Scene
{
    /// <summary>
    /// Represents a scene with special optimizations for rendering in 3D.
    /// </summary>
    public class VisualScene3D : VisualScene
    {
        internal static Action<VisualScene3D>? SwapBuffersHook { get; set; }

        [YamlIgnore]
        public Octree<RenderInfo3D> RenderTree { get; } = new Octree<RenderInfo3D>(new AABB());
        private readonly CpuBvhRenderTree<RenderInfo3D> _bvhRenderTree = new(new AABB());
        private AABB _sceneBounds;
        private bool _hasSceneBounds = false;
        private bool _isGpuDispatchActive = RuntimeEngine.Rendering.ResolveMeshSubmissionStrategy() != EMeshSubmissionStrategy.CpuDirect;
        private bool _isCpuGpuCommandMirrorActive = false;
        private bool _useGpuBvhActive = RuntimeEngine.EffectiveSettings.UseGpuBvh;
        private ECpuSceneCullingStructure _cpuSceneCullingStructureActive = RuntimeEngine.EffectiveSettings.CpuSceneCullingStructure;
        private I3DRenderTree<RenderInfo3D> ActiveCpuRenderTree
            => _cpuSceneCullingStructureActive == ECpuSceneCullingStructure.Bvh ? _bvhRenderTree : RenderTree;
        public BvhRaycastDispatcher BvhRaycasts { get; } = new();

        public VisualScene3D()
        {
            GPUCommands.UseGpuBvh = _useGpuBvhActive;
            GPUCommands.UseInternalBvh = _useGpuBvhActive; // Enable internal command BVH for GPU culling
            BvhRaycasts.WarmShaders();
        }

        public void SetBounds(AABB bounds)
        {
            _sceneBounds = bounds;
            _hasSceneBounds = true;

            if (_isGpuDispatchActive || _isCpuGpuCommandMirrorActive)
                GPUCommands.Bounds = bounds;
            else
                ActiveCpuRenderTree.Remake(bounds);

            //Lights.LightProbeTree.Remake(bounds);
        }

        public override IRenderTree GenericRenderTree => ActiveCpuRenderTree;

        public override void DebugRender(IRuntimeCullingCamera? camera, bool onlyContainingItems = false)
            => ActiveCpuRenderTree.DebugRender(camera?.WorldFrustum(), RenderAABB, onlyContainingItems);

        private void RenderAABB(Vector3 extents, Vector3 center, Color color)
            => RuntimeEngine.Rendering.Debug.RenderAABB(extents, center, false, color);

        private static readonly Action<(OctreeNodeBase node, bool intersects)> RenderSpatialTreeNodeAction = RenderSpatialTreeNode;

        public void DebugRenderSpatialTreeNodes(IRuntimeCullingCamera? camera, bool onlyContainingItems = false)
            => ActiveCpuRenderTree.CollectVisibleNodes(camera?.WorldFrustum(), onlyContainingItems, RenderSpatialTreeNodeAction);

        private static void RenderSpatialTreeNode((OctreeNodeBase node, bool intersects) data)
        {
            var host = RuntimeRenderingHostServices.Current;
            var color = data.intersects
                ? host.OctreeIntersectedBoundsColor
                : host.OctreeContainedBoundsColor;

            RuntimeEngine.Rendering.Debug.RenderAABB(data.node.Bounds.HalfExtents, data.node.Center, false, color);
        }

        public void RaycastAsync(
            Segment worldSegment,
            SortedDictionary<float, List<(RenderInfo3D item, object? data)>> items,
            Func<RenderInfo3D, Segment, (float? distance, object? data)> directTest,
            Action<SortedDictionary<float, List<(RenderInfo3D item, object? data)>>> finishedCallback)
        {
            if (_cpuSceneCullingStructureActive == ECpuSceneCullingStructure.Bvh)
                _bvhRenderTree.RaycastAsync(worldSegment, items, directTest, finishedCallback);
            else
                RenderTree.RaycastAsync(worldSegment, items, directTest, finishedCallback);
        }

        public void Raycast(
            Segment worldSegment,
            SortedDictionary<float, List<(RenderInfo3D item, object? data)>> items,
            Func<RenderInfo3D, Segment, (float? distance, object? data)> directTest)
        {
            if (_cpuSceneCullingStructureActive == ECpuSceneCullingStructure.Bvh)
                _bvhRenderTree.Raycast(worldSegment, items, directTest);
            else
                RenderTree.Raycast(worldSegment, items, directTest);
        }

        public override void CollectRenderedItems(
            RenderCommandCollection meshRenderCommands,
            IRuntimeCullingCamera? camera,
            bool cullWithFrustum,
            Func<IRuntimeCullingCamera>? cullingCameraOverride,
            IVolume? collectionVolumeOverride,
            bool collectMirrors)
        {
            using var sample = RuntimeEngine.Profiler.Start("VisualScene3D.CollectRenderedItems", ProfilerScopeKind.AlwaysOnHotPathLoop);

            IRuntimeCullingCamera? cullingCamera = cullingCameraOverride?.Invoke() ?? camera;
            IVolume? collectionVolume = collectionVolumeOverride ?? (cullWithFrustum ? cullingCamera?.WorldFrustum() : null);
            CollectRenderedItems(meshRenderCommands, collectionVolume, camera, collectMirrors);
        }
        public void CollectRenderedItems(RenderCommandCollection commands, IVolume? collectionVolume, IRuntimeCullingCamera? camera, bool collectMirrors)
        {
            using var sample = RuntimeEngine.Profiler.Start("VisualScene3D.CollectRenderedItems", ProfilerScopeKind.AlwaysOnHotPathLoop);
            int visibleRenderables = 0;
            bool modelDiagActive = ModelRenderDiagnostics.HasActiveTrace;
            int commandsBefore = modelDiagActive ? commands.GetUpdatingCommandCount() : 0;

            bool IntersectionTest(RenderInfo3D item, IVolume? cullingVolume, bool containsOnly)
            {
                bool allowed = item.AllowRender(cullingVolume, commands, camera, containsOnly, collectMirrors);
                if (RenderDiagnosticsFlags.SkinCullRejectDiag)
                {
                    item.DiagIntersectGen = _collectGen;
                    item.DiagIntersectResult = allowed;
                }
                if (!allowed && modelDiagActive)
                    ModelRenderDiagnostics.LogRejected(item, cullingVolume, commands, camera, containsOnly, collectMirrors);
                return allowed;
            }

            void AddRenderCommands(RenderInfo3D renderable)
            {
                visibleRenderables++;
                if (RenderDiagnosticsFlags.SkinCullRejectDiag)
                    renderable.DiagCollectedGen = _collectGen;
                if (modelDiagActive)
                    ModelRenderDiagnostics.LogVisibilityAccepted(renderable, commands, camera, collectMirrors);
                renderable.CollectCommands(commands, camera);
            }

            if (IsGpuCulling)
            {
                using var gpuSample = RuntimeEngine.Profiler.Start("VisualScene3D.CollectRenderedItems.Gpu", ProfilerScopeKind.AlwaysOnHotPathLoop);
                visibleRenderables = CollectRenderedItemsGpu(commands, collectionVolume, camera, collectMirrors, modelDiagActive);
            }
            else
            {
                I3DRenderTree<RenderInfo3D> cpuTree = ActiveCpuRenderTree;
                int cpuCommandsBefore = commands.GetUpdatingCommandCount();
                long collectStart = System.Diagnostics.Stopwatch.GetTimestamp();
                using var cpuSample = RuntimeEngine.Profiler.Start(
                    _cpuSceneCullingStructureActive == ECpuSceneCullingStructure.Bvh
                        ? "VisualScene3D.CollectRenderedItems.CpuBvh"
                        : "VisualScene3D.CollectRenderedItems.Octree",
                    ProfilerScopeKind.AlwaysOnHotPathLoop);
                cpuTree.CollectVisible(collectionVolume, false, AddRenderCommands, IntersectionTest);
                long collectTicks = System.Diagnostics.Stopwatch.GetTimestamp() - collectStart;
                int emittedCommands = Math.Max(0, commands.GetUpdatingCommandCount() - cpuCommandsBefore);
                RuntimeEngine.Rendering.Stats.Octree.RecordOctreeCollect(visibleRenderables, emittedCommands);
                RuntimeEngine.Rendering.Stats.Octree.RecordCpuSpatialTreeStats(
                    _cpuSceneCullingStructureActive.ToString(),
                    cpuTree.GetOccupancyStats(),
                    collectTicks);
            }

            if (modelDiagActive)
            {
                int emittedCommands = Math.Max(0, commands.GetUpdatingCommandCount() - commandsBefore);
                ModelRenderDiagnostics.LogCollectSummary(
                    this,
                    _renderables.Count,
                    visibleRenderables,
                    emittedCommands,
                    IsGpuCulling,
                    camera,
                    collectMirrors);
            }
        }

        public IReadOnlyList<RenderInfo3D> Renderables => _renderables;
        private readonly List<RenderInfo3D> _renderables = [];
        private readonly HashSet<RenderInfo3D> _renderableSet = [];
        private readonly ConcurrentQueue<(RenderInfo3D renderable, bool add)> _pendingRenderableOperations = new(); // staged until PreCollectVisible runs on the collect visible thread
        private bool IsGpuCulling => _isGpuDispatchActive;
        private readonly HashSet<RenderableMesh> _skinnedMeshes = new();
        private uint _lastGpuVisibleDraws;
        private uint _lastGpuVisibleInstances;

        public (uint Draws, uint Instances) LastGpuVisibility => (_lastGpuVisibleDraws, _lastGpuVisibleInstances);

        public override void RecordGpuVisibility(uint draws, uint instances)
        {
            _lastGpuVisibleDraws = draws;
            _lastGpuVisibleInstances = instances;
        }

        public void AddRenderable(RenderInfo3D renderable)
            => _pendingRenderableOperations.Enqueue((renderable, true));

        public void RemoveRenderable(RenderInfo3D renderable)
            => _pendingRenderableOperations.Enqueue((renderable, false));

        public override void GlobalCollectVisible()
        {
            base.GlobalCollectVisible();
            SyncCpuSceneCullingStructurePreference();
            ProcessPendingRenderableOperations();

            if (!_isGpuDispatchActive)
            {
                if (RenderDiagnosticsFlags.SkinCullRejectDiag)
                    EvaluateSkinCullRejectDiagnostics(_collectGen);
                _collectGen++;
                RefreshSkinnedCpuCullingBounds();
                ActiveCpuRenderTree.Swap();
            }
        }

        // Per-collect generation counter for the SkinCullRejectDiag stage log. Incremented once per
        // CPU GlobalCollectVisible; all CollectRenderedItems passes within a frame share one value so
        // a drop is "collected in any viewport last gen, no viewport this gen".
        private long _collectGen;

        private void EvaluateSkinCullRejectDiagnostics(long finishedGen)
        {
            foreach (RenderableMesh mesh in _skinnedMeshes)
            {
                RenderInfo3D ri = mesh.RenderInfo;
                bool collectedThis = ri.DiagCollectedGen == finishedGen;
                if (mesh.DiagWasCollectedLastEval && !collectedThis)
                {
                    bool tested = ri.DiagIntersectGen == finishedGen;
                    string stage = !tested
                        ? "bvh-node"
                        : (ri.DiagIntersectResult ? "downstream" : "bone-override");
                    RuntimeEngine.LogWarning(mesh.BuildSkinCullRejectPayload(stage, finishedGen));
                }

                mesh.DiagWasCollectedLastEval = collectedThis;
            }
        }

        public override void GlobalPreRender()
        {
            base.GlobalPreRender();
            BvhRaycasts.ProcessDispatches();
            
            RuntimeRenderingHostServices.Current.ProcessGpuPhysicsChainDispatches();
        }

        public override void GlobalPostRender()
        {
            base.GlobalPostRender();
            RuntimeRenderingHostServices.Current.ProcessGpuPhysicsChainCompletions();
            BvhRaycasts.ProcessCompletions();
        }

        public override void GlobalSwapBuffers()
        {
            SyncCpuGpuCommandMirrorState();
            base.GlobalSwapBuffers();
            SwapBuffersHook?.Invoke(this);
        }

        private bool ShouldMaintainCpuGpuCommandMirror()
        {
            // The CPU?GPU command mirror is only needed by Surfel-GI consumers.
            // CpuDirect no longer trusts any GPU compute cull output (C-CPU-2), so
            // mirroring would be dead weight on the CpuDirect path. The GPU paths
            // (Instrumented/ZeroReadback) already maintain the GPUScene directly.
            foreach (XRViewport viewport in RuntimeEngine.EnumerateActiveViewports())
            {
                switch (viewport.RenderPipeline)
                {
                    case DefaultRenderPipeline pipeline when pipeline.UsesSurfelGI:
                    case SurfelDebugRenderPipeline:
                        return true;
                }
            }

            return false;
        }

        private void SyncCpuGpuCommandMirrorState()
        {
            if (_isGpuDispatchActive)
            {
                _isCpuGpuCommandMirrorActive = false;
                return;
            }

            bool shouldMirror = ShouldMaintainCpuGpuCommandMirror();
            if (shouldMirror == _isCpuGpuCommandMirrorActive)
                return;

            _isCpuGpuCommandMirrorActive = shouldMirror;

            if (shouldMirror && _hasSceneBounds)
                GPUCommands.Bounds = _sceneBounds;

            foreach (var renderable in _renderables)
            {
                if (renderable.RenderCommands.Count == 0 || renderable.RenderCommands[0] is not IRenderCommandMesh meshCmd)
                    continue;

                if (shouldMirror)
                {
                    if (meshCmd.GPUCommandIndex == uint.MaxValue)
                        GPUCommands.Add(renderable);
                }
                else if (meshCmd.GPUCommandIndex != uint.MaxValue)
                {
                    GPUCommands.Remove(renderable);
                }
            }
        }

        // NOTE: skinning/blendshape compute prepass is now dispatched per-viewport from the
        // swapped visible command list (see XRViewport.Render).

        public void ApplyRenderDispatchPreference(bool useGpu)
        {
            // Always re-resolve through the central strategy so backend capability changes
            // override any stale caller value.
            useGpu = RuntimeEngine.Rendering.ResolveMeshSubmissionStrategy(useGpu) != EMeshSubmissionStrategy.CpuDirect;

            if (useGpu == _isGpuDispatchActive)
                return;

            if (useGpu)
            {
                // GPU dispatch: remove from RenderTree, keep in GPUCommands.
                ActiveCpuRenderTree.RemoveRange(_renderables);
                ActiveCpuRenderTree.Swap();

                if (_hasSceneBounds)
                    GPUCommands.Bounds = _sceneBounds;

                // GPUCommands is always populated (see below), so renderables should
                // already be present. Re-add defensively for cases where they were
                // removed during an earlier CPU->GPU transition that was interrupted.
                foreach (var renderable in _renderables)
                {
                    if (renderable.RenderCommands.Count > 0 &&
                        renderable.RenderCommands[0] is IRenderCommandMesh meshCmd &&
                        meshCmd.GPUCommandIndex == uint.MaxValue)
                    {
                        GPUCommands.Add(renderable);
                    }
                }
            }
            else
            {
                // CPU dispatch: repopulate the RenderTree for CPU draw dispatch.
                // GPUCommands mirroring is handled separately and only enabled when
                // an active surfel consumer actually needs it.
                if (_hasSceneBounds)
                    ActiveCpuRenderTree.Remake(_sceneBounds);
                else
                    ActiveCpuRenderTree.Remake();

                ActiveCpuRenderTree.AddRange(_renderables);
                ActiveCpuRenderTree.Swap();
            }

            _isGpuDispatchActive = useGpu;
            SyncCpuGpuCommandMirrorState();
        }

        public void ApplyGpuBvhPreference(bool useGpuBvh)
        {
            if (_useGpuBvhActive == useGpuBvh)
                return;

            _useGpuBvhActive = useGpuBvh;
            GPUCommands.UseGpuBvh = useGpuBvh;
            GPUCommands.UseInternalBvh = useGpuBvh; // Sync internal BVH with UseGpuBvh

            if (useGpuBvh)
            {
                Debug.Out("[VisualScene3D] GPU BVH enabled; warming shaders and rebuilding GPU buffers for traversal.");
                BvhRaycasts.SetEnabled(true, "settings toggled on");
                BvhRaycasts.WarmShaders();
            }
            else
            {
                Debug.LogWarning("[VisualScene3D] GPU BVH disabled for scene traversal; explicit BVH raycasts remain available for tools and picking.");
            }
        }

        public void ApplyCpuSceneCullingStructurePreference(ECpuSceneCullingStructure structure)
        {
            if (_cpuSceneCullingStructureActive == structure)
                return;

            I3DRenderTree<RenderInfo3D> oldTree = ActiveCpuRenderTree;
            if (!_isGpuDispatchActive)
            {
                oldTree.RemoveRange(_renderables);
                oldTree.Swap();
            }

            _cpuSceneCullingStructureActive = structure;
            I3DRenderTree<RenderInfo3D> newTree = ActiveCpuRenderTree;

            if (_hasSceneBounds)
                newTree.Remake(_sceneBounds);
            else
                newTree.Remake();

            if (!_isGpuDispatchActive)
            {
                newTree.AddRange(_renderables);
                newTree.Swap();
            }

            Debug.Out($"[VisualScene3D] CPU scene culling structure set to {structure}.");
        }

        public override IEnumerator<RenderInfo> GetEnumerator()
        {
            foreach (var renderable in _renderables)
                yield return renderable;
        }

        private void ProcessPendingRenderableOperations()
        {
            while (_pendingRenderableOperations.TryDequeue(out var operation))
            {
                if (operation.add)
                {
                    if (_renderableSet.Add(operation.renderable))
                    {
                        _renderables.Add(operation.renderable);
                        operation.renderable.SwapBuffersCallback += OnRenderableSwapBuffers;
                        TrackRenderable(operation.renderable);

                        if (_isGpuDispatchActive || _isCpuGpuCommandMirrorActive)
                            GPUCommands.Add(operation.renderable);

                        if (!_isGpuDispatchActive)
                            ActiveCpuRenderTree.Add(operation.renderable);

                        ModelRenderDiagnostics.LogSceneRegistration(
                            this,
                            operation.renderable,
                            added: true,
                            _isGpuDispatchActive,
                            _isCpuGpuCommandMirrorActive,
                            _renderables.Count);
                    }
                }
                else if (_renderableSet.Remove(operation.renderable))
                {
                    UntrackRenderable(operation.renderable);
                    operation.renderable.SwapBuffersCallback -= OnRenderableSwapBuffers;

                    GPUCommands.Remove(operation.renderable);

                    if (!_isGpuDispatchActive)
                        ActiveCpuRenderTree.Remove(operation.renderable);

                    _renderables.Remove(operation.renderable);
                    ModelRenderDiagnostics.LogSceneRegistration(
                        this,
                        operation.renderable,
                        added: false,
                        _isGpuDispatchActive,
                        _isCpuGpuCommandMirrorActive,
                        _renderables.Count);
                }
            }
        }

        private void OnRenderableSwapBuffers(RenderInfo info, RenderCommand command)
        {
            if ((_isGpuDispatchActive || _isCpuGpuCommandMirrorActive)
                && command is IRenderCommandMesh meshCmd)
                GPUCommands.TryUpdateMeshCommand(info, meshCmd);
        }

        private void RefreshSkinnedCpuCullingBounds()
        {
            foreach (RenderableMesh mesh in _skinnedMeshes)
                _ = mesh.RefreshSkinnedCullingBoundsForSceneCulling();
        }

        private void SyncCpuSceneCullingStructurePreference()
        {
            ECpuSceneCullingStructure structure = RuntimeEngine.EffectiveSettings.CpuSceneCullingStructure;
            if (structure != _cpuSceneCullingStructureActive)
                ApplyCpuSceneCullingStructurePreference(structure);
        }

        /// <summary>
        /// Ensure mesh BVHs are available before GPU culling/dispatch executes.
        /// </summary>
        internal void PrepareGpuCulling()
        {
            if (!IsGpuCulling)
                return;

            foreach (var mesh in _skinnedMeshes)
            {
                // Use cached skinned BVHs during culling; avoid triggering rebuilds on every bone update.
                _ = mesh.GetSkinnedBvh(allowRebuild: false);
            }
        }

        private int CollectRenderedItemsGpu(RenderCommandCollection commands, IVolume? collectionVolume, IRuntimeCullingCamera? camera, bool collectMirrors, bool modelDiagActive)
        {
            using var sample = RuntimeEngine.Profiler.Start("VisualScene3D.CollectRenderedItemsGpu");
            int visibleRenderables = 0;

            // GPU dispatch path: the GPU performs the authoritative frustum/BVH cull on its own
            // command buffers for regular scene cameras. Shadow cameras can provide custom
            // collection volumes (directional cascade slices, atlas tiles, cubemap faces) that
            // are narrower than the render frustum, so keep those CPU-side or every cascade can
            // submit the same caster set.
            IVolume? allowRenderVolume = commands.IsOwnedByShadowPipeline || modelDiagActive
                ? collectionVolume
                : null;

            // Iterate by index to avoid per-frame ToArray() allocation.
            // _renderables is only mutated in PreCollectVisible (same thread), so direct iteration is safe.
            for (int i = 0; i < _renderables.Count; i++)
            {
                var renderable = _renderables[i];
                bool allowed = renderable.AllowRender(allowRenderVolume, commands, camera, false, collectMirrors);
                if (!allowed)
                {
                    if (modelDiagActive)
                        ModelRenderDiagnostics.LogRejected(renderable, collectionVolume, commands, camera, containsOnly: false, collectMirrors);
                    continue;
                }

                visibleRenderables++;
                if (modelDiagActive)
                    ModelRenderDiagnostics.LogVisibilityAccepted(renderable, commands, camera, collectMirrors);
                renderable.CollectCommands(commands, camera);
            }

            return visibleRenderables;
        }

        private void TrackRenderable(RenderInfo3D renderable)
        {
            if (renderable.OwnerRenderableMesh is not RenderableMesh mesh)
                return;

            if (mesh.IsSkinned)
                _skinnedMeshes.Add(mesh);
        }

        private void UntrackRenderable(RenderInfo3D renderable)
        {
            if (renderable.OwnerRenderableMesh is not RenderableMesh mesh)
                return;

            _skinnedMeshes.Remove(mesh);
        }

    }
}
