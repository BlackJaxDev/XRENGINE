using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Numerics;
using System.Threading;
using SimpleScene.Util.ssBVH;
using XREngine.Data.Core;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Compute;
using XREngine.Rendering.Info;
using XREngine.Rendering.Models;
using XREngine.Scene.Transforms;

namespace XREngine.Components.Scene.Mesh
{
    /// <summary>
    /// Runtime-side wrapper for one source <see cref="SubMesh"/>. It owns the active render command,
    /// LOD selection, culling state, and the handoff between component transforms and renderer state.
    /// </summary>
    public partial class RenderableMesh : XRBase, IDisposable
    {
        #region Core render state

        private readonly RenderCommandMesh3D _rc;
        private readonly RenderCommandMethod3D _renderBoundsCommand;
        private readonly HashSet<XRMesh> _ownedRuntimeMeshes = new(System.Collections.Generic.ReferenceEqualityComparer.Instance);
        // Source LODs can outlive this renderable and be shared by other components.
        // Retirement removes these handlers before it releases runtime ownership.
        private readonly List<(SubMeshLOD Source, XRPropertyChangedEventHandler Handler)> _sourceLodSubscriptions = [];
        private readonly Dictionary<SubMeshLOD, (XRMeshRenderer Renderer, bool Mesh, bool Material)> _pendingLodReferenceUpdates = [];
        private bool _lodReferenceUpdateInProgress;
        private readonly object _lifetimeGate = new();
        private readonly List<XRMeshRenderer> _pendingRendererRetirements = [];
        private IRuntimeRenderInfo3DRegistrationTarget? _pendingRegistrationRetirement;
        private volatile bool _retiring;
        private bool _fullyRetired;
        private bool _cleanupInProgress;
        private int _lifetimeMutationDepth;
        private XRMaterial? _materialOverride;

        public RenderInfo3D RenderInfo { get; }

        /// <summary>
        /// Optional material used by this renderable's primary draw command instead of the
        /// material owned by the active LOD renderer.
        /// </summary>
        public XRMaterial? MaterialOverride
        {
            get => Volatile.Read(ref _materialOverride);
            set
            {
                if (!_retiring && SetField(ref _materialOverride, value) && !_retiring)
                    _rc.MaterialOverride = value;
            }
        }

        #endregion

        #region LOD and component state

        private readonly object _lodsLock = new();
        private int _lodCount;
        private int _lodRegistrationVersion;
        private XRMeshRenderer? _currentLODRenderer;

        /// <summary>
        /// Advances whenever the LOD list membership/order or a LOD renderer's mesh
        /// changes. GPU-scene logical mesh registration retains its result until this
        /// version, the per-level mesh identities or the atlas residency change, so a
        /// transform-only update does not rebuild an unchanged registration.
        /// </summary>
        internal int LodRegistrationVersion => Volatile.Read(ref _lodRegistrationVersion);

        private void AdvanceLodRegistrationVersion()
            => Interlocked.Increment(ref _lodRegistrationVersion);

        /// <summary>
        /// Fills the per-level mesh and minimum projected radius for one submesh index
        /// without allocating, in LOD list order, up to the span length. Levels whose
        /// renderer has no mesh at that submesh index are skipped. Returns the level
        /// count written.
        /// </summary>
        internal int CollectLodMeshes(uint submeshIndex, Span<XRMesh?> meshes, Span<float> minProjectedRadiusPixels)
        {
            int count = 0;
            lock (_lodsLock)
            {
                for (LinkedListNode<RenderableLOD>? node = LODs.First; node is not null && count < meshes.Length; node = node.Next)
                {
                    RenderableLOD lod = node.Value;
                    if (!lod.Renderer.TryGetMesh((int)submeshIndex, out XRMesh? lodMesh, out _) || lodMesh is null)
                        continue;

                    meshes[count] = lodMesh;
                    minProjectedRadiusPixels[count] = lod.MinProjectedScreenRadiusPixels;
                    count++;
                }
            }

            return count;
        }

        public XRMeshRenderer? CurrentLODRenderer
            => Volatile.Read(ref _currentLODRenderer);

        /// <summary>Captures the renderer owners in the same filtered order as the shared LOD table.</summary>
        internal int CollectLodRenderers(uint submeshIndex, Span<XRMeshRenderer?> renderers)
        {
            int count = 0;
            lock (_lodsLock)
                for (LinkedListNode<RenderableLOD>? node = LODs.First; node is not null; node = node.Next)
                {
                    XRMeshRenderer renderer = node.Value.Renderer;
                    if (!renderer.TryGetMesh((int)submeshIndex, out XRMesh? mesh, out _) || mesh is null)
                        continue;
                    if (count < renderers.Length) renderers[count] = renderer;
                    count++;
                }
            return count;
        }

