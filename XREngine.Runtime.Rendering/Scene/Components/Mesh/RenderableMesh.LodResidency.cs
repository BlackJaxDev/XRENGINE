using System;
using System.Collections.Generic;
using System.Threading;
using XREngine.Cutscenes;
using XREngine.Rendering;
using XREngine.Rendering.Models;

namespace XREngine.Components.Scene.Mesh
{
    /// <summary>
    /// LOD residency: the LOD table of the source sub-mesh, the publication and removal of LODs whose cooked payload loads
    /// after construction, and the selection of the nearest resident LOD.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="LODs"/> holds only resident LODs, in LOD table order. A LOD of a cooked sub-mesh whose separate payload is
    /// not loaded (<see cref="SubMeshLOD.IsPayloadDeferred"/> with no mesh) keeps its slot in the table, with its distance
    /// and screen-radius values, but has no renderer. When its source mesh comes, the LOD is published: it gets a renderer,
    /// the current blendshape weights, bone tracking, and a place in <see cref="LODs"/>, and the LOD registration version
    /// advances. The next <see cref="UpdateLOD(float)"/> can select it. When the source mesh of such a LOD goes back to null,
    /// the LOD leaves the selectable set first, and then its renderer goes to the deferred destruction queue.
    /// </para>
    /// <para>
    /// An editor sub-mesh and an older cooked sub-mesh have no deferred LODs, so every LOD is resident and the selection is
    /// the same as before. A LOD with no deferred payload is never removed, so the coarsest LOD always stays.
    /// </para>
    /// </remarks>
    public partial class RenderableMesh
    {
        private SubMesh? _sourceSubMesh;
        private LodSlot[] _lodSlots = [];
        private int _requestedLodIndex;
        private int? _forcedMinimumLod;
        private readonly HashSet<XRMeshRenderer> _lateLodRenderersAwaitingSeed = new(ReferenceEqualityComparer.Instance);

        /// <summary>
        /// The smallest LOD index that <see cref="UpdateLOD(float)"/> can request, or null for no limit. A value of 2 keeps the
        /// selection at LOD 2 or coarser. The selection still takes the nearest resident LOD. Use it for diagnostics and tests.
        /// </summary>
        public int? ForcedMinimumLod
        {
            get => _forcedMinimumLod;
            set => SetField(ref _forcedMinimumLod, value is { } index ? Math.Max(0, index) : null);
        }

        /// <summary>The number of LODs in the LOD table of the source sub-mesh, resident or not.</summary>
        public int LodTableCount => _lodSlots.Length;

        /// <summary>
        /// Gets the residency of a LOD of the table: its tier while it is resident, or <see cref="EResidencyLevel.None"/>.
        /// </summary>
        /// <param name="lodIndex">The index of the LOD in the table.</param>
        public EResidencyLevel GetLodResidency(int lodIndex)
        {
            LodSlot[] slots = _lodSlots;
            if ((uint)lodIndex >= (uint)slots.Length)
                return EResidencyLevel.None;

            lock (_lodsLock)
                return slots[lodIndex].Node is null ? EResidencyLevel.None : slots[lodIndex].Tier;
        }

        /// <summary>True if a LOD of the table is resident and selectable.</summary>
        /// <param name="lodIndex">The index of the LOD in the table.</param>
        public bool IsLodResident(int lodIndex)
            => GetLodResidency(lodIndex) != EResidencyLevel.None;

        /// <summary>The index of the current LOD in the table, or -1.</summary>
        public int CurrentLodIndex
        {
            get
            {
                lock (_lodsLock)
                    return _currentLOD?.Value.Index ?? -1;
            }
        }

