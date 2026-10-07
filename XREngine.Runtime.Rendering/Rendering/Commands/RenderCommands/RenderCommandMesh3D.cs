using System.Numerics;
using System.Runtime.CompilerServices;
using XREngine;
using XREngine.Data.Core;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Info;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Occlusion;
using YamlDotNet.Serialization;

namespace XREngine.Rendering.Commands
{
    public class RenderCommandMesh3D : RenderCommand3D, IRenderCommandMesh
    {
        private uint _gpuCommandIndex = uint.MaxValue;
        public uint GPUCommandIndex
        {
            get => _gpuCommandIndex;
            set => SetField(ref _gpuCommandIndex, value);
        }

        private XRMeshRenderer? _mesh;
        private XRMeshRenderer? _observedRenderer;
        // A published renderer has all mutation subscriptions from the last bind.
        private XRMeshRenderer? _currentRendererMutationBinding;
        private bool _rendererMutationsAttached;
        // These flags are owned by the subscription gate. A failed refresh leaves
        // its group invalid so a same-reference assignment can retry it.
        private bool _rendererSubscriptionsCurrent;
        private bool _materialSubscriptionsCurrent;
        private bool _materialOptionsSubscriptionsCurrent;
        private bool _meshSubscriptionsCurrent;
        private XRMaterial? _observedMaterialOverride;
        private RenderingParameters? _observedRenderOptionsOverride;
        private EventList<XRMeshRenderer.SubMesh>? _observedSubmeshes;
        private readonly Dictionary<XRMeshRenderer.SubMesh, int> _observedSubmeshCounts = [];
        private readonly HashSet<XRMaterial> _observedMaterials = [];
        private readonly HashSet<RenderingParameters> _observedMaterialOptions = [];
        private readonly Dictionary<XRMesh, XREvent<XRMesh>> _observedMeshDataEvents = [];
        // Refreshes run under the subscription gate. Keep their collection capacity,
        // but clear scratch references after each refresh so removed owners can retire.
        private readonly HashSet<XRMaterial> _currentMaterialsScratch = [];
        private readonly HashSet<RenderingParameters> _currentMaterialOptionsScratch = [];
        private readonly HashSet<XRMesh> _currentMeshesScratch = [];
        private readonly Dictionary<XRMesh, XREvent<XRMesh>> _nextMeshDataEventsScratch = [];
        private XRMeshRenderer.SubMesh[] _materialSubmeshesScratch = [];
        private XRMeshRenderer.SubMesh[] _meshSubmeshesScratch = [];
        private readonly object _rendererSubscriptionsGate = new();
        private Matrix4x4 _worldMatrix = Matrix4x4.Identity;
        private XRMaterial? _materialOverride;
        private RenderingParameters? _renderOptionsOverride;
        private uint _instances = 1;
        private bool _worldMatrixIsModelMatrix = true;
        private bool _forceCpuRendering;
        private uint _editorHighlightBits;
        private AABB? _worldCullingVolumeOverride;
        private string? _gpuProfilingLabel;

        private XRMeshRenderer? _renderMesh;
        private Matrix4x4 _renderWorldMatrix;
        private XRMaterial? _renderMaterialOverride;
        private RenderingParameters? _renderRenderOptionsOverride;
        private uint _renderInstances;
        private bool _renderWorldMatrixIsModelMatrix;
        private bool _renderForceCpuRendering;
        private int _renderPass;
        private uint _renderEditorHighlightBits;
        private GpuSceneOwnerSnapshot _renderGpuSceneOwner;
        private bool _renderBasePassEnabled = true;
        private bool _renderShadowPassEnabled = true;
        private AABB? _renderWorldCullingVolumeOverride;
        private Matrix4x4 _renderPrevWorldMatrix = Matrix4x4.Identity;
        private bool _renderHasPrevWorldMatrix;
        private EAdvancedVelocityValidityReason _renderModelHistoryReason =
            EAdvancedVelocityValidityReason.HistoryReset;
        private static int s_MotionVectorLogBudget = 128;

        private Matrix4x4 _lastSubmittedModelMatrix = Matrix4x4.Identity;
        private bool _lastSubmittedModelMatrixValid;
        private Matrix4x4 _previousModelMatrixForSwapFrame = Matrix4x4.Identity;
        private ulong _lastSubmittedSwapFrameId;
        private volatile bool _temporalSettlementPending;
        private int _swapCaptureInProgress;
        private int _identityNotificationInProgress;
        private ulong _temporalSettlementFrameId;
        private readonly ConditionalWeakTable<XRViewport, OutputModelHistory> _outputModelHistories = new();
        private ulong _lastPhase524bVelocityDiagnosticFrame = ulong.MaxValue;

        private sealed class OutputModelHistory
        {
            public ulong SequenceId;
            public Matrix4x4 LastRenderedModelMatrix = Matrix4x4.Identity;
            public Matrix4x4 PreviousModelMatrixForSequence = Matrix4x4.Identity;
            public bool HasRenderedModel;
            public bool HasSequence;
        }

        private uint _renderGpuCommandIndex = uint.MaxValue;
        private AdvancedGpuSceneDrawIdentitySnapshot _canonicalDrawIdentitySnapshot;
        private AdvancedGpuSceneDrawIdentitySnapshot _renderCanonicalDrawIdentitySnapshot;

        internal override bool _dirty => base._dirty || _temporalSettlementPending;