        /// <summary>Identifies the primary command whose pass follows its authored LOD material.</summary>
        internal bool IsPrimaryMeshCommand(IRenderCommandMesh command) => ReferenceEquals(command, _rc);

        /// <summary>Reads the component convention only while sealing the primary command's swap image.</summary>
        internal Matrix4x4 GetLodComponentWorldMatrix() => GetCurrentTransformMatrix(Component.Transform);

        public XRMesh? CurrentLODMesh
            => Volatile.Read(ref _currentLODRenderer)?.Mesh;

        private LinkedListNode<RenderableLOD>? _currentLOD = null;
        public LinkedListNode<RenderableLOD>? CurrentLOD
        {
            get => _currentLOD;
            private set
            {
                if (_retiring || !SetField(ref _currentLOD, value) || _retiring)
                    return;

                Volatile.Write(ref _currentLODRenderer, value?.Value.Renderer);
            }
        }
        public IRuntimeRenderWorld? World => Component.SceneNode.World.GetRenderWorld();
        public LinkedList<RenderableLOD> LODs { get; private set; } = new();

        private bool _renderBounds = RuntimeEngine.EditorPreferences.Debug.RenderMesh3DBounds;
        public bool RenderBounds
        {
            get => _renderBounds;
            set => SetField(ref _renderBounds, value);
        }

        private TransformBase? _rootBone;
        public TransformBase? RootBone
        {
            get => _rootBone;
            set => SetField(ref _rootBone, value);
        }

        private RenderableComponent _component;

        /// <summary>
        /// Scene component and transform that own this renderable mesh instance.
        /// </summary>
        public RenderableComponent Component
        {
            get => _component;
            private set => SetField(ref _component, value);
        }

        public bool IsSkinned
            => !_retiring && (CurrentLODRenderer?.Mesh?.HasSkinning ?? false) && RuntimeEngine.Rendering.Settings.AllowSkinning;

        public record RenderableLOD(
            XRMeshRenderer Renderer,
            float MaxVisibleDistance,
            float MinProjectedScreenRadiusPixels);

        #endregion

        #region Construction

#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider adding the 'required' modifier or declaring as nullable.
        public RenderableMesh(SubMesh mesh, RenderableComponent component)
            : this(mesh, component, null)
        {
        }

