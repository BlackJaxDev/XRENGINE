using System.Numerics;
using XREngine.Rendering;
using XREngine.Rendering.Commands;

namespace XREngine.Data.Rendering
{
    public class RenderCommandMesh2D : RenderCommand2D
    {
        private XRMeshRenderer? _mesh;
        private Matrix4x4 _worldMatrix = Matrix4x4.Identity;
        private XRMaterial? _materialOverride;

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

        public XRMaterial? MaterialOverride
        {
            get => _materialOverride;
            set => SetField(ref _materialOverride, value);
        }

        public RenderCommandMesh2D() : base() { }
        public RenderCommandMesh2D(int renderPass) : base(renderPass) { }
        public RenderCommandMesh2D(
            int renderPass,
            XRMeshRenderer manager,
            Matrix4x4 worldMatrix,
            int zIndex,
            XRMaterial? materialOverride = null) : base(renderPass, zIndex)
        {
            RenderPass = renderPass;
            Mesh = manager;
            WorldMatrix = worldMatrix;
            MaterialOverride = materialOverride;
        }

        public override void Render()
        {
            var m = Mesh;
            if (m is null)
                return;

            OnPreRender();
            m.Render(WorldMatrix, WorldMatrix, MaterialOverride);
            OnPostRender();
        }
    }
}