        /// <summary>
        /// Exact canonical identity for primitive zero in the render-buffer
        /// snapshot. Multi-primitive renderers retain the complete mapping in
        /// <see cref="CanonicalDrawIdentitySnapshot"/>.
        /// </summary>
        internal AdvancedGpuSceneDrawIdentity RenderCanonicalDrawIdentity
            => _renderCanonicalDrawIdentitySnapshot.Primary;

        /// <summary>
        /// Immutable primitive mapping and publication reference captured at
        /// the scene's successful publication boundary.
        /// </summary>
        internal AdvancedGpuSceneDrawIdentitySnapshot CanonicalDrawIdentitySnapshot
            => _renderCanonicalDrawIdentitySnapshot;

        [YamlIgnore]
        public XRMeshRenderer? Mesh
        {
            get => _mesh;
            set
            {
                try
                {
                    SetField(ref _mesh, value);
                }
                finally
                {
                    // A notification can throw after SetField installs the new value.
                    // Keep subscriptions aligned with the value actually retained.
                    if (System.Threading.Volatile.Read(ref _rendererMutationsAttached)
                        && !HasCurrentRendererMutationBinding(_mesh))
                        BindRendererMutations(_mesh);
                }
            }
        }

        /// <summary>Rebind after this command is added to a render info.</summary>
        internal void AttachRendererMutationTracking()
        {
            InvalidateRendererMutationBinding();
            System.Threading.Volatile.Write(ref _rendererMutationsAttached, true);
            BindRendererMutations(_mesh);
            // Changes made while detached must be copied at the next swap.
            MarkDirty();
        }

        /// <summary>Release renderer event roots when the owning render info removes this command.</summary>
        internal void DetachRendererMutationTracking()
        {
            InvalidateRendererMutationBinding();
            System.Threading.Volatile.Write(ref _rendererMutationsAttached, false);
            BindRendererMutations(null);
            lock (_rendererSubscriptionsGate)
            {
                ClearObservedMaterials();
                ClearObservedMeshes();
            }
        }

        private void BindRendererMutations(XRMeshRenderer? renderer)
        {
            lock (_rendererSubscriptionsGate)
            {
                InvalidateRendererMutationBinding();
                if (!ReferenceEquals(_observedRenderer, renderer) || !_rendererSubscriptionsCurrent)
                {
                    _rendererSubscriptionsCurrent = false;
                    _materialSubscriptionsCurrent = false;
                    _meshSubscriptionsCurrent = false;
                    UnbindSubmeshMutations();
                    if (_observedRenderer is not null)
                        _observedRenderer.PropertyChanged -= ObservedRendererPropertyChanged;
                    _observedRenderer = renderer;
                    if (renderer is not null)
                    {
                        renderer.PropertyChanged += ObservedRendererPropertyChanged;
                        BindSubmeshMutations(renderer.Submeshes);
                    }
                    _rendererSubscriptionsCurrent = true;
                }

                // Mutation events keep these groups current between assignments.
                // SetField still runs so cancellation and notification behavior stay intact.
                EnsureObservedMaterialsCurrent();
                if (!_meshSubscriptionsCurrent)
                    RefreshObservedMeshes();

                if (_rendererMutationsAttached && renderer is not null
                    && ReferenceEquals(_observedRenderer, renderer)
                    && _rendererSubscriptionsCurrent && _materialSubscriptionsCurrent
                    && _materialOptionsSubscriptionsCurrent && _meshSubscriptionsCurrent
                    && ReferenceEquals(_observedMaterialOverride, _materialOverride)
                    && ReferenceEquals(_observedRenderOptionsOverride, _renderOptionsOverride))
                    System.Threading.Volatile.Write(ref _currentRendererMutationBinding, renderer);
            }
        }

        private void InvalidateRendererMutationBinding()
            => System.Threading.Volatile.Write(ref _currentRendererMutationBinding, null);

        private bool HasCurrentRendererMutationBinding(XRMeshRenderer? renderer)
        {
            if (renderer is null
                || !ReferenceEquals(System.Threading.Volatile.Read(ref _currentRendererMutationBinding), renderer)
                || !System.Threading.Volatile.Read(ref _rendererSubscriptionsCurrent)
                || !System.Threading.Volatile.Read(ref _materialSubscriptionsCurrent)
                || !System.Threading.Volatile.Read(ref _materialOptionsSubscriptionsCurrent)
                || !System.Threading.Volatile.Read(ref _meshSubscriptionsCurrent)
                || !ReferenceEquals(_observedMaterialOverride, _materialOverride)
                || !ReferenceEquals(_observedRenderOptionsOverride, _renderOptionsOverride))
                return false;

            return ReferenceEquals(System.Threading.Volatile.Read(ref _currentRendererMutationBinding), renderer);
        }

        private void ObservedRendererPropertyChanged(object? sender, IXRPropertyChangedEventArgs args)
        {
            lock (_rendererSubscriptionsGate)
            {
                if (!ReferenceEquals(sender, _observedRenderer))
                    return;

                if (args.PropertyName == nameof(XRMeshRenderer.Submeshes))
                {
                    UnbindSubmeshMutations();
                    BindSubmeshMutations(_observedRenderer!.Submeshes);
                }
                if (args.PropertyName is nameof(XRMeshRenderer.Material) or
                    nameof(XRMeshRenderer.Submeshes))
                    RefreshObservedMaterials();
                if (args.PropertyName is nameof(XRMeshRenderer.Mesh) or
                    nameof(XRMeshRenderer.Submeshes))
                    RefreshObservedMeshes();
            }

            if (args.PropertyName is nameof(XRMeshRenderer.Mesh) or
                nameof(XRMeshRenderer.Material) or
                nameof(XRMeshRenderer.Submeshes) or
                nameof(XRMeshRenderer.SourceSubMeshAsset))
                MarkDirty();
        }

