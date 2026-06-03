using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
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

        public RenderInfo3D RenderInfo { get; }

        #endregion

        #region LOD and component state

        private readonly object _lodsLock = new();

        public XRMeshRenderer? CurrentLODRenderer
        {
            get
            {
                lock (_lodsLock)
                    return _currentLOD?.Value?.Renderer;
            }
        }

        public XRMesh? CurrentLODMesh
        {
            get
            {
                lock (_lodsLock)
                    return _currentLOD?.Value?.Renderer?.Mesh;
            }
        }

        private LinkedListNode<RenderableLOD>? _currentLOD = null;
        public LinkedListNode<RenderableLOD>? CurrentLOD
        {
            get => _currentLOD;
            private set => SetField(ref _currentLOD, value);
        }
        public IRuntimeRenderWorld? World => Component.SceneNode.World as IRuntimeRenderWorld;
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
            => (CurrentLODRenderer?.Mesh?.HasSkinning ?? false) && RuntimeEngine.Rendering.Settings.AllowSkinning;

        public record RenderableLOD(
            XRMeshRenderer Renderer,
            float MaxVisibleDistance,
            float MinProjectedScreenRadiusPixels);

        #endregion

        #region Construction

#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider adding the 'required' modifier or declaring as nullable.
        public RenderableMesh(SubMesh mesh, RenderableComponent component)