        internal RenderableMesh(SubMesh mesh, RenderableComponent component, Action<RenderableMesh>? retainPendingRetirement)
#pragma warning restore CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider adding the 'required' modifier or declaring as nullable.
        {
            lock (_lifetimeGate)
            {
                List<(SubMeshLOD Source, XRMeshRenderer Renderer, XRMesh? Mesh, XRMaterial? Material)> initialReferences = [];
                try
                {
                    Component = component;
                    TransformBase? referenceSearchRoot = GetTransformReferenceSearchRoot();
                    TransformBase? serializedRootBone = ResolveTransformReference(mesh.RootBone, referenceSearchRoot);
                    _skinnedBoundsRootTransform = ResolveTransformReference(mesh.RootTransform, referenceSearchRoot);

                    lock (_lodsLock)
                    {
                        foreach (var lod in mesh.LODs)
                        {
                            XRMesh? initialMesh = lod.Mesh;
                            XRMaterial? initialMaterial = lod.Material;
                            var renderer = lod.NewRenderer();
                            _pendingRendererRetirements.Add(renderer);
                            renderer.Mesh = CreateRuntimeMesh(initialMesh, referenceSearchRoot);
                            renderer.Material = initialMaterial;
                            renderer.SourceSubMeshAsset = mesh;
                            void UpdateReferences(object? s, IXRPropertyChangedEventArgs e)
                            {
                                UpdateLodReferences(lod, renderer, e.PropertyName);
                            }
                            XRPropertyChangedEventHandler handler = UpdateReferences;
                            _sourceLodSubscriptions.Add((lod, handler));
                            initialReferences.Add((lod, renderer, initialMesh, initialMaterial));
                            LODs.AddLast(new RenderableLOD(renderer, lod.MaxVisibleDistance, lod.MinProjectedScreenRadiusPixels));
                            TrackBones(renderer.Mesh, true);
                            RegisterCommittedBoundsRenderer(renderer);
                        }
                        Volatile.Write(ref _lodCount, LODs.Count);
                        AdvanceLodRegistrationVersion();
                    }

                    RootBone = ResolveSkinnedRootBoneTransform(
                        serializedRootBone,
                        DetermineRootBoneFromRenderers(),
                        referenceSearchRoot);

                    // Bounds colors consume the primary CPU-query decision, so run the debug
                    // callback after deferred, opaque-forward, and masked-forward mesh passes.
                    _renderBoundsCommand = new RenderCommandMethod3D((int)EDefaultRenderPass.OnTopForward, DoRenderBounds);
                    RenderInfo = RenderInfo3D.New(component, _rc = new RenderCommandMesh3D(0));
                    RenderInfo.RenderCommands.Add(_materialOutlineCommand);
                    RenderInfo.OwnerRenderableMesh = this;
                    if (RenderBounds)
                        RenderInfo.RenderCommands.Add(_renderBoundsCommand);
                    _usesAuthoredSkinnedCullingBounds = mesh.CullingBounds.HasValue;
                    RenderInfo.LocalCullingVolume = mesh.CullingBounds ?? mesh.Bounds;
                    _bindPoseBounds = RenderInfo.LocalCullingVolume ?? mesh.Bounds;
                    _primaryCollectionCallback = BeforeAdd;
                    RenderInfo.PreCollectCommandsCallback = _primaryCollectionCallback;
                    RenderInfo.RenderCullingVolumeDebugOverride = RenderCullingVolumeDebugOverride;
                    RefreshSkinnedCullingIntersectionOverride();
                    RenderInfo.PropertyChanged += RenderInfoPropertyChanged;
                    PublishRenderCommandCullingVolume();

                    lock (_lodsLock)
                    {
                        if (LODs.Count > 0)
                            CurrentLOD = LODs.First;
                    }

                    // Set initial mesh renderer for GPU scene (will be updated in BeforeAdd if needed)
                    _rc.Mesh = CurrentLODRenderer;
                    var mat = CurrentLODRenderer?.Material;
                    if (mat is not null)
                        _rc.RenderPass = mat.RenderPass;
                    RefreshVertexEffectCullingBounds();

                    // Seed startup transform state now that the render command and render info exist.
                    // This avoids the first registration frame depending on a later queued matrix update.
                    if (IsSkinned)
                    {
                        Matrix4x4 basis = GetSkinnedBasisMatrix();
                        SetSkinnedRootRenderMatrix(basis);
                        // Seed with the single skinned convention: world-space LocalCullingVolume + identity
                        // offset. Publishing root-local bounds with a non-identity offset here would be a
                        // torn-read source until the first aggregate refresh (the culling "tower" flicker).
                        PublishSkinnedWorldCullingBounds(_bindPoseBounds, basis, boundsAreWorldSpace: false);
                        QueuePendingRenderMatrixUpdate();
                    }
                    else
                    {
                        Matrix4x4 matrix = GetCurrentTransformMatrix(Component.Transform);
                        _rc.WorldMatrix = matrix;
                        RenderInfo.CullingOffsetMatrix = GetCurrentCullingBasisMatrix(Component.Transform);
                    }

                    PublishRenderCommandCullingVolume();
                    CaptureRenderDeformationSettings(IsSkinned);
                    EnsureInitialSubscriptions();
                    // Do not expose source callbacks to a half-constructed wrapper.
                    foreach ((SubMeshLOD source, XRPropertyChangedEventHandler handler) in _sourceLodSubscriptions)
                        source.PropertyChanged += handler;
                    // Construction can dispatch live bind observers before the LOD
                    // listeners attach. Reconcile those edits after subscribing so
                    // no change can fall between the snapshot and live observation.
                    foreach (var initial in initialReferences)
                    {
                        if (!ReferenceEquals(initial.Source.Mesh, initial.Mesh))
                            UpdateLodReferences(initial.Source, initial.Renderer, nameof(SubMeshLOD.Mesh));
                        if (!ReferenceEquals(initial.Source.Material, initial.Material))
                            UpdateLodReferences(initial.Source, initial.Renderer, nameof(SubMeshLOD.Material));
                    }
                    RuntimeEngine.Rendering.SettingsChanged += Rendering_SettingsChanged;
                    ReconcileCommittedWorldBounds();
                }
                catch (Exception constructionFailure)
                {
                    List<Exception>? cleanupFailures = null;
                    try { Dispose(); }
                    catch (Exception cleanupFailure) { (cleanupFailures ??= []).Add(cleanupFailure); }
                    if (!IsFullyRetired)
                        retainPendingRetirement?.Invoke(this);
                    if (cleanupFailures is not null)
                        constructionFailure.Data["RenderableMeshConstructionCleanupFailures"] = cleanupFailures;
                    throw;
                }
            }
        }

