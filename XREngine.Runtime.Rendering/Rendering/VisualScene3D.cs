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
        private AABB _sceneBounds;
        private bool _hasSceneBounds = false;
        private bool _isGpuDispatchActive = Engine.Rendering.ResolveMeshSubmissionStrategy() != EMeshSubmissionStrategy.CpuDirect;
        private bool _isCpuGpuCommandMirrorActive = false;
        private bool _useGpuBvhActive = Engine.EffectiveSettings.UseGpuBvh;
        public BvhRaycastDispatcher BvhRaycasts { get; } = new();

        public VisualScene3D()
        {
            GPUCommands.UseGpuBvh = _useGpuBvhActive;
            GPUCommands.UseInternalBvh = _useGpuBvhActive; // Enable internal command BVH for GPU culling
            if (_useGpuBvhActive)
                BvhRaycasts.WarmShaders();
            else
                BvhRaycasts.SetEnabled(false, "initial settings disabled");
        }

        public void SetBounds(AABB bounds)
        {
            _sceneBounds = bounds;
            _hasSceneBounds = true;

            if (_isGpuDispatchActive || _isCpuGpuCommandMirrorActive)
                GPUCommands.Bounds = bounds;
            else
                RenderTree.Remake(bounds);

            //Lights.LightProbeTree.Remake(bounds);
        }

        public override IRenderTree GenericRenderTree => RenderTree;

        public override void DebugRender(IRuntimeCullingCamera? camera, bool onlyContainingItems = false)
            => RenderTree.DebugRender(camera?.WorldFrustum(), onlyContainingItems, RenderAABB);

        private void RenderAABB(Vector3 extents, Vector3 center, Color color)
            => Engine.Rendering.Debug.RenderAABB(extents, center, false, color);

        public void RaycastAsync(
            Segment worldSegment,
            SortedDictionary<float, List<(RenderInfo3D item, object? data)>> items,
            Func<RenderInfo3D, Segment, (float? distance, object? data)> directTest,
            Action<SortedDictionary<float, List<(RenderInfo3D item, object? data)>>> finishedCallback)
            => RenderTree.RaycastAsync(worldSegment, items, directTest, finishedCallback);

        public void Raycast(
            Segment worldSegment,
            SortedDictionary<float, List<(RenderInfo3D item, object? data)>> items,
            Func<RenderInfo3D, Segment, (float? distance, object? data)> directTest)
            => RenderTree.Raycast(worldSegment, items, directTest);

        public override void CollectRenderedItems(
            RenderCommandCollection meshRenderCommands,
            IRuntimeCullingCamera? camera,
            bool cullWithFrustum,
            Func<IRuntimeCullingCamera>? cullingCameraOverride,
            IVolume? collectionVolumeOverride,
            bool collectMirrors)
        {
            using var sample = Engine.Profiler.Start("VisualScene3D.CollectRenderedItems", ProfilerScopeKind.AlwaysOnHotPathLoop);

            IRuntimeCullingCamera? cullingCamera = cullingCameraOverride?.Invoke() ?? camera;
            IVolume? collectionVolume = collectionVolumeOverride ?? (cullWithFrustum ? cullingCamera?.WorldFrustum() : null);
            CollectRenderedItems(meshRenderCommands, collectionVolume, camera, collectMirrors);
        }
        public void CollectRenderedItems(RenderCommandCollection commands, IVolume? collectionVolume, IRuntimeCullingCamera? camera, bool collectMirrors)
        {
            using var sample = Engine.Profiler.Start("VisualScene3D.CollectRenderedItems", ProfilerScopeKind.AlwaysOnHotPathLoop);
            int visibleRenderables = 0;
            bool modelDiagActive = ModelRenderDiagnostics.HasActiveTrace;
            int commandsBefore = modelDiagActive ? commands.GetUpdatingCommandCount() : 0;

            bool IntersectionTest(RenderInfo3D item, IVolume? cullingVolume, bool containsOnly)
            {
                bool allowed = item.AllowRender(cullingVolume, commands, camera, containsOnly, collectMirrors);
                if (!allowed && modelDiagActive)
                    ModelRenderDiagnostics.LogRejected(item, cullingVolume, commands, camera, containsOnly, collectMirrors);
                return allowed;
            }

            void AddRenderCommands(ITreeItem item)
            {
                if (item is RenderInfo renderable)
                {
                    visibleRenderables++;
                    if (modelDiagActive && renderable is RenderInfo3D renderInfo3D)
                        ModelRenderDiagnostics.LogVisibilityAccepted(renderInfo3D, commands, camera, collectMirrors);
                    renderable.CollectCommands(commands, camera);
                }
            }

            if (IsGpuCulling)
            {
                using var gpuSample = Engine.Profiler.Start("VisualScene3D.CollectRenderedItems.Gpu", ProfilerScopeKind.AlwaysOnHotPathLoop);
                visibleRenderables = CollectRenderedItemsGpu(commands, collectionVolume, camera, collectMirrors, modelDiagActive);
            }
            else
            {
                int cpuCommandsBefore = commands.GetUpdatingCommandCount();
                using var octreeSample = Engine.Profiler.Start("VisualScene3D.CollectRenderedItems.Octree", ProfilerScopeKind.AlwaysOnHotPathLoop);
                RenderTree.CollectVisible(collectionVolume, false, AddRenderCommands, IntersectionTest);
                int emittedCommands = Math.Max(0, commands.GetUpdatingCommandCount() - cpuCommandsBefore);
                Engine.Rendering.Stats.RecordOctreeCollect(visibleRenderables, emittedCommands);
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
            ProcessPendingRenderableOperations();

            if (!_isGpuDispatchActive)
                RenderTree.Swap();
        }

        public override void GlobalPreRender()
        {
            base.GlobalPreRender();
            if (_useGpuBvhActive)
                BvhRaycasts.ProcessDispatches();
            
            RuntimeRenderingHostServices.Current.ProcessGpuPhysicsChainDispatches();
        }

        public override void GlobalPostRender()
        {
            base.GlobalPostRender();
            RuntimeRenderingHostServices.Current.ProcessGpuPhysicsChainCompletions();
            if (_useGpuBvhActive)
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
            // The CPU↔GPU command mirror is only needed by Surfel-GI consumers.
            // CpuDirect no longer trusts any GPU compute cull output (C-CPU-2), so
            // mirroring would be dead weight on the CpuDirect path. The GPU paths
            // (Instrumented/ZeroReadback) already maintain the GPUScene directly.
            foreach (XRViewport viewport in Engine.EnumerateActiveViewports())
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
            useGpu = Engine.Rendering.ResolveMeshSubmissionStrategy(useGpu) != EMeshSubmissionStrategy.CpuDirect;

            if (useGpu == _isGpuDispatchActive)
                return;

            if (useGpu)
            {
                // GPU dispatch: remove from RenderTree, keep in GPUCommands.
                RenderTree.RemoveRange(_renderables);
                RenderTree.Swap();

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
                    RenderTree.Remake(_sceneBounds);
                else
                    RenderTree.Remake();

                RenderTree.AddRange(_renderables);
                RenderTree.Swap();
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
                Debug.LogWarning("[VisualScene3D] GPU BVH disabled; falling back to CPU octree traversal.");
                BvhRaycasts.SetEnabled(false, "disabled by settings");
            }
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
                            RenderTree.Add(operation.renderable);

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
                        RenderTree.Remove(operation.renderable);

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
            using var sample = Engine.Profiler.Start("VisualScene3D.CollectRenderedItemsGpu");
            int visibleRenderables = 0;

            // GPU dispatch path: the GPU performs the authoritative frustum/BVH cull on its own
            // command buffers. Doing a redundant CPU frustum-vs-box test on every renderable here
            // is pure CPU overhead that scales O(N) and was a major contributor to GPU-path
            // perf regressions vs. CpuDirect on Sponza-class scenes. We still need the layer,
            // shadow, and mirror filters from AllowRender, so we pass cullingVolume=null which
            // short-circuits the Intersects test to "contained".
            IVolume? allowRenderVolume = modelDiagActive ? collectionVolume : null;

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
            if (renderable.Owner is not RenderableMesh mesh)
                return;

            if (mesh.IsSkinned)
            {
                _skinnedMeshes.Add(mesh);
            }
            else
            {
                EnsureStaticMeshBvh(mesh);
            }
        }

        private void UntrackRenderable(RenderInfo3D renderable)
        {
            if (renderable.Owner is not RenderableMesh mesh)
                return;

            _skinnedMeshes.Remove(mesh);
        }

        private static void EnsureStaticMeshBvh(RenderableMesh mesh)
        {
            var xrMesh = mesh.CurrentLODRenderer?.Mesh;
            if (xrMesh is null)
                return;

            if (xrMesh.BVHTree is null)
                _ = xrMesh.BVHTree;
        }
    }
}