        private void BindSubmeshMutations(EventList<XRMeshRenderer.SubMesh> submeshes)
        {
            InvalidateRendererMutationBinding();
            _rendererSubscriptionsCurrent = false;
            _materialSubscriptionsCurrent = false;
            _meshSubscriptionsCurrent = false;
            _observedSubmeshes = submeshes;
            submeshes.PostAnythingAdded += ObservedSubmeshAdded;
            submeshes.PostAnythingRemoved += ObservedSubmeshRemoved;
            foreach (XRMeshRenderer.SubMesh submesh in submeshes)
                ObserveSubmesh(submesh);
            _rendererSubscriptionsCurrent = true;
        }

        private void UnbindSubmeshMutations()
        {
            InvalidateRendererMutationBinding();
            _rendererSubscriptionsCurrent = false;
            if (_observedSubmeshes is not null)
            {
                _observedSubmeshes.PostAnythingAdded -= ObservedSubmeshAdded;
                _observedSubmeshes.PostAnythingRemoved -= ObservedSubmeshRemoved;
                _observedSubmeshes = null;
            }

            foreach (XRMeshRenderer.SubMesh submesh in _observedSubmeshCounts.Keys)
                submesh.PropertyChanged -= ObservedSubmeshPropertyChanged;
            _observedSubmeshCounts.Clear();
        }

        private void ObserveSubmesh(XRMeshRenderer.SubMesh submesh)
        {
            InvalidateRendererMutationBinding();
            if (_observedSubmeshCounts.TryGetValue(submesh, out int count))
            {
                _observedSubmeshCounts[submesh] = count + 1;
                return;
            }

            _observedSubmeshCounts.Add(submesh, 1);
            submesh.PropertyChanged += ObservedSubmeshPropertyChanged;
        }

        private void ObservedSubmeshAdded(XRMeshRenderer.SubMesh submesh)
        {
            lock (_rendererSubscriptionsGate)
            {
                InvalidateRendererMutationBinding();
                bool wasCurrent = _rendererSubscriptionsCurrent;
                _rendererSubscriptionsCurrent = false;
                ObserveSubmesh(submesh);
                RefreshObservedMaterials();
                RefreshObservedMeshes();
                _rendererSubscriptionsCurrent = wasCurrent;
            }
            MarkDirty();
        }

        private void ObservedSubmeshRemoved(XRMeshRenderer.SubMesh submesh)
        {
            lock (_rendererSubscriptionsGate)
            {
                InvalidateRendererMutationBinding();
                bool wasCurrent = _rendererSubscriptionsCurrent;
                _rendererSubscriptionsCurrent = false;
                if (_observedSubmeshCounts.TryGetValue(submesh, out int count))
                {
                    if (count > 1)
                        _observedSubmeshCounts[submesh] = count - 1;
                    else
                    {
                        _observedSubmeshCounts.Remove(submesh);
                        submesh.PropertyChanged -= ObservedSubmeshPropertyChanged;
                    }
                }
                RefreshObservedMaterials();
                RefreshObservedMeshes();
                _rendererSubscriptionsCurrent = wasCurrent;
            }
            MarkDirty();
        }

        private void ObservedSubmeshPropertyChanged(object? sender, IXRPropertyChangedEventArgs args)
        {
            if (args.PropertyName == nameof(XRMeshRenderer.SubMesh.Material))
            {
                lock (_rendererSubscriptionsGate)
                    RefreshObservedMaterials();
            }
            if (args.PropertyName == nameof(XRMeshRenderer.SubMesh.Mesh))
            {
                lock (_rendererSubscriptionsGate)
                    RefreshObservedMeshes();
            }
            if (args.PropertyName is nameof(XRMeshRenderer.SubMesh.Mesh) or
                nameof(XRMeshRenderer.SubMesh.Material))
                MarkDirty();
        }

        private void EnsureObservedMaterialsCurrent()
        {
            if (!_materialSubscriptionsCurrent || !ReferenceEquals(_observedMaterialOverride, _materialOverride))
                RefreshObservedMaterials();
            else if (!_materialOptionsSubscriptionsCurrent || !ReferenceEquals(_observedRenderOptionsOverride, _renderOptionsOverride))
                RefreshObservedMaterialOptions();
        }

        private void RefreshObservedMaterials()
        {
            InvalidateRendererMutationBinding();
            _materialSubscriptionsCurrent = false;
            XRMaterial? materialOverride = _materialOverride;
            HashSet<XRMaterial> current = _currentMaterialsScratch;
            int submeshCount = 0;
            try
            {
                if (_rendererMutationsAttached)
                {
                    if (materialOverride is not null)
                        current.Add(materialOverride);
                    if (_observedRenderer?.Material is XRMaterial rendererMaterial)
                        current.Add(rendererMaterial);
                    if (_observedSubmeshes is not null)
                    {
                        submeshCount = _observedSubmeshes.CopySnapshot(ref _materialSubmeshesScratch);
                        for (int index = 0; index < submeshCount; ++index)
                        {
                            XRMeshRenderer.SubMesh submesh = _materialSubmeshesScratch[index];
                            if (submesh.Material is XRMaterial material)
                                current.Add(material);
                        }
                    }
                }

                foreach (XRMaterial material in _observedMaterials)
                    if (!current.Contains(material))
                        material.PropertyChanged -= ObservedMaterialPropertyChanged;
                foreach (XRMaterial material in current)
                    if (!_observedMaterials.Contains(material))
                        material.PropertyChanged += ObservedMaterialPropertyChanged;
                _observedMaterials.Clear();
                foreach (XRMaterial material in current)
                    _observedMaterials.Add(material);
            }
            finally
            {
                current.Clear();
                Array.Clear(_materialSubmeshesScratch, 0, submeshCount);
            }
            RefreshObservedMaterialOptions();
            _observedMaterialOverride = materialOverride;
            _materialSubscriptionsCurrent = true;
        }