        private void EnsureInitialSubscriptions()
        {
            // Snapshot restoration can suppress SetField notifications while this mesh is
            // constructed. Replacing an existing handler keeps the normal path idempotent.
            TransformBase transform = Component.Transform;
            transform.WorldMatrixChanged -= Component_WorldMatrixPreviewChanged;
            transform.RenderMatrixChanged -= Component_WorldMatrixChanged;
            transform.WorldMatrixChanged += Component_WorldMatrixPreviewChanged;
            transform.RenderMatrixChanged += Component_WorldMatrixChanged;

            Component.PropertyChanged -= ComponentPropertyChanged;
            Component.PropertyChanging -= ComponentPropertyChanging;
            Component.PropertyChanged += ComponentPropertyChanged;
            Component.PropertyChanging += ComponentPropertyChanging;

            if (RootBone is not { } rootBone)
                return;

            rootBone.RenderMatrixChanged -= RootBone_WorldMatrixChanged;
            rootBone.RenderMatrixChanged += RootBone_WorldMatrixChanged;
        }

        #endregion

        #region Render command collection

        private void RenderInfoPropertyChanged(object? sender, IXRPropertyChangedEventArgs e)
        {
            if (_retiring)
                return;
            if (e.PropertyName is nameof(RenderInfo3D.LocalCullingVolume) or nameof(RenderInfo3D.CullingOffsetMatrix))
            {
                long t = RenderableMeshStageTelemetry.Begin();
                PublishRenderCommandCullingVolume();
                RenderableMeshStageTelemetry.End(RenderableMeshStage.PublishRenderCommandCullingVolume, t);
            }
        }

        private void PublishRenderCommandCullingVolume()
        {
            Box? worldBox = ((IOctreeItem)RenderInfo).WorldCullingVolume;
            AABB? worldBounds = worldBox?.GetAABB(transformed: true);
            _rc.WorldCullingVolumeOverride = worldBounds;
            _materialOutlineCommand.WorldCullingVolumeOverride = worldBounds;
        }

        internal RenderableLOD[] GetLodSnapshot()
        {
            lock (_lodsLock)
                return [.. LODs];
        }

        internal XRMeshRenderer? GetCurrentOrFirstLodRenderer()
        {
            lock (_lodsLock)
                return _currentLOD?.Value?.Renderer ?? LODs.First?.Value?.Renderer;
        }

        private bool BeforeAdd(RenderInfo info, RenderCommandCollection passes, IRuntimeRenderCamera? camera)
        {
            if (!BeginLifetimeMutation())
                return false;
            long tTotal = RenderableMeshStageTelemetry.Begin();
            long tHead = tTotal;
            try
            {
                if (_retiring)
                    return false;
                var rend = CurrentLODRenderer;
                bool skinned = (rend?.Mesh?.HasSkinning ?? false) && RuntimeEngine.Rendering.Settings.AllowSkinning;
                TransformBase tfm = skinned ? RootBone ?? Component.Transform : Component.Transform;
                float distance = camera?.DistanceFromRenderNearPlane(tfm.RenderTranslation) ?? 0.0f;

                if (!passes.IsShadowPass)
                    UpdateLOD(distance);
                if (_retiring)
                    return false;

                rend = CurrentLODRenderer;
                skinned = (rend?.Mesh?.HasSkinning ?? false) && RuntimeEngine.Rendering.Settings.AllowSkinning;
                XRMaterial? materialOverride = Volatile.Read(ref _materialOverride);
                XRMaterial? mat = materialOverride ?? rend?.Material;

                // One-shot: construction-time palette/draw-matrix seeds can capture stale RenderMatrix
                // values. The toggle path fixes that by re-reading current transform state; do the same
                // here before the first real draw.
                SeedInitialRenderState(rend);
                if (_retiring)
                    return false;

                // Vertex draw path pose-settle: keep re-seeding the CPU-built skin palette from current
                // bone render state until the skeleton pose stabilizes. The compute path does this in
                // SkinningPrepassDispatcher; the vertex shader reads the same palette but has no settle
                // loop of its own, so a runtime-imported avatar that publishes intermediate startup poses
                // can otherwise latch a wrong pose and render exploded until a bone is manually moved.
                // Only the vertex path runs this -- when compute skinning is enabled the dispatcher owns
                // the shared re-seed and double-driving it here would corrupt its pose tracking.
                if (!_vertexSkinSeedSettled
                    && rend?.Mesh?.HasSkinning == true
                    && RuntimeEngine.Rendering.Settings.AllowSkinning
                    && !RuntimeEngine.Rendering.Settings.CalculateSkinningInComputeShader
                    && rend.EnsureSkinningBuffers(logWarnings: false))
                {
                    _vertexSkinSeedSettled = rend.ReseedSkinPaletteUntilPoseStable();
                }

                RenderableMeshStageTelemetry.End(RenderableMeshStage.BeforeAddSkinningState, tHead);
                long tBounds = RenderableMeshStageTelemetry.Begin();
                if (skinned)
                {
                    ReconcileCommittedWorldBounds();
                    if (!UsesCommittedWorldBounds)
                    {
                        bool skinnedBoundsOk = RefreshSkinnedCullingBoundsForSceneCulling();
                        LogSkinnedCullingDiagnosticsOnce(skinnedBoundsOk);
                    }
                }
                else
                {
                    Matrix4x4 basis = GetCurrentCullingBasisMatrix(Component.Transform);
                    _rc.WorldMatrix = basis;
                    RenderInfo.LocalCullingVolume = ExpandVertexEffectLocalBounds(_bindPoseBounds, mat);
                    if (_retiring)
                        return false;
                    RenderInfo.CullingOffsetMatrix = basis;
                }
                if (_retiring)
                    return false;
                RenderableMeshStageTelemetry.End(RenderableMeshStage.BeforeAddCullingBounds, tBounds);

                long tSet = RenderableMeshStageTelemetry.Begin();
                _rc.Mesh = rend;
                _rc.MaterialOverride = materialOverride;

                if (mat is not null)
                {
                    if (ShouldRecordImportedTextureStreamingUsage(passes.IsShadowPass, RuntimeEngine.Rendering.State.IsMainPass))
                        XRTexture2D.RecordImportedTextureStreamingUsage(mat, BuildImportedTextureStreamingUsage(rend?.Mesh, camera as XRCamera, distance));
                    _rc.RenderPass = mat.RenderPass;
                }
                RenderableMeshStageTelemetry.End(RenderableMeshStage.BeforeAddCommandState, tSet);

                long tSync = RenderableMeshStageTelemetry.Begin();
                SyncMaterialPassCommands(rend, mat, passes.IsShadowPass);
                RenderableMeshStageTelemetry.End(RenderableMeshStage.BeforeAddMaterialPasses, tSync);
                long tTail = RenderableMeshStageTelemetry.Begin();
                ApplyHighlightRenderOptionsOverride(mat);
                ModelRenderDiagnostics.LogCommandCollect(this, _rc, passes, camera, distance);
                ProcessPendingGpuMeshBvhRefresh();
                RenderableMeshStageTelemetry.End(RenderableMeshStage.BeforeAddDiagnostics, tTail);

                return !_retiring;
            }
            finally
            {
                RenderableMeshStageTelemetry.End(RenderableMeshStage.BeforeAdd, tTotal);
                EndLifetimeMutation();
            }
        }