#pragma warning restore CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider adding the 'required' modifier or declaring as nullable.
        {
            Component = component;
            TransformBase? referenceSearchRoot = GetTransformReferenceSearchRoot();
            TransformBase? serializedRootBone = ResolveTransformReference(mesh.RootBone, referenceSearchRoot);
            _skinnedBoundsRootTransform = ResolveTransformReference(mesh.RootTransform, referenceSearchRoot);

            lock (_lodsLock)
            {
                foreach (var lod in mesh.LODs)
                {
                    var renderer = lod.NewRenderer();
                    renderer.Mesh = CreateRuntimeMesh(lod.Mesh, referenceSearchRoot);
                    renderer.SourceSubMeshAsset = mesh;
                    renderer.SettingUniforms += SettingUniforms;
                    void UpdateReferences(object? s, IXRPropertyChangedEventArgs e)
                    {
                        if (e.PropertyName == nameof(SubMeshLOD.Mesh))
                        {
                            XRMesh? previousMesh = renderer.Mesh;
                            TrackBones(previousMesh, false);
                            ReleaseOwnedRuntimeMesh(previousMesh);
                            renderer.Mesh = CreateRuntimeMesh(lod.Mesh, GetTransformReferenceSearchRoot());
                            TrackBones(renderer.Mesh, true);
                            MarkSkinnedDataDirty();
                            MarkSkinnedBoneCullingVolumesDirty();
                            RefreshSkinnedCullingIntersectionOverride();
                        }
                        else if (e.PropertyName == nameof(SubMeshLOD.Material))
                            renderer.Material = lod.Material;
                    }
                    lod.PropertyChanged += UpdateReferences;
                    LODs.AddLast(new RenderableLOD(renderer, lod.MaxVisibleDistance, lod.MinProjectedScreenRadiusPixels));
                    TrackBones(renderer.Mesh, true);
                }
            }

            RootBone = ResolveSkinnedRootBoneTransform(
                serializedRootBone,
                DetermineRootBoneFromRenderers(),
                referenceSearchRoot);

            _renderBoundsCommand = new RenderCommandMethod3D((int)EDefaultRenderPass.OpaqueForward, DoRenderBounds);
            RenderInfo = RenderInfo3D.New(component, _rc = new RenderCommandMesh3D(0));
            RenderInfo.OwnerRenderableMesh = this;
            if (RenderBounds)
                RenderInfo.RenderCommands.Add(_renderBoundsCommand);
            _usesAuthoredSkinnedCullingBounds = mesh.CullingBounds.HasValue;
            RenderInfo.LocalCullingVolume = mesh.CullingBounds ?? mesh.Bounds;
            _bindPoseBounds = RenderInfo.LocalCullingVolume ?? mesh.Bounds;
            RenderInfo.PreCollectCommandsCallback = BeforeAdd;
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
                _rc.WorldMatrix = Component.Transform.RenderMatrix;
                RenderInfo.CullingOffsetMatrix = Component.Transform.WorldMatrix;
            }

            CaptureRenderDeformationSettings(IsSkinned);
            RuntimeEngine.Rendering.SettingsChanged += Rendering_SettingsChanged;
        }

        #endregion

        #region Render command collection

        private void RenderInfoPropertyChanged(object? sender, IXRPropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(RenderInfo3D.LocalCullingVolume) or nameof(RenderInfo3D.CullingOffsetMatrix))
                PublishRenderCommandCullingVolume();
        }

        private void PublishRenderCommandCullingVolume()
        {
            Box? worldBox = ((IOctreeItem)RenderInfo).WorldCullingVolume;
            _rc.WorldCullingVolumeOverride = worldBox?.GetAABB(transformed: true);
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

        private void SettingUniforms(XRRenderProgram vertexProgram, XRRenderProgram materialProgram)
        {
            // Reserved for mesh-level uniforms; renderer and material paths currently bind their own state.
        }

        private bool BeforeAdd(RenderInfo info, RenderCommandCollection passes, IRuntimeRenderCamera? camera)
        {
            var rend = CurrentLODRenderer;
            bool skinned = (rend?.Mesh?.HasSkinning ?? false) && RuntimeEngine.Rendering.Settings.AllowSkinning;
            TransformBase tfm = skinned ? RootBone ?? Component.Transform : Component.Transform;
            float distance = camera?.DistanceFromRenderNearPlane(tfm.RenderTranslation) ?? 0.0f;

            if (!passes.IsShadowPass)
                UpdateLOD(distance);

            rend = CurrentLODRenderer;
            skinned = (rend?.Mesh?.HasSkinning ?? false) && RuntimeEngine.Rendering.Settings.AllowSkinning;

            // One-shot: construction-time palette/draw-matrix seeds can capture stale RenderMatrix
            // values. The toggle path fixes that by re-reading current transform state; do the same
            // here before the first real draw.
            if (!_initialRenderStateSeeded)
            {
                _initialRenderStateSeeded = true;
                if (rend?.Mesh?.HasSkinning == true && rend.EnsureSkinningBuffers(logWarnings: false))
                    rend.RefreshBoneMatricesFromRenderState();
                QueueCurrentRenderMatrixUpdate();
            }

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

            if (skinned)
            {
                bool skinnedBoundsOk = RefreshSkinnedCullingBoundsForSceneCulling();
                LogSkinnedCullingDiagnosticsOnce(skinnedBoundsOk);
            }
            else
            {
                RenderInfo.LocalCullingVolume = _bindPoseBounds;
                RenderInfo.CullingOffsetMatrix = GetCurrentCullingBasisMatrix(Component.Transform);
            }

            _rc.Mesh = rend;
            _rc.RenderDistance = distance;

            var mat = rend?.Material;
            if (mat is not null)
            {
                if (ShouldRecordImportedTextureStreamingUsage(passes.IsShadowPass, RuntimeEngine.Rendering.State.IsMainPass))
                    XRTexture2D.RecordImportedTextureStreamingUsage(mat, BuildImportedTextureStreamingUsage(rend?.Mesh, camera as XRCamera, distance));
                _rc.RenderPass = mat.RenderPass;
            }

            ApplyHighlightRenderOptionsOverride(mat);
            ModelRenderDiagnostics.LogCommandCollect(this, _rc, passes, camera, distance);
            ProcessPendingGpuMeshBvhRefresh();

            return true;
        }

        #endregion

        #region Public operations

        public void UpdateLOD(XRCamera camera)
            => UpdateLOD(camera.DistanceFromRenderNearPlane(Component.Transform.RenderTranslation));
        public void UpdateLOD(float distanceToCamera)
        {
            lock (_lodsLock)
            {
                if (LODs.Count == 0)
                    return;

                if (_currentLOD is null)
                {
                    CurrentLOD = LODs.First;
                    return;
                }

                while (_currentLOD.Next is not null && distanceToCamera > _currentLOD.Value.MaxVisibleDistance)
                    CurrentLOD = _currentLOD.Next;

                if (_currentLOD.Previous is not null && distanceToCamera < _currentLOD.Previous.Value.MaxVisibleDistance)
                    CurrentLOD = _currentLOD.Previous;
            }
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
                        {
                            RootBone.WorldMatrixChanged -= RootBone_WorldMatrixPreviewChanged;
                            RootBone.RenderMatrixChanged -= RootBone_WorldMatrixChanged;
                        }
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
            switch (propName)
            {
                case nameof(RootBone):
                    if (RootBone is not null)
                    {
                        RootBone.WorldMatrixChanged += RootBone_WorldMatrixPreviewChanged;
                        RootBone.RenderMatrixChanged += RootBone_WorldMatrixChanged;
                        RootBone_WorldMatrixPreviewChanged(RootBone, RootBone.WorldMatrix);
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
                        _rc.WorldMatrix = skinned ? Matrix4x4.Identity : Component.Transform.RenderMatrix;
                    }
                    break;
            }
        }

        #endregion

        #region Disposal

        public void Dispose()
        {
            RuntimeEngine.Rendering.SettingsChanged -= Rendering_SettingsChanged;
            RenderInfo.PropertyChanged -= RenderInfoPropertyChanged;
            UntrackAllBones();
            SkinnedMeshBoundsCalculator.Instance.UnregisterSkinnedMesh(this, World?.VisualScene?.GPUCommands);
            RenderableLOD[] lods;
            lock (_lodsLock)
            {
                lods = [.. LODs];
                CurrentLOD = null;
                LODs.Clear();
            }

            foreach (RenderableLOD lod in lods)
                lod.Renderer.Destroy();

            foreach (XRMesh mesh in _ownedRuntimeMeshes)
                mesh.Destroy(now: true);
            _ownedRuntimeMeshes.Clear();
            DisposeGpuMeshBvh();
            DisposeGpuSkinnedBoundsDebugRenderer();

            lock (_highlightStateLock)
            {
                _rc.MaterialOverride = null;
                _rc.RenderOptionsOverride = null;
                _rc.ForceCpuRendering = false;
                _highlightRenderOptionsOverride = null;
                _highlightSourceMaterial = null;
                _highlightStencilBits = 0;
            }

            GC.SuppressFinalize(this);
        }

        #endregion
    }
}
