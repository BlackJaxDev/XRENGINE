using System.Numerics;
using XREngine.Rendering;
using XREngine.Rendering.Commands;
using YamlDotNet.Serialization;

namespace XREngine.Data.Rendering
{
    public interface IRenderCommandMesh : IRenderCommand
    {
        uint GPUCommandIndex { get; set; }
        uint Instances { get; set; }
        XRMeshRenderer? Mesh { get; set; }
        Matrix4x4 WorldMatrix { get; set; }
        bool WorldMatrixIsModelMatrix { get; set; }
        XRMaterial? MaterialOverride { get; set; }
    }
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
        private uint _instances = 1;
        private bool _worldMatrixIsModelMatrix = true;

        private XRMeshRenderer? _renderMesh;
        private Matrix4x4 _renderWorldMatrix;
        private XRMaterial? _renderMaterialOverride;
        private uint _renderInstances;
        private bool _renderWorldMatrixIsModelMatrix;

        [YamlIgnore]
        public XRMeshRenderer? Mesh
        {
            get => _mesh;
            set => SetField(ref _mesh, value);
        }
        public Matrix4x4 WorldMatrix
        {
            get => _worldMatrix;
            set => SetField(ref _worldMatrix, value);
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
        public uint Instances
        {
            get => _instances;
            set => SetField(ref _instances, value);
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
            mesh.Render(
                _renderWorldMatrixIsModelMatrix ? _renderWorldMatrix : Matrix4x4.Identity,
                _renderMaterialOverride,
                _renderInstances);
            OnPostRender();
        }

        public override void CollectedForRender(XRCamera? camera)
        {
            base.CollectedForRender(camera);
            // Update render distance for proper sorting.
            // This is done in the collect visible thread - doesn't need to be thread safe.
            if (camera != null)
                UpdateRenderDistance(_renderWorldMatrix.Translation, camera);
        }

        public override void SwapBuffers()
        {
            base.SwapBuffers();
            _renderMesh = Mesh;
            _renderWorldMatrix = WorldMatrix;
            _renderMaterialOverride = MaterialOverride;
            _renderInstances = Instances;
            _renderWorldMatrixIsModelMatrix = WorldMatrixIsModelMatrix;
        }
    }
}