        #endregion

        #region Public operations

        public void UpdateLOD(XRCamera camera)
            => UpdateLOD(camera.DistanceFromRenderNearPlane(Component.Transform.RenderTranslation));
        public void UpdateLOD(float distanceToCamera)
        {
            if (!BeginLifetimeMutation())
                return;
            try
            {
                if (Volatile.Read(ref _lodCount) <= 1)
                    return;

                lock (_lodsLock)
                {
                    if (LODs.Count == 0)
                        return;

                    if (_currentLOD is null)
                    {
                        CurrentLOD = LODs.First;
                        return;
                    }

                    while (!_retiring && _currentLOD.Next is not null && distanceToCamera > _currentLOD.Value.MaxVisibleDistance)
                        CurrentLOD = _currentLOD.Next;

                    if (!_retiring && _currentLOD.Previous is not null && distanceToCamera < _currentLOD.Previous.Value.MaxVisibleDistance)
                        CurrentLOD = _currentLOD.Previous;
                }
            }
            finally { EndLifetimeMutation(); }
        }

        [RequiresDynamicCode("")]
        public float? Intersect(Segment localSpaceSegment, out Triangle? triangle)
        {
            triangle = null;
            return CurrentLODRenderer?.Mesh?.Intersect(localSpaceSegment, out triangle);
        }

        public Segment GetLocalSegment(Segment worldSegment, bool skinnedMesh)
            => skinnedMesh
                ? worldSegment.TransformedBy(SkinnedBvhWorldToLocalMatrix)
                : worldSegment.TransformedBy(Component.Transform.InverseWorldMatrix);

        /// <summary>
        /// Attempts to retrieve the current world-space bounds for this mesh, preferring skinned bounds when available.
        /// </summary>
        public bool TryGetWorldBounds(out AABB worldBounds)
        {
            // Default to an invalid box so callers can check IsValid before use.
            worldBounds = default;

            if (UsesCommittedWorldBounds && RenderInfo.TryGetCommittedWorldBounds(out worldBounds, out _))
                return true;

            // Prefer the live skinned bounds when skinning is active and successfully computed.
            if (IsSkinned && TryGetSkinnedBoneAggregateWorldBounds(out worldBounds))
                return true;

            if (IsSkinned && EnsureSkinnedBounds())
            {
                worldBounds = TransformBounds(_skinnedLocalBounds, _skinnedRootRenderMatrix);
                return worldBounds.IsValid;
            }

            // Fall back to the bind-pose/local culling bounds.
            AABB localBounds = RenderInfo?.LocalCullingVolume ?? _bindPoseBounds;
            if (!localBounds.IsValid)
                return false;

            Matrix4x4 basis = RenderInfo?.CullingOffsetMatrix ?? Component.Transform.RenderMatrix;
            worldBounds = TransformBounds(localBounds, basis);
            return worldBounds.IsValid;
        }