        private void RefreshObservedMaterialOptions()
        {
            InvalidateRendererMutationBinding();
            _materialOptionsSubscriptionsCurrent = false;
            RenderingParameters? renderOptionsOverride = _renderOptionsOverride;
            HashSet<RenderingParameters> current = _currentMaterialOptionsScratch;
            try
            {
                if (_rendererMutationsAttached && renderOptionsOverride is not null)
                    current.Add(renderOptionsOverride);
                foreach (XRMaterial material in _observedMaterials)
                    current.Add(material.RenderOptions);

                foreach (RenderingParameters options in _observedMaterialOptions)
                    if (!current.Contains(options))
                        options.PropertyChanged -= ObservedMaterialOptionsPropertyChanged;
                foreach (RenderingParameters options in current)
                    if (!_observedMaterialOptions.Contains(options))
                        options.PropertyChanged += ObservedMaterialOptionsPropertyChanged;
                _observedMaterialOptions.Clear();
                foreach (RenderingParameters options in current)
                    _observedMaterialOptions.Add(options);
            }
            finally
            {
                current.Clear();
            }
            _observedRenderOptionsOverride = renderOptionsOverride;
            _materialOptionsSubscriptionsCurrent = true;
        }

        private void ClearObservedMaterials()
        {
            InvalidateRendererMutationBinding();
            _materialSubscriptionsCurrent = false;
            _materialOptionsSubscriptionsCurrent = false;
            _observedMaterialOverride = null;
            _observedRenderOptionsOverride = null;
            foreach (XRMaterial material in _observedMaterials)
                material.PropertyChanged -= ObservedMaterialPropertyChanged;
            _observedMaterials.Clear();
            foreach (RenderingParameters options in _observedMaterialOptions)
                options.PropertyChanged -= ObservedMaterialOptionsPropertyChanged;
            _observedMaterialOptions.Clear();
        }

        private void RefreshObservedMeshes()
        {
            InvalidateRendererMutationBinding();
            _meshSubscriptionsCurrent = false;
            HashSet<XRMesh> current = _currentMeshesScratch;
            Dictionary<XRMesh, XREvent<XRMesh>> next = _nextMeshDataEventsScratch;
            int submeshCount = 0;
            try
            {
                if (_rendererMutationsAttached)
                {
                    if (_observedRenderer?.Mesh is XRMesh rendererMesh)
                        current.Add(rendererMesh);
                    if (_observedSubmeshes is not null)
                    {
                        submeshCount = _observedSubmeshes.CopySnapshot(ref _meshSubmeshesScratch);
                        for (int index = 0; index < submeshCount; ++index)
                        {
                            XRMeshRenderer.SubMesh submesh = _meshSubmeshesScratch[index];
                            if (submesh.Mesh is XRMesh mesh)
                                current.Add(mesh);
                        }
                    }
                }

                foreach ((XRMesh mesh, XREvent<XRMesh> dataChanged) in _observedMeshDataEvents)
                    if (!current.Contains(mesh))
                    {
                        mesh.PropertyChanged -= ObservedMeshPropertyChanged;
                        dataChanged.RemoveListener(ObservedMeshDataChanged);
                    }
                foreach (XRMesh mesh in current)
                    if (!_observedMeshDataEvents.ContainsKey(mesh))
                    {
                        mesh.PropertyChanged += ObservedMeshPropertyChanged;
                        (mesh.DataChanged ??= new XREvent<XRMesh>()).AddListener(ObservedMeshDataChanged);
                    }

                foreach (XRMesh mesh in current)
                    next.Add(mesh, _observedMeshDataEvents.TryGetValue(mesh, out XREvent<XRMesh>? previous)
                        ? previous : mesh.DataChanged!);
                _observedMeshDataEvents.Clear();
                foreach ((XRMesh mesh, XREvent<XRMesh> dataChanged) in next)
                    _observedMeshDataEvents.Add(mesh, dataChanged);
            }
            finally
            {
                current.Clear();
                next.Clear();
                Array.Clear(_meshSubmeshesScratch, 0, submeshCount);
            }
            _meshSubscriptionsCurrent = true;
        }

        private void ClearObservedMeshes()
        {
            InvalidateRendererMutationBinding();
            _meshSubscriptionsCurrent = false;
            foreach ((XRMesh mesh, XREvent<XRMesh> dataChanged) in _observedMeshDataEvents)
            {
                mesh.PropertyChanged -= ObservedMeshPropertyChanged;
                dataChanged.RemoveListener(ObservedMeshDataChanged);
            }
            _observedMeshDataEvents.Clear();
        }

        private void ObservedMeshPropertyChanged(object? sender, IXRPropertyChangedEventArgs args)
        {
            if (args.PropertyName is nameof(XRMesh.Bounds) or
                nameof(XRMesh.UtilizedBones) or
                nameof(XRMesh.BlendshapeNames))
                MarkDirty();
        }

