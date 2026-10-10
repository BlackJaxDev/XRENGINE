using System.Collections.Generic;
using System.Threading;
using XREngine.Data.Geometry;
using XREngine.Rendering;

namespace XREngine.Components.Scene.Mesh
{
    public partial class RenderableMesh
    {
        private readonly Dictionary<XRMeshRenderer, bool> _boneTrackingByRenderer = [];
        private int _committedBoundsStateDirty = 1;
        private int _usesCommittedWorldBounds;
        private XRMeshRenderer? _committedBoundsRenderer;
        private ulong _committedBoundsGeneration;
        private int _coveredShadowCasterLayerMask;

        /// <summary>Gets the number of bones with CPU bounds subscriptions on this mesh.</summary>
        internal int TrackedSkinnedBoneCount => _trackedSkinnedBones.Count;

        /// <summary>Gets whether this mesh has queued render-matrix work.</summary>
        internal bool HasPendingRenderMatrixUpdate
            => Volatile.Read(ref _pendingRenderMatrixQueued) != 0;

        /// <summary>Gets whether the active LOD uses a valid committed world bound.</summary>
        internal bool UsesCommittedWorldBounds
        {
            get
            {
                XRMeshRenderer? renderer = CurrentLODRenderer;
                return Volatile.Read(ref _usesCommittedWorldBounds) != 0 &&
                    ReferenceEquals(renderer, _committedBoundsRenderer) &&
                    renderer?.HasCompleteGpuDrivenBoneCoverage == true &&
                    renderer.TryGetCommittedWorldBounds(out AABB bounds, out _) && bounds.IsValid;
            }
        }

        private void RegisterCommittedBoundsRenderer(XRMeshRenderer renderer)
        {
            _boneTrackingByRenderer.Add(renderer, true);
            renderer.GpuDrivenBoneCoverageChanged += RendererGpuBoneCoverageChanged;
            renderer.CommittedOutputChanged += RendererCommittedOutputChanged;
        }

        private void UnregisterCommittedBoundsRenderers()
        {
            foreach (XRMeshRenderer renderer in _boneTrackingByRenderer.Keys)
            {
                renderer.GpuDrivenBoneCoverageChanged -= RendererGpuBoneCoverageChanged;
                renderer.CommittedOutputChanged -= RendererCommittedOutputChanged;
            }
            _boneTrackingByRenderer.Clear();
            Volatile.Write(ref _coveredShadowCasterLayerMask, 0);
            // Construction can fail before RenderInfo exists.
            RenderInfo?.SetCommittedWorldBoundsProvider(null);
            Volatile.Write(ref _usesCommittedWorldBounds, 0);
            _committedBoundsRenderer = null;
        }

        private void RendererGpuBoneCoverageChanged(XRMeshRenderer renderer, GpuDrivenBoneCoverageSnapshot coverage)
            => Volatile.Write(ref _committedBoundsStateDirty, 1);

        private void RendererCommittedOutputChanged(XRMeshRenderer renderer, uint producerEpoch, bool published,
            bool boundsChanged)
        {
            // An unchanged committed bound keeps the CPU tree placement. Covered shadow casters
            // still need every new output, because the GPU pose changed.
            if (boundsChanged)
                Volatile.Write(ref _committedBoundsStateDirty, 1);
            if (ReferenceEquals(renderer, CurrentLODRenderer) &&
                Volatile.Read(ref _coveredShadowCasterLayerMask) != 0)
                World?.VisualScene.NotifyCoveredShadowCasterOutputChanged(producerEpoch, published);
        }

        /// <summary>Publishes the layer of a covered GPU shadow caster.</summary>
        internal bool SetCoveredShadowCasterLayerMask(uint layerMask)
        {
            int next = unchecked((int)layerMask);
            return Interlocked.Exchange(ref _coveredShadowCasterLayerMask, next) != next;
        }

        /// <summary>Applies coverage changes before CPU tree traversal.</summary>
        internal void ReconcileCommittedWorldBounds()
        {
            XRMeshRenderer? active = CurrentLODRenderer;
            AABB activeBounds = default;
            ulong generation = 0;
            bool activeCovered = IsSkinned &&
                active?.HasCompleteGpuDrivenBoneCoverage == true &&
                active.TryGetCommittedWorldBounds(out activeBounds, out generation) &&
                activeBounds.IsValid;

            if (Volatile.Read(ref _committedBoundsStateDirty) == 0 &&
                activeCovered == (Volatile.Read(ref _usesCommittedWorldBounds) != 0) &&
                ReferenceEquals(active, _committedBoundsRenderer) &&
                (!activeCovered || generation == _committedBoundsGeneration))
                return;

            Volatile.Write(ref _committedBoundsStateDirty, 0);
            lock (_lodsLock)
            {
                for (LinkedListNode<RenderableLOD>? node = LODs.First; node is not null; node = node.Next)
                {
                    XRMeshRenderer renderer = node.Value.Renderer;
                    if (!_boneTrackingByRenderer.TryGetValue(renderer, out bool tracking))
                        continue;

                    bool covered = renderer.HasCompleteGpuDrivenBoneCoverage &&
                        renderer.TryGetCommittedWorldBounds(out AABB bounds, out _) && bounds.IsValid;
                    if (tracking == !covered)
                        continue;

                    TrackBones(renderer.Mesh, subscribe: !covered);
                    _boneTrackingByRenderer[renderer] = !covered;
                }
            }

            bool wasCovered = Volatile.Read(ref _usesCommittedWorldBounds) != 0;
            if (activeCovered)
            {
                _committedBoundsRenderer = active;
                _committedBoundsGeneration = generation;
                RenderInfo.CullingIntersectionOverride = null;
                RenderInfo.SetCommittedWorldBoundsProvider(active);
                Volatile.Write(ref _usesCommittedWorldBounds, 1);
                if (wasCovered)
                    RenderInfo.NotifyCommittedWorldBoundsChanged();
                return;
            }

            Volatile.Write(ref _usesCommittedWorldBounds, 0);
            _committedBoundsRenderer = null;
            _committedBoundsGeneration = 0;
            RenderInfo.SetCommittedWorldBoundsProvider(null);
            if (wasCovered)
            {
                RefreshSkinnedCullingIntersectionOverride();
                _ = RefreshSkinnedCullingBoundsForSceneCulling();
            }
        }
    }
}