        #endregion

        #region Property wiring

        protected override bool OnPropertyChanging<T>(string? propName, T field, T @new)
        {
            bool change = base.OnPropertyChanging(propName, field, @new);
            if (change)
            {
                switch (propName)
                {
                    case nameof(RootBone):
                        if (RootBone is not null)
                            RootBone.RenderMatrixChanged -= RootBone_WorldMatrixChanged;
                        break;

                    case nameof(Component):
                        if (Component is not null)
                        {
                            Component.Transform.WorldMatrixChanged -= Component_WorldMatrixPreviewChanged;
                            Component.Transform.RenderMatrixChanged -= Component_WorldMatrixChanged;
                            Component.PropertyChanged -= ComponentPropertyChanged;
                            Component.PropertyChanging -= ComponentPropertyChanging;
                        }
                        break;
                }
            }
            return change;
        }

        protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
        {
            base.OnPropertyChanged(propName, prev, field);
            if (_retiring)
                return;
            switch (propName)
            {
                case nameof(RootBone):
                    if (RootBone is not null)
                    {
                        // Skinned culling bounds read published render matrices. A world-matrix
                        // listener would run before publication and recompute the previous
                        // bounds, so only the render-matrix listener follows later motion.
                        RootBone.RenderMatrixChanged += RootBone_WorldMatrixChanged;
                        InitializeRootBoneCullingBasis(RootBone, RootBone.WorldMatrix);
                        RootBone_WorldMatrixChanged(RootBone, RootBone.RenderMatrix);
                    }
                    break;
                case nameof(Component):
                    if (Component is not null)
                    {
                        Component.Transform.WorldMatrixChanged += Component_WorldMatrixPreviewChanged;
                        Component.Transform.RenderMatrixChanged += Component_WorldMatrixChanged;
                        Component_WorldMatrixPreviewChanged(Component.Transform, Component.Transform.WorldMatrix);
                        Component_WorldMatrixChanged(Component.Transform, Component.Transform.RenderMatrix);
                        if (_retiring)
                            break;
                        Component.PropertyChanged += ComponentPropertyChanged;
                        Component.PropertyChanging += ComponentPropertyChanging;
                    }
                    break;
                case nameof(RenderBounds):
                    if (RenderBounds)
                    {
                        if (!RenderInfo.RenderCommands.Contains(_renderBoundsCommand))
                            RenderInfo.RenderCommands.Add(_renderBoundsCommand);
                    }
                    else
                        RenderInfo.RenderCommands.Remove(_renderBoundsCommand);
                    break;
                case nameof(CurrentLOD):
                    if (CurrentLOD is not null)
                    {
                        var rend = CurrentLODRenderer;
                        bool skinned = (rend?.Mesh?.HasSkinning ?? false) && RuntimeEngine.Rendering.Settings.AllowSkinning;
                        CaptureRenderDeformationSettings(skinned);
                        _rc.WorldMatrix = skinned ? Matrix4x4.Identity : GetCurrentTransformMatrix(Component.Transform);
                        RefreshVertexEffectCullingBounds();
                        Volatile.Write(ref _committedBoundsStateDirty, 1);
                        ReconcileCommittedWorldBounds();
                    }
                    break;
            }
        }

        #endregion

        #region Disposal

        internal bool IsFullyRetired
        {
            get { lock (_lifetimeGate) return _fullyRetired; }
        }

        internal void RequestRetirement()
        {
            lock (_lifetimeGate)
            {
                if (_retiring)
                    return;
                _retiring = true;
                // Save the actual target before notification-capable cleanup can
                // change WorldInstance without completing its remove callback.
                _pendingRegistrationRetirement = RenderInfo?.WorldInstance;
                foreach ((SubMeshLOD source, XRPropertyChangedEventHandler handler) in _sourceLodSubscriptions)
                    source.PropertyChanged -= handler;
                _sourceLodSubscriptions.Clear();
                _pendingLodReferenceUpdates.Clear();
                if (Component is not null)
                {
                    Component.PropertyChanged -= ComponentPropertyChanged;
                    Component.PropertyChanging -= ComponentPropertyChanging;
                    if (Component.SceneNode is { IsTransformNull: false })
                    {
                        Component.Transform.WorldMatrixChanged -= Component_WorldMatrixPreviewChanged;
                        Component.Transform.RenderMatrixChanged -= Component_WorldMatrixChanged;
                    }
                }
                if (RootBone is not null)
                {
                    RootBone.RenderMatrixChanged -= RootBone_WorldMatrixChanged;
                }
                RuntimeEngine.Rendering.SettingsChanged -= Rendering_SettingsChanged;
                if (RenderInfo is not null)
                {
                    RenderInfo.PropertyChanged -= RenderInfoPropertyChanged;
                    // null means "collect". Keep a rejecting callback for captured
                    // scene entries until unregister completes, including retries.
                    RenderInfo.PreCollectCommandsCallback = RejectRetiredCollection;
                }
                UntrackAllBones();
            }
        }