        private void ObservedMeshDataChanged(XRMesh mesh)
            => MarkDirty();

        private void ObservedMaterialPropertyChanged(object? sender, IXRPropertyChangedEventArgs args)
        {
            if (args.PropertyName == nameof(XRMaterial.RenderOptions))
            {
                lock (_rendererSubscriptionsGate)
                    RefreshObservedMaterialOptions();
            }

            if (args.PropertyName is nameof(XRMaterial.RenderOptions) or
                nameof(XRMaterial.TransparencyMode) or
                nameof(XRMaterial.TransparentTechniqueOverride) or
                nameof(XRMaterial.AlphaCutoff) or
                nameof(XRMaterial.TransparentSortPriority) or
                nameof(XRMaterial.RenderPass) or
                nameof(XRMaterial.PassSet) or
                nameof(XRMaterial.ShaderStateRevision) or
                nameof(XRMaterial.UberStateRevision))
                MarkDirty();
        }

        private void ObservedMaterialOptionsPropertyChanged(object? sender, IXRPropertyChangedEventArgs args)
        {
            if (args.PropertyName is nameof(RenderingParameters.CullMode) or
                nameof(RenderingParameters.ExcludeFromGpuIndirect) or
                nameof(RenderingParameters.AlphaToCoverage) or
                nameof(RenderingParameters.BlendModeAllDrawBuffers) or
                nameof(RenderingParameters.BlendModesPerDrawBuffer))
                MarkDirty();
        }
        public Matrix4x4 WorldMatrix
        {
            get => _worldMatrix;
            set
            {
                bool changed = SetField(ref _worldMatrix, value);

                if (changed && RuntimeRenderingHostServices.FrameTiming.IsRenderThread &&
                    !IsInsideSynchronousCallback &&
                    System.Threading.Volatile.Read(ref _swapCaptureInProgress) == 0 &&
                    System.Threading.Volatile.Read(ref _identityNotificationInProgress) == 0)
                    ApplyLateRenderThreadWorldMatrix(value);
            }
        }
        public bool WorldMatrixIsModelMatrix
        {
            get => _worldMatrixIsModelMatrix;
            set => SetField(ref _worldMatrixIsModelMatrix, value);
        }
        public XRMaterial? MaterialOverride
        {
            get => _materialOverride;
            set
            {
                try
                {
                    SetField(ref _materialOverride, value);
                }
                finally
                {
                    if (_rendererMutationsAttached)
                        lock (_rendererSubscriptionsGate)
                            EnsureObservedMaterialsCurrent();
                }
            }
        }
        public RenderingParameters? RenderOptionsOverride
        {
            get => _renderOptionsOverride;
            set
            {
                try
                {
                    SetField(ref _renderOptionsOverride, value);
                }
                finally
                {
                    if (_rendererMutationsAttached)
                        lock (_rendererSubscriptionsGate)
                            if (!_materialOptionsSubscriptionsCurrent || !ReferenceEquals(_observedRenderOptionsOverride, _renderOptionsOverride))
                                RefreshObservedMaterialOptions();
                }
            }
        }
        public uint Instances
        {
            get => _instances;
            set => SetField(ref _instances, value);
        }
        public bool ForceCpuRendering
        {
            get => _forceCpuRendering;
            set => SetField(ref _forceCpuRendering, value);
        }
        /// <summary>
        /// Editor hover (bit 0) and selection (bit 1). Native visibility carries
        /// these as metadata; traditional draws retain the stencil override.
        /// </summary>
        public uint EditorHighlightBits
        {
            get => _editorHighlightBits;
            set => SetField(ref _editorHighlightBits, value & 3u);
        }

        /// <summary>
        /// Optional world-space bounds supplied by the owning render info. Skinned meshes use this
        /// because their draw matrix is identity while culling is rooted at the skeleton/bounds basis.
        /// </summary>
        [YamlIgnore]
        public AABB? WorldCullingVolumeOverride
        {
            get => _worldCullingVolumeOverride;
            set => SetField(ref _worldCullingVolumeOverride, value);
        }
        /// <summary>
        /// Optional stable source label for GPU timing dumps.
        /// </summary>
        public string? GpuProfilingLabel
        {
            get => _gpuProfilingLabel;
            set => SetField(ref _gpuProfilingLabel, value);
        }

        public RenderCommandMesh3D() : base() { }
        public RenderCommandMesh3D(int renderPass) : base(renderPass) { }
        public RenderCommandMesh3D(EDefaultRenderPass renderPass) : base((int)renderPass) { }
        public RenderCommandMesh3D(
            int renderPass,
            XRMeshRenderer renderer,
            Matrix4x4 worldMatrix,
            XRMaterial? materialOverride = null) : base(renderPass)
        {
            Mesh = renderer;
            WorldMatrix = worldMatrix;
            MaterialOverride = materialOverride;
        }

