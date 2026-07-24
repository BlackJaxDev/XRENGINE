using System.Drawing.Drawing2D;
using System.Numerics;
using System.Runtime.CompilerServices;
using XREngine;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Commands;
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
        private Matrix4x4 _worldMatrix = Matrix4x4.Identity;
        private XRMaterial? _materialOverride;
        private RenderingParameters? _renderOptionsOverride;
        private uint _instances = 1;
        private bool _worldMatrixIsModelMatrix = true;
        private bool _forceCpuRendering;
        private AABB? _worldCullingVolumeOverride;
        private string? _gpuProfilingLabel;

        private XRMeshRenderer? _renderMesh;
        private Matrix4x4 _renderWorldMatrix;
        private XRMaterial? _renderMaterialOverride;
        private RenderingParameters? _renderRenderOptionsOverride;
        private uint _renderInstances;
        private bool _renderWorldMatrixIsModelMatrix;
        private bool _renderForceCpuRendering;
        private AABB? _renderWorldCullingVolumeOverride;
        private Matrix4x4 _renderPrevWorldMatrix = Matrix4x4.Identity;
        private bool _renderHasPrevWorldMatrix;
        private static int s_MotionVectorLogBudget = 128;

        private Matrix4x4 _lastSubmittedModelMatrix = Matrix4x4.Identity;
        private bool _lastSubmittedModelMatrixValid;
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

        [YamlIgnore]
        public XRMeshRenderer? Mesh
        {
            get => _mesh;
            set => SetField(ref _mesh, value);
        }
        public Matrix4x4 WorldMatrix
        {
            get => _worldMatrix;
            set
            {
                SetField(ref _worldMatrix, value);

                if (RuntimeRenderingHostServices.FrameTiming.IsRenderThread)
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
            set => SetField(ref _materialOverride, value);
        }
        public RenderingParameters? RenderOptionsOverride
        {
            get => _renderOptionsOverride;
            set => SetField(ref _renderOptionsOverride, value);
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

                using var _ = RuntimeRenderingHostServices.DebugDrawing.PushTransformId(_renderGpuCommandIndex == uint.MaxValue ? 0u : _renderGpuCommandIndex);

                Matrix4x4 modelMatrix = GetModelMatrix();
                Matrix4x4 previousModelMatrix = GetPreviousModelMatrix(modelMatrix);
                LogPhase524bVelocityMatricesIfNeeded(mesh, modelMatrix, previousModelMatrix);
                mesh.Render(
                    modelMatrix,
                    previousModelMatrix,
                    _renderMaterialOverride,
                    _renderInstances,
                    renderOptionsOverride: _renderRenderOptionsOverride);
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
            _renderMesh = Mesh;
            _renderWorldMatrix = WorldMatrix;
            _renderMaterialOverride = MaterialOverride;
            _renderRenderOptionsOverride = RenderOptionsOverride;
            _renderInstances = Instances;
            _renderWorldMatrixIsModelMatrix = WorldMatrixIsModelMatrix;
            _renderForceCpuRendering = ForceCpuRendering;
            _renderWorldCullingVolumeOverride = WorldCullingVolumeOverride;
            _renderGpuCommandIndex = GPUCommandIndex;
            if (_renderWorldMatrixIsModelMatrix)
            {
                _renderPrevWorldMatrix = _lastSubmittedModelMatrixValid ? _lastSubmittedModelMatrix : _renderWorldMatrix;
                _renderHasPrevWorldMatrix = true;
                _lastSubmittedModelMatrix = _renderWorldMatrix;
                _lastSubmittedModelMatrixValid = true;
            }
            else
            {
                // For non-model matrices, treat as static so motion vectors stay zero.
                _renderPrevWorldMatrix = _renderWorldMatrix;
                _renderHasPrevWorldMatrix = true;
                _lastSubmittedModelMatrix = Matrix4x4.Identity;
                _lastSubmittedModelMatrixValid = false;
            }

            base.SwapBuffers();
        }

        internal void ApplyLateRenderThreadWorldMatrix(Matrix4x4 worldMatrix)
        {
            _renderWorldMatrix = worldMatrix;

            if (_renderWorldMatrixIsModelMatrix && !_renderHasPrevWorldMatrix)
            {
                _renderPrevWorldMatrix = _lastSubmittedModelMatrixValid ? _lastSubmittedModelMatrix : worldMatrix;
                _renderHasPrevWorldMatrix = true;
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