        /// <summary>
        /// Selects the LOD for a distance. The requested index walks the whole LOD table with the same rule as before the
        /// payload split, and <see cref="ForcedMinimumLod"/> limits it. The result is the resident LOD nearest to the
        /// requested index; at equal distance the coarser LOD wins. Call it with <see cref="_lodsLock"/> held. It does not
        /// allocate.
        /// </summary>
        private LinkedListNode<RenderableLOD>? SelectResidentLod(float distanceToCamera)
        {
            LodSlot[] slots = _lodSlots;
            int count = slots.Length;
            if (count == 0)
                return _currentLOD;

            int requested = Math.Clamp(_requestedLodIndex, 0, count - 1);
            while (requested < count - 1 && distanceToCamera > slots[requested].MaxVisibleDistance)
                requested++;
            if (requested > 0 && distanceToCamera < slots[requested - 1].MaxVisibleDistance)
                requested--;
            _requestedLodIndex = requested;

            int target = requested;
            if (_forcedMinimumLod is { } minimum)
                target = Math.Max(target, Math.Min(minimum, count - 1));

            for (int offset = 0; offset < count; offset++)
            {
                int coarser = target + offset;
                if (coarser < count && slots[coarser].Node is { } coarserNode)
                    return coarserNode;
                int finer = target - offset;
                if (offset > 0 && finer >= 0 && slots[finer].Node is { } finerNode)
                    return finerNode;
            }
            return _currentLOD;
        }

        /// <summary>Routes a change of a source LOD: a residency change publishes or removes the LOD; other changes update the renderer.</summary>
        private void OnSourceLodChanged(LodSlot slot, string? propertyName)
        {
            XRMeshRenderer? renderer = slot.Node?.Value.Renderer;
            if (propertyName == nameof(SubMeshLOD.Mesh))
            {
                if (renderer is null)
                {
                    if (slot.Source.Mesh is not null)
                        PublishLod(slot);
                    return;
                }
                if (slot.Source.IsPayloadDeferred && slot.Source.Mesh is null)
                {
                    UnpublishLod(slot);
                    return;
                }
            }

            // A LOD that is not resident takes its current material when it is published.
            if (renderer is not null)
                UpdateLodReferences(slot.Source, renderer, propertyName);
        }

        /// <summary>Makes a renderer for a LOD whose source mesh came after construction, and adds it to the selectable set.</summary>
        private void PublishLod(LodSlot slot)
        {
            if (!BeginLifetimeMutation())
                return;
            Exception? mutationFailure = null;
            try
            {
                XRMesh? sourceMesh = slot.Source.Mesh;
                if (sourceMesh is null || slot.Node is not null)
                    return;

                XRMeshRenderer renderer = slot.Source.NewRenderer();
                _pendingRendererRetirements.Add(renderer);
                renderer.Mesh = CreateRuntimeMesh(sourceMesh, GetTransformReferenceSearchRoot());
                renderer.Material = slot.Source.Material;
                renderer.SourceSubMeshAsset = _sourceSubMesh;
                if (_retiring)
                    return;

                CopyBlendshapeWeights(CurrentLODRenderer, renderer);
                TrackBones(renderer.Mesh, true);
                RegisterCommittedBoundsRenderer(renderer);
                lock (_lodsLock)
                {
                    LinkedListNode<RenderableLOD>? next = FindNextResidentNode(slot.Index);
                    RenderableLOD value = slot.CreateRenderableLod(renderer);
                    slot.Node = next is null ? LODs.AddLast(value) : LODs.AddBefore(next, value);
                    Volatile.Write(ref _lodCount, LODs.Count);
                    if (_currentLOD is null)
                        CurrentLOD = slot.Node;
                    AdvanceLodRegistrationVersion();
                }

                _lateLodRenderersAwaitingSeed.Add(renderer);
                Volatile.Write(ref _committedBoundsStateDirty, 1);
                MarkSkinnedDataDirty();
                if (!_retiring)
                    _rc?.MarkDirty();
            }
            catch (Exception failure)
            {
                mutationFailure = failure;
                throw;
            }
            finally { EndLifetimeMutation(mutationFailure); }
        }