        public override void Render()
        {
            var mesh = _renderMesh;
            if (mesh is null)
                return;

            OnPreRender();
            try
            {
                if (!_renderHasPrevWorldMatrix && s_MotionVectorLogBudget-- > 0)
                {
                    Debug.Rendering($"[MotionVectors] Missing prev model; treating as static. WorldIsModel={_renderWorldMatrixIsModelMatrix}, Instances={_renderInstances}");
                }

                using var _ = RuntimeRenderingHostServices.DebugDrawing.PushTransformId(StableQueryKey);

                Matrix4x4 modelMatrix = GetModelMatrix();
                Matrix4x4 previousModelMatrix = GetPreviousModelMatrix(modelMatrix);
                LogPhase524bVelocityMatricesIfNeeded(mesh, modelMatrix, previousModelMatrix);
                mesh.Render(
                    modelMatrix,
                    previousModelMatrix,
                    _renderMaterialOverride,
                    _renderInstances,
                    renderOptionsOverride: _renderRenderOptionsOverride,
                    canonicalDrawIdentitySnapshot: _renderCanonicalDrawIdentitySnapshot);
            }
            finally
            {
                OnPostRender();
            }
        }

        public override void CollectedForRender(IRuntimeRenderCamera? camera)
        {
            base.CollectedForRender(camera);
            // Update render distance for proper sorting.
            // This is done in the collect visible thread - doesn't need to be thread safe.
            if (camera != null)
            {
                if (CullingVolume is AABB bounds && bounds.IsValid)
                    UpdateRenderDistance(bounds, camera);
                else
                    UpdateRenderDistance(_renderWorldMatrix.Translation, camera);
            }
        }

        internal override float CaptureSortDistance(IRuntimeRenderCamera? camera)
        {
            if (camera is null)
                return base.CaptureSortDistance(camera);

            return CullingVolume is AABB bounds && bounds.IsValid
                ? CalculateRenderDistance(bounds, camera)
                : Vector3.DistanceSquared(camera.Transform.RenderTranslation, _renderWorldMatrix.Translation);
        }

        public override void SwapBuffers()
        {
            ulong swapFrameId = RuntimeRenderingHostServices.FrameTiming.SwapFrameId;
            // A second collection may reach this command during the same swap boundary.
            // Keep the moving snapshot until the next frame unless a real mutation arrived.
            if (_temporalSettlementPending && !base._dirty &&
                swapFrameId != 0UL && swapFrameId == _temporalSettlementFrameId)
                return;

            if (System.Threading.Interlocked.CompareExchange(
                    ref _swapCaptureInProgress, 1, 0) != 0)
                throw new InvalidOperationException("A mesh command swap is already in progress.");
            try
            {
                SwapBuffersCore(swapFrameId);
            }
            finally
            {
                System.Threading.Volatile.Write(ref _swapCaptureInProgress, 0);
            }
        }

        private void SwapBuffersCore(ulong swapFrameId)
        {
            long capturedVersion = BeginSwapBuffers();
            _renderMesh = Mesh;
            _renderWorldMatrix = WorldMatrix;
            _renderMaterialOverride = MaterialOverride;
            _renderRenderOptionsOverride = RenderOptionsOverride;
            _renderInstances = Instances;
            _renderWorldMatrixIsModelMatrix = WorldMatrixIsModelMatrix;
            _renderForceCpuRendering = ForceCpuRendering;
            _renderPass = RenderPass;
            _renderEditorHighlightBits = EditorHighlightBits;
            _renderGpuSceneOwner = GpuSceneOwnerSnapshot.CaptureLive(OwnerRenderInfo);
            XRMaterial? effectiveMaterial = _renderMaterialOverride ?? _renderMesh?.Material;
            bool authoredEnabled = Enabled && (OwnerRenderInfo?.ShouldRender ?? true);
            bool isPrimary = OwnerRenderInfo is RenderInfo3D { OwnerRenderableMesh: { } ownerMesh } &&
                ownerMesh.IsPrimaryCommand(this);
            _renderBasePassEnabled = authoredEnabled &&
                (!isPrimary || XREngine.Components.Scene.Mesh.RenderableMesh.IsPrimaryMaterialPassEnabled(effectiveMaterial, false));
            _renderShadowPassEnabled = authoredEnabled &&
                (!isPrimary || XREngine.Components.Scene.Mesh.RenderableMesh.IsPrimaryMaterialPassEnabled(effectiveMaterial, true));
            _renderWorldCullingVolumeOverride = WorldCullingVolumeOverride;
            _renderGpuCommandIndex = GPUCommandIndex;
            _renderCanonicalDrawIdentitySnapshot = _canonicalDrawIdentitySnapshot;
            bool settleModelHistory = false;
            bool hadPreviousModelMatrix = _lastSubmittedModelMatrixValid;
            if (_renderWorldMatrixIsModelMatrix)
            {
                _renderPrevWorldMatrix = hadPreviousModelMatrix
                    ? swapFrameId != 0UL && _lastSubmittedSwapFrameId == swapFrameId
                        ? _previousModelMatrixForSwapFrame
                        : _lastSubmittedModelMatrix
                    : _renderWorldMatrix;
                settleModelHistory = !hadPreviousModelMatrix ||
                    _renderPrevWorldMatrix != _renderWorldMatrix;
                _renderHasPrevWorldMatrix = true;
                _renderModelHistoryReason = hadPreviousModelMatrix
                    ? EAdvancedVelocityValidityReason.Valid
                    : EAdvancedVelocityValidityReason.HistoryReset;
            }
            else
            {
                // For non-model matrices, treat as static so motion vectors stay zero.
                _renderPrevWorldMatrix = _renderWorldMatrix;
                _renderHasPrevWorldMatrix = true;
                _renderModelHistoryReason = EAdvancedVelocityValidityReason.Valid;
            }

            bool submittedWorldMatrixIsModelMatrix = _renderWorldMatrixIsModelMatrix;
            Matrix4x4 submittedWorldMatrix = _renderWorldMatrix;
            Matrix4x4 submittedPreviousWorldMatrix = _renderPrevWorldMatrix;
            CompleteSwapBuffers(capturedVersion);
            if (submittedWorldMatrixIsModelMatrix)
            {
                if (!hadPreviousModelMatrix || swapFrameId == 0UL ||
                    _lastSubmittedSwapFrameId != swapFrameId)
                    _previousModelMatrixForSwapFrame = submittedPreviousWorldMatrix;
                _lastSubmittedModelMatrix = submittedWorldMatrix;
                _lastSubmittedModelMatrixValid = true;
            }
            else
            {
                _lastSubmittedModelMatrix = Matrix4x4.Identity;
                _lastSubmittedModelMatrixValid = false;
            }
            _lastSubmittedSwapFrameId = swapFrameId;
            // The next frame advances previous to current once motion stops. This flag
            // is independent of real mutations, so identity delivery cannot trigger it.
            _temporalSettlementFrameId = swapFrameId;
            _temporalSettlementPending = settleModelHistory;
        }