        private bool BeginLifetimeMutation()
        {
            Monitor.Enter(_lifetimeGate);
            if (_retiring)
            {
                Monitor.Exit(_lifetimeGate);
                return false;
            }
            _lifetimeMutationDepth++;
            return true;
        }

        private static bool RejectRetiredCollection(RenderInfo info, RenderCommandCollection passes, IRuntimeRenderCamera? camera)
            => false;

        private void EndLifetimeMutation(Exception? mutationFailure = null)
        {
            try
            {
                _lifetimeMutationDepth--;
                if (_retiring && _lifetimeMutationDepth == 0)
                {
                    try { Dispose(); }
                    catch (Exception cleanupFailure) when (mutationFailure is not null)
                    { mutationFailure.Data["RenderableMeshCleanupFailures"] = cleanupFailure; }
                }
            }
            finally { Monitor.Exit(_lifetimeGate); }
        }

        private void UpdateLodReferences(SubMeshLOD lod, XRMeshRenderer renderer, string? propertyName)
        {
            if (!BeginLifetimeMutation())
                return;
            Exception? mutationFailure = null;
            try
            {
                bool meshChanged = propertyName == nameof(SubMeshLOD.Mesh);
                bool materialChanged = propertyName == nameof(SubMeshLOD.Material);
                if (!meshChanged && !materialChanged)
                    return;
                if (_pendingLodReferenceUpdates.TryGetValue(lod, out var pending))
                    _pendingLodReferenceUpdates[lod] = (renderer, pending.Mesh || meshChanged, pending.Material || materialChanged);
                else
                    _pendingLodReferenceUpdates.Add(lod, (renderer, meshChanged, materialChanged));
                if (_lodReferenceUpdateInProgress)
                    return;

                _lodReferenceUpdateInProgress = true;
                try
                {
                    // Source setters can synchronously edit another source while
                    // this renderer setter is notifying. Complete each installed
                    // transition before processing the coalesced next request.
                    for (int attempt = 0; attempt < 8 && !_retiring && _pendingLodReferenceUpdates.Count != 0; attempt++)
                    {
                        KeyValuePair<SubMeshLOD, (XRMeshRenderer Renderer, bool Mesh, bool Material)> next = default;
                        foreach (var request in _pendingLodReferenceUpdates)
                        {
                            next = request;
                            break;
                        }
                        _pendingLodReferenceUpdates.Remove(next.Key);
                        ApplyLodReferences(next.Key, next.Value.Renderer, next.Value.Mesh, next.Value.Material);
                    }
                    if (!_retiring && _pendingLodReferenceUpdates.Count != 0)
                        throw new InvalidOperationException("RenderableMesh.SourceLodPublicationUnstable: structural callbacks changed source LODs repeatedly; retry at a cold source/model boundary.");
                }
                finally { _lodReferenceUpdateInProgress = false; }
            }
            catch (Exception failure)
            {
                mutationFailure = failure;
                throw;
            }
            finally { EndLifetimeMutation(mutationFailure); }
        }

        private void ApplyLodReferences(SubMeshLOD lod, XRMeshRenderer renderer, bool meshChanged, bool materialChanged)
        {
            if (meshChanged)
            {
                XRMesh? previousMesh = renderer.Mesh;
                XRMesh? replacement = CreateRuntimeMesh(lod.Mesh, GetTransformReferenceSearchRoot());
                if (_retiring)
                    return;
                renderer.Mesh = replacement;
                if (_retiring)
                    return;
                if (!ReferenceEquals(renderer.Mesh, replacement))
                {
                    ReleaseOwnedRuntimeMesh(replacement);
                    return;
                }
                // A renderer with complete GPU bone coverage has no CPU bone
                // subscriptions. Remove only the subscriptions that exist.
                if (_boneTrackingByRenderer.TryGetValue(renderer, out bool wasTracked) && wasTracked)
                    TrackBones(previousMesh, false);
                AdvanceLodRegistrationVersion();
                TrackBones(replacement, true);
                _boneTrackingByRenderer[renderer] = true;
                Volatile.Write(ref _committedBoundsStateDirty, 1);
                MarkSkinnedDataDirty();
                MarkSkinnedBoneCullingVolumesDirty();
                RefreshSkinnedCullingIntersectionOverride();
                if (!_retiring)
                    _rc?.MarkDirty();
                // A veto of old-clone destruction must not leave the already
                // installed replacement without registration or bone wiring.
                ReleaseOwnedRuntimeMesh(previousMesh);
            }
            if (!_retiring && materialChanged)
            {
                renderer.Material = lod.Material;
                if (!_retiring)
                    _rc?.MarkDirty();
            }
        }