        /// <summary>
        /// Removes a deferred LOD whose source mesh went back to null. The LOD leaves the selectable set before its renderer
        /// goes to the deferred destruction queue. The last resident LOD never leaves.
        /// </summary>
        private void UnpublishLod(LodSlot slot)
        {
            if (!BeginLifetimeMutation())
                return;
            Exception? mutationFailure = null;
            try
            {
                XRMeshRenderer renderer;
                bool wasCurrent;
                lock (_lodsLock)
                {
                    if (slot.Node is not { } node || LODs.Count <= 1)
                        return;

                    renderer = node.Value.Renderer;
                    wasCurrent = ReferenceEquals(_currentLOD, node);
                    if (wasCurrent)
                        CurrentLOD = node.Next ?? node.Previous;
                    LODs.Remove(node);
                    slot.Node = null;
                    Volatile.Write(ref _lodCount, LODs.Count);
                    AdvanceLodRegistrationVersion();
                }

                _lateLodRenderersAwaitingSeed.Remove(renderer);
                if (_boneTrackingByRenderer.Remove(renderer, out bool tracked))
                {
                    if (tracked)
                        TrackBones(renderer.Mesh, false);
                    renderer.GpuDrivenBoneCoverageChanged -= RendererGpuBoneCoverageChanged;
                    renderer.CommittedOutputChanged -= RendererCommittedOutputChanged;
                }
                if (wasCurrent && !_retiring)
                    _rc.Mesh = CurrentLODRenderer;

                XRMesh? runtimeMesh = renderer.Mesh;
                // The established deferred destruction queue takes ownership, so frames in flight keep their resources.
                renderer.Destroy();
                _pendingRendererRetirements.Remove(renderer);
                ReleaseOwnedRuntimeMesh(runtimeMesh);
                Volatile.Write(ref _committedBoundsStateDirty, 1);
                MarkSkinnedDataDirty();
                if (!_retiring)
                    _rc?.MarkDirty();
            }
            catch (Exception failure)
            {
                mutationFailure = failure;
                throw;
            }
            finally { EndLifetimeMutation(mutationFailure); }
        }

        /// <summary>Gets the node of the first resident LOD after a table index, or null. Call it with <see cref="_lodsLock"/> held.</summary>
        private LinkedListNode<RenderableLOD>? FindNextResidentNode(int lodIndex)
        {
            LodSlot[] slots = _lodSlots;
            for (int i = lodIndex + 1; i < slots.Length; i++)
                if (slots[i].Node is { } node)
                    return node;
            return null;
        }

        /// <summary>Resets the per-mesh palette seed state at the first selection of a LOD that was published after construction.</summary>
        private void ResetSeedStateForLateLod(XRMeshRenderer? renderer)
        {
            if (renderer is null || _lateLodRenderersAwaitingSeed.Count == 0 || !_lateLodRenderersAwaitingSeed.Remove(renderer))
                return;

            _initialRenderStateSeeded = false;
            _vertexSkinSeedSettled = false;
        }

        /// <summary>
        /// Copies the current blendshape weights to a new LOD renderer by name. Weights reach only the renderers that exist
        /// when they are set, so without this a new LOD shows the neutral pose.
        /// </summary>
        private static void CopyBlendshapeWeights(XRMeshRenderer? source, XRMeshRenderer destination)
        {
            if (source?.Mesh is not { HasBlendshapes: true } || destination.Mesh is not { HasBlendshapes: true } mesh)
                return;
            if (!destination.EnsureBlendshapeBuffers(logWarnings: false))
                return;

            string[] names = mesh.BlendshapeNames;
            for (int i = 0; i < names.Length; i++)
            {
                float weight = source.GetBlendshapeWeightNormalized(names[i]);
                if (weight != 0.0f)
                    destination.SetBlendshapeWeightNormalized((uint)i, weight);
            }
        }

        /// <summary>One LOD of the source table. <see cref="Node"/> is null while the LOD is not resident.</summary>
        private sealed class LodSlot(SubMeshLOD source, int index, EResidencyLevel tier)
        {
            public SubMeshLOD Source { get; } = source;
            public int Index { get; } = index;
            public EResidencyLevel Tier { get; } = tier;
            public float MaxVisibleDistance { get; } = source.MaxVisibleDistance;
            public float MinProjectedScreenRadiusPixels { get; } = source.MinProjectedScreenRadiusPixels;
            public LinkedListNode<RenderableLOD>? Node { get; set; }

            public RenderableLOD CreateRenderableLod(XRMeshRenderer renderer)
                => new(renderer, MaxVisibleDistance, MinProjectedScreenRadiusPixels) { Index = Index, Residency = Tier };
        }
    }
}