        protected override bool IsRenderStateDirtyProperty(string? propName)
            => propName != nameof(PublishCanonicalDrawIdentities) &&
                base.IsRenderStateDirtyProperty(propName);

        internal void PublishCanonicalDrawIdentities(
            AdvancedSharedGpuSceneDatabase database,
            in AdvancedGpuScenePublicationReference publication,
            ReadOnlySpan<AdvancedGpuHandle> handles)
        {
            S13aPublicationTelemetry.Trace(S13aPublicationTraceEventKind.IdentityPublished,
                StableQueryKey, publicationSequence: publication.Sequence,
                detail: handles.Length);
            AdvancedGpuSceneDrawHandleSet? existing = _canonicalDrawIdentitySnapshot.Handles;
            AdvancedGpuSceneDrawHandleSet handleSet = existing is not null && existing.Matches(handles)
                ? existing
                : new AdvancedGpuSceneDrawHandleSet(handles);
            AdvancedGpuSceneDrawIdentitySnapshot snapshot = new(
                database,
                publication,
                handleSet);
            System.Threading.Interlocked.Increment(ref _identityNotificationInProgress);
            EnterSynchronousCallback();
            try
            {
                SetField(ref _canonicalDrawIdentitySnapshot, snapshot);
                // GPUScene seals after the generic render tree has swapped for this
                // frame. Publish the same immutable snapshot to the render buffer
                // so Vulkan sees the exact resident publication immediately rather
                // than one swap later.
                SetField(ref _renderCanonicalDrawIdentitySnapshot, snapshot);
            }
            finally
            {
                LeaveSynchronousCallback();
                System.Threading.Interlocked.Decrement(ref _identityNotificationInProgress);
            }
            if (_canonicalDrawIdentitySnapshot != snapshot ||
                _renderCanonicalDrawIdentitySnapshot != snapshot)
                throw new InvalidOperationException("Canonical draw identity delivery was rejected by a property notification.");
        }

        /// <summary>
        /// Captures the immutable render-buffer state used by aggregate GPU
        /// preparation. This avoids reading mutable update-side properties
        /// after a <see cref="RenderWorldSnapshot"/> has been published.
        /// </summary>
        internal AdvancedMeshRenderSnapshot CaptureAdvancedPreparationSnapshot()
            => new(
                _renderMesh,
                _renderWorldMatrix,
                _renderHasPrevWorldMatrix
                    ? _renderPrevWorldMatrix
                    : _renderWorldMatrix,
                _renderInstances,
                _renderWorldMatrixIsModelMatrix,
                _renderModelHistoryReason,
                _renderForceCpuRendering,
                _renderMaterialOverride,
                _renderRenderOptionsOverride);

        /// <summary>Returns the command inputs sealed before swap callbacks run.</summary>
        internal GpuSceneMeshCommandSnapshot CaptureGpuSceneSnapshot()
            => new(_renderMesh, _renderWorldMatrix, _renderWorldMatrixIsModelMatrix,
                _renderMaterialOverride, _renderInstances, _renderPass,
                _renderForceCpuRendering, _renderEditorHighlightBits, StableQueryKey,
                _renderGpuSceneOwner, _renderBasePassEnabled, _renderShadowPassEnabled);

        internal void ApplyLateRenderThreadWorldMatrix(Matrix4x4 worldMatrix)
        {
            _renderWorldMatrix = worldMatrix;

            if (_renderWorldMatrixIsModelMatrix && !_renderHasPrevWorldMatrix)
            {
                _renderPrevWorldMatrix = _lastSubmittedModelMatrixValid ? _lastSubmittedModelMatrix : worldMatrix;
                _renderHasPrevWorldMatrix = true;
                _renderModelHistoryReason = _lastSubmittedModelMatrixValid
                    ? EAdvancedVelocityValidityReason.Valid
                    : EAdvancedVelocityValidityReason.HistoryReset;
            }
        }

        private Matrix4x4 GetModelMatrix()
            => _renderWorldMatrixIsModelMatrix ? _renderWorldMatrix : Matrix4x4.Identity;

