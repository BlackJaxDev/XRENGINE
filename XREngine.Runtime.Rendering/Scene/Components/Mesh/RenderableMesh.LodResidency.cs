using System;
using System.Collections.Generic;
using System.Threading;
using XREngine.Rendering.Residency;
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
        private int _lateLodReadinessChanged;

        /// <summary>
        /// The number of render-thread attempts after which a late LOD that is still not ready gives one warning. The LOD
        /// stays unselectable; the lower resident LOD keeps drawing.
        /// </summary>
        private const int LateLodReadinessWarningAttempts = 1800;

        /// <summary>
        /// Prepares the renderer of a late LOD on the active backend and tells if it can draw now. It runs on the render
        /// thread, once per frame until it gives true. The default uses <see cref="XRMeshRenderer.TryPrewarmForLodSelection"/>.
        /// Tests replace it.
        /// </summary>
        internal static Func<XRMeshRenderer, bool> LateLodReadinessProbe { get; set; } = DefaultLateLodReadinessProbe;

        /// <summary>
        /// Schedules the readiness step of a late LOD, or null for the render-thread coroutine of the host. The step
        /// follows the coroutine contract: it returns true when it is complete and false to run again in the next frame.
        /// Tests replace it.
        /// </summary>
        internal static Action<Func<bool>>? LateLodReadinessScheduler { get; set; }

        internal static bool DefaultLateLodReadinessProbe(XRMeshRenderer renderer)
            => renderer.TryPrewarmForLodSelection(out _);

        /// <summary>
        /// The smallest LOD index that <see cref="UpdateLOD(float)"/> can request, or null for no limit. A value of 2 keeps the
        /// selection at LOD 2 or coarser. The selection still takes the nearest resident LOD. Use it for diagnostics and tests.
        /// </summary>
        public int? ForcedMinimumLod
        {
            get => _forcedMinimumLod;
            set => SetField(ref _forcedMinimumLod, value is { } index ? Math.Max(0, index) : null);
        }

        /// <summary>The sub-mesh that this renderable draws, or null for a renderable made without one.</summary>
        internal SubMesh? SourceSubMesh => _sourceSubMesh;

        /// <summary>The number of LODs in the LOD table of the source sub-mesh, resident or not.</summary>
        public int LodTableCount => _lodSlots.Length;

        /// <summary>
        /// The renderer of the lowest LOD while it is resident, or null. This is the renderer of the T0 draw. A renderable
        /// with no LOD table gives the renderer of its last LOD.
        /// </summary>
        internal XRMeshRenderer? LowestLodRenderer
        {
            get
            {
                lock (_lodsLock)
                {
                    LodSlot[] slots = _lodSlots;
                    return slots.Length > 0
                        ? slots[^1].Node?.Value.Renderer
                        : LODs.Last?.Value.Renderer;
                }
            }
        }

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
                if (coarser < count && slots[coarser].Node is { } coarserNode && slots[coarser].IsSelectable)
                    return coarserNode;
                int finer = target - offset;
                if (offset > 0 && finer >= 0 && slots[finer].Node is { } finerNode && slots[finer].IsSelectable)
                    return finerNode;
            }
            return _currentLOD;
        }

        /// <summary>
        /// Fills the GPU registration levels of one submesh index without allocating. With a LOD table, the levels follow the
        /// table order, and a LOD that is not resident, or whose renderer has no mesh at the submesh index, gives a null level
        /// with its configured screen radius. The GPU scene registers a null level with mesh ID 0, so the GPU LOD selection
        /// clamps to the nearest resident level. When the table is longer than the spans, the last level is the last LOD of the
        /// table, so the lowest LOD is always a registration level. Without a LOD table, the resident LODs fill the levels in
        /// list order. Returns the level count: the index of the last non-null level plus one, or zero.
        /// </summary>
        internal int CollectLodMeshes(uint submeshIndex, Span<XRMesh?> meshes, Span<float> minProjectedRadiusPixels)
        {
            lock (_lodsLock)
            {
                LodSlot[] slots = _lodSlots;
                if (slots.Length == 0)
                {
                    int count = 0;
                    for (LinkedListNode<RenderableLOD>? node = LODs.First; node is not null && count < meshes.Length; node = node.Next)
                    {
                        RenderableLOD lod = node.Value;
                        if (!lod.Renderer.TryGetMesh((int)submeshIndex, out XRMesh? lodMesh, out _) || lodMesh is null)
                            continue;

                        meshes[count] = lodMesh;
                        minProjectedRadiusPixels[count] = lod.MinProjectedScreenRadiusPixels;
                        count++;
                    }
                    return count;
                }

                int levelCount = Math.Min(slots.Length, meshes.Length);
                int lastResident = -1;
                for (int level = 0; level < levelCount; level++)
                {
                    LodSlot slot = slots[GetRegistrationSlotIndex(level, slots.Length, meshes.Length)];
                    minProjectedRadiusPixels[level] = slot.MinProjectedScreenRadiusPixels;
                    meshes[level] = null;
                    if (!slot.IsSelectable
                        || slot.Node?.Value.Renderer is not { } renderer
                        || !renderer.TryGetMesh((int)submeshIndex, out XRMesh? lodMesh, out _)
                        || lodMesh is null)
                        continue;

                    meshes[level] = lodMesh;
                    lastResident = level;
                }
                return lastResident + 1;
            }
        }

        /// <summary>
        /// Captures the LOD renderers of one submesh index at the same levels as <see cref="CollectLodMeshes"/>. A level with no
        /// resident renderer for the submesh gets null. Returns the authored LOD count: the table length when the table is
        /// longer than the span, otherwise the level count of <see cref="CollectLodMeshes"/>.
        /// </summary>
        internal int CollectLodRenderers(uint submeshIndex, Span<XRMeshRenderer?> renderers)
        {
            lock (_lodsLock)
            {
                LodSlot[] slots = _lodSlots;
                if (slots.Length == 0)
                {
                    int count = 0;
                    for (LinkedListNode<RenderableLOD>? node = LODs.First; node is not null; node = node.Next)
                    {
                        XRMeshRenderer renderer = node.Value.Renderer;
                        if (!renderer.TryGetMesh((int)submeshIndex, out XRMesh? mesh, out _) || mesh is null)
                            continue;
                        if (count < renderers.Length)
                            renderers[count] = renderer;
                        count++;
                    }
                    return count;
                }

                int levelCount = Math.Min(slots.Length, renderers.Length);
                int lastResident = -1;
                for (int level = 0; level < levelCount; level++)
                {
                    LodSlot slot = slots[GetRegistrationSlotIndex(level, slots.Length, renderers.Length)];
                    renderers[level] = null;
                    if (!slot.IsSelectable
                        || slot.Node?.Value.Renderer is not { } renderer
                        || !renderer.TryGetMesh((int)submeshIndex, out XRMesh? mesh, out _)
                        || mesh is null)
                        continue;

                    renderers[level] = renderer;
                    lastResident = level;
                }
                return slots.Length > renderers.Length ? slots.Length : lastResident + 1;
            }
        }

        /// <summary>
        /// Maps a GPU registration level to a LOD table index. The levels follow the table, except that the last level of a
        /// table longer than the level capacity is the last LOD of the table: the lowest LOD, which is always resident.
        /// </summary>
        private static int GetRegistrationSlotIndex(int level, int slotCount, int levelCapacity)
            => slotCount > levelCapacity && level == levelCapacity - 1 ? slotCount - 1 : level;

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
                    // The LOD is not selectable until its renderer is ready on the active backend.
                    Volatile.Write(ref slot.SelectionReady, 0);
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
                RequestLateLodReadiness(slot, renderer);
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

        /// <summary>
        /// Starts the readiness check of a late LOD renderer on the render thread. When the renderer is ready on the active
        /// backend, the LOD becomes selectable and its GPU registration level is refreshed one time. A cold program or late
        /// LOD so never makes a frame wait: the lower resident LOD draws until then. One closure is made for each
        /// publication, not for each frame. The update thread never calls backend preparation: with a render host
        /// installed, the check always goes to the render thread.
        /// </summary>
        private void RequestLateLodReadiness(LodSlot slot, XRMeshRenderer renderer)
        {
            int attempts = 0;
            Func<bool> step = () => StepLateLodReadiness(slot, renderer, ref attempts);
            if (LateLodReadinessScheduler is { } scheduler)
            {
                scheduler(step);
                return;
            }

            // With no render host, no backend exists and no draw can wait for this renderer, so one step runs here.
            if (!RuntimeRenderingHostServices.HasConcreteHost)
            {
                _ = step();
                return;
            }

            RuntimeRenderingHostServices.Scheduling.EnqueueRenderThreadCoroutine(step, "RenderableMesh.LateLodReadiness");
        }

        /// <summary>
        /// Runs one readiness check of a late LOD renderer. It follows the coroutine contract: it returns true when the check
        /// is complete (the LOD is ready, left the table, got another renderer, or the mesh retires) and false to check
        /// again in the next frame.
        /// </summary>
        private bool StepLateLodReadiness(LodSlot slot, XRMeshRenderer renderer, ref int attempts)
        {
            // Stop when the LOD left the table or another renderer took its place.
            if (_retiring || !ReferenceEquals(slot.Node?.Value.Renderer, renderer))
                return true;
            if (!LateLodReadinessProbe(renderer))
            {
                if (++attempts == LateLodReadinessWarningAttempts)
                    Debug.RenderingWarning(
                        "[RenderableMesh] Late LOD {0} of '{1}' is still not ready on the render backend after {2} attempts. The lower resident LOD keeps drawing.",
                        slot.Index,
                        renderer.Mesh?.Name ?? "<unnamed mesh>",
                        attempts);
                return false;
            }

            // The update thread can evict the LOD and publish a new renderer while the probe runs.
            if (_retiring || !ReferenceEquals(slot.Node?.Value.Renderer, renderer))
                return true;
            Volatile.Write(ref slot.SelectionReady, 1);
            AdvanceLodRegistrationVersion();
            Volatile.Write(ref _lateLodReadinessChanged, 1);
            return true;
        }

        /// <summary>
        /// Marks the render command dirty after a late LOD became ready, so the next registration sees the new level. Call it
        /// on the update thread.
        /// </summary>
        private void ApplyLateLodReadinessChange()
        {
            if (Interlocked.Exchange(ref _lateLodReadinessChanged, 0) != 0 && !_retiring)
                _rc?.MarkDirty();
        }

        /// <summary>True while a LOD renderer that was published after construction waits for its first selection.</summary>
        internal bool IsAwaitingLateLodSeed(XRMeshRenderer renderer)
            => _lateLodRenderersAwaitingSeed.Contains(renderer);

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

            /// <summary>
            /// 1 when the LOD can be selected. A LOD resident at construction is ready at once. A late LOD is 0 until its
            /// renderer is ready on the active backend.
            /// </summary>
            public int SelectionReady = 1;

            /// <summary>True when the LOD is resident and its renderer can draw on the active backend.</summary>
            public bool IsSelectable => Node is not null && Volatile.Read(ref SelectionReady) != 0;

            public RenderableLOD CreateRenderableLod(XRMeshRenderer renderer)
                => new(renderer, MaxVisibleDistance, MinProjectedScreenRadiusPixels) { Index = Index, Residency = Tier };
        }
    }
}