        public void Dispose()
        {
            lock (_lifetimeGate)
            {
                RequestRetirement();
                if (_fullyRetired || _cleanupInProgress || _lifetimeMutationDepth != 0)
                    return;
                _cleanupInProgress = true;
                List<Exception>? failures = null;
                void Attempt(Action cleanup)
                {
                    try { cleanup(); }
                    catch (Exception failure) { (failures ??= []).Add(failure); }
                }
                try
                {
                    if (RenderInfo is not null)
                    {
                        Attempt(() => RenderInfo.WorldInstance = null);
                        if (_pendingRegistrationRetirement is { } target)
                            Attempt(() =>
                            {
                                target.RemoveRenderable3D(RenderInfo);
                                _pendingRegistrationRetirement = null;
                            });
                        if (_pendingRegistrationRetirement is null)
                        {
                            // Removal completed normally; clearing terminal metadata
                            // must not dispatch another registration transaction.
                            using (XRBase.SuppressPropertyNotifications())
                                RenderInfo.WorldInstance = null;
                            Attempt(RenderInfo.RenderCommands.Clear);
                        }
                        // Remove the renderer coverage handlers and the committed
                        // bounds provider. A repeated call after a failed removal is safe.
                        Attempt(UnregisterCommittedBoundsRenderers);
                    }
                    Attempt(() =>
                    {
                        if (Component?.SceneNode is not null)
                            SkinnedMeshBoundsCalculator.Instance.UnregisterSkinnedMesh(this, World?.VisualScene?.GPUCommands);
                    });
                    lock (_lodsLock)
                    {
                        Volatile.Write(ref _lodCount, 0);
                        SetField(ref _currentLOD, null, publishNotifications: false, nameof(CurrentLOD));
                        Volatile.Write(ref _currentLODRenderer, null);
                        LODs.Clear();
                        AdvanceLodRegistrationVersion();
                    }
                    for (int index = _pendingRendererRetirements.Count - 1; index >= 0; index--)
                    {
                        XRMeshRenderer renderer = _pendingRendererRetirements[index];
                        Attempt(() =>
                        {
                            // The established deferred destruction queue accepts
                            // ownership; retirement does not wait for a GPU fence.
                            renderer.Destroy();
                            _pendingRendererRetirements.Remove(renderer);
                        });
                    }
                    foreach (XRMesh mesh in _ownedRuntimeMeshes.ToArray())
                        Attempt(() => ReleaseOwnedRuntimeMesh(mesh));
                    Attempt(DisposeGpuMeshBvh);
                    Attempt(DisposeGpuSkinnedBoundsDebugRenderer);
                    SetField(ref _materialOverride, null, publishNotifications: false, nameof(MaterialOverride));
                    if (_rc is not null)
                    {
                        Attempt(() => _rc.Mesh = null);
                        Attempt(() => _rc.MaterialOverride = null);
                    }
                    lock (_highlightStateLock)
                    {
                        if (_rc is not null)
                        {
                            Attempt(() => _rc.RenderOptionsOverride = null);
                            Attempt(() => _rc.ForceCpuRendering = false);
                        }
                        Attempt(() => _materialOutlineCommand.Enabled = false);
                        Attempt(() => _materialOutlineCommand.Mesh = null);
                        Attempt(() => _materialOutlineCommand.MaterialOverride = null);
                        Attempt(() => _materialOutlineCommand.RenderOptionsOverride = null);
                        _highlightRenderOptionsOverride = null;
                        _highlightSourceMaterial = null;
                        _highlightStencilBits = 0;
                    }
                    _fullyRetired = failures is null && _pendingRegistrationRetirement is null &&
                        _pendingRendererRetirements.Count == 0 && _ownedRuntimeMeshes.Count == 0 &&
                        _gpuMeshBvh is null && _gpuBoundsDebugRenderer is null;
                    if (_fullyRetired)
                        GC.SuppressFinalize(this);
                }
                finally { _cleanupInProgress = false; }
                if (failures is not null)
                    throw new AggregateException("RenderableMesh.RetirementIncomplete: remaining ownership is retained for retry.", failures);
            }
        }

        #endregion
    }
}