        private Matrix4x4 GetPreviousModelMatrix(in Matrix4x4 currentModelMatrix)
        {
            if (!_renderHasPrevWorldMatrix)
                return currentModelMatrix;

            if (!_renderWorldMatrixIsModelMatrix)
                return currentModelMatrix;

            XRViewport? viewport = RuntimeEngine.Rendering.State.RenderingViewport;
            if (viewport is null)
                return _renderPrevWorldMatrix;

            OutputModelHistory history = _outputModelHistories.GetOrCreateValue(viewport);
            ulong sequenceId = viewport.SceneRenderSequenceId;
            if (!history.HasSequence || history.SequenceId != sequenceId)
            {
                bool consecutive = history.HasSequence && unchecked(history.SequenceId + 1UL) == sequenceId;
                history.PreviousModelMatrixForSequence = consecutive && history.HasRenderedModel
                    ? history.LastRenderedModelMatrix
                    : currentModelMatrix;
                history.LastRenderedModelMatrix = currentModelMatrix;
                history.HasRenderedModel = true;
                history.SequenceId = sequenceId;
                history.HasSequence = true;
            }

            return history.PreviousModelMatrixForSequence;
        }

        private void LogPhase524bVelocityMatricesIfNeeded(
            XRMeshRenderer mesh,
            in Matrix4x4 modelMatrix,
            in Matrix4x4 previousModelMatrix)
        {
            if (!CpuOcclusionValidationEvidence.Enabled ||
                mesh.Material?.Name != CpuOcclusionValidationEvidence.SpsMovingSentinelMaterialName ||
                RuntimeEngine.Rendering.State.CurrentRenderingPipeline?.RenderState.CurrentRenderTargetBinding?.Name != DefaultRenderPipeline.VelocityFBOName)
            {
                return;
            }

            ulong frameId = RuntimeEngine.Rendering.State.RenderFrameId;
            if (_lastPhase524bVelocityDiagnosticFrame == frameId)
                return;

            _lastPhase524bVelocityDiagnosticFrame = frameId;
            Debug.Rendering(
                "[Phase524bVelocity] renderFrame={0} sequenceFrame={1} current=({2:F5},{3:F5},{4:F5}) previous=({5:F5},{6:F5},{7:F5}) delta=({8:F5},{9:F5},{10:F5}).",
                frameId,
                Phase524bTemporalScenarioDiagnostics.SequenceFrame,
                modelMatrix.M41,
                modelMatrix.M42,
                modelMatrix.M43,
                previousModelMatrix.M41,
                previousModelMatrix.M42,
                previousModelMatrix.M43,
                modelMatrix.M41 - previousModelMatrix.M41,
                modelMatrix.M42 - previousModelMatrix.M42,
                modelMatrix.M43 - previousModelMatrix.M43);
        }

        internal bool TryGetCpuOcclusionSnapshot(
            out XRMeshRenderer? mesh,
            out Matrix4x4 modelMatrix,
            out XRMaterial? materialOverride,
            out RenderingParameters? renderOptionsOverride,
            out uint instances)
        {
            mesh = _renderMesh;
            modelMatrix = GetModelMatrix();
            materialOverride = _renderMaterialOverride;
            renderOptionsOverride = _renderRenderOptionsOverride;
            instances = _renderInstances;
            return mesh is not null;
        }

        /// <summary>
        /// World-space AABB for this mesh command, used by the CPU occlusion coordinator's
        /// proxy-probe path (depth-only AABB redraw for retest, no visible flicker).
        /// Computed from the mesh's local-space bounds transformed by the current model
        /// matrix. Returns null when the mesh or its bounds are unavailable.
        /// </summary>
        public override AABB? CullingVolume
        {
            get
            {
                if (RenderDiagnosticsFlags.ForceSkinnedUnbounded && UsesDeformedMesh(_renderMesh ?? _mesh))
                    return null;

                XRMeshRenderer? commandRenderer = _dirty ? _mesh : _renderMesh ?? _mesh;
                if (OwnerRenderInfo is XREngine.Rendering.Info.RenderInfo3D owner &&
                    ReferenceEquals(commandRenderer, owner.OwnerRenderableMesh?.CurrentLODRenderer) &&
                    owner.TryGetCommittedWorldBounds(out AABB committedBounds, out _))
                    return committedBounds;

                if (TryGetWorldCullingVolumeOverride(out AABB overrideBounds))
                    return overrideBounds;

                XRMesh? meshAsset = _renderMesh?.Mesh ?? _mesh?.Mesh;
                if (meshAsset is null)
                    return null;

                Matrix4x4 modelMatrix = GetModelMatrix();
                if (modelMatrix == Matrix4x4.Identity)
                    return meshAsset.Bounds;

                return meshAsset.Bounds.Transformed(modelMatrix);
            }
        }

        public bool TryGetWorldCullingVolumeOverride(out AABB bounds)
        {
            if (_dirty && _worldCullingVolumeOverride is AABB dirtyBounds)
            {
                bounds = dirtyBounds;
                return bounds.IsValid;
            }

            if (_renderWorldCullingVolumeOverride is AABB renderBounds)
            {
                bounds = renderBounds;
                return bounds.IsValid;
            }

            if (_worldCullingVolumeOverride is AABB currentBounds)
            {
                bounds = currentBounds;
                return bounds.IsValid;
            }

            bounds = default;
            return false;
        }

        private static bool UsesDeformedMesh(XRMeshRenderer? renderer)
        {
            if (renderer is null)
                return false;

            if (IsDeformedMesh(renderer.Mesh))
                return true;

            for (int i = 0; i < renderer.Submeshes.Count; i++)
            {
                if (IsDeformedMesh(renderer.Submeshes[i].Mesh))
                    return true;
            }

            return false;
        }

        private static bool IsDeformedMesh(XRMesh? mesh)
            => mesh is not null && (mesh.HasSkinning || mesh.BlendshapeCount > 0);
    }
}
