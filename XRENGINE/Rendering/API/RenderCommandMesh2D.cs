using System.Numerics;
using XREngine.Data.Geometry;
using XREngine.Rendering.Commands;
using YamlDotNet.Serialization;

namespace XREngine.Rendering
{
    public class RenderCommandMesh2D : RenderCommand2D
    {
        private XRMeshRenderer? _mesh;
        private Matrix4x4 _worldMatrix = Matrix4x4.Identity;
        private XRMaterial? _materialOverride;
        private uint _instances = 1;
        private BoundingRectangle? _worldCropRegion = null;

        /// <summary>
        /// The mesh to render.
        /// </summary>
        [YamlIgnore]
        public XRMeshRenderer? Mesh
        {
            get => _mesh;
            set => SetField(ref _mesh, value);
        }
        /// <summary>
        /// The transformation of the mesh in world space.
        /// Formally known as the 'model' matrix in the graphics pipeline.
        /// </summary>
        public Matrix4x4 WorldMatrix
        {
            get => _worldMatrix;
            set => SetField(ref _worldMatrix, value);
        }
        /// <summary>
        /// If not null, the material to use for rendering instead of the mesh's default material.
        /// </summary>
        public XRMaterial? MaterialOverride
        {
            get => _materialOverride;
            set => SetField(ref _materialOverride, value);
        }
        /// <summary>
        /// The number of instances to tell the GPU to render.
        /// </summary>
        public uint Instances
        {
            get => _instances;
            set => SetField(ref _instances, value);
        }
        /// <summary>
        /// If not null, the mesh will be cropped to this region before rendering.
        /// Region is in the UI's world space, with the origin at the bottom-left corner of the screen.
        /// </summary>
        public BoundingRectangle? WorldCropRegion
        {
            get => _worldCropRegion;
            set => SetField(ref _worldCropRegion, value);
        }

        public RenderCommandMesh2D() : base() { }
        public RenderCommandMesh2D(int renderPass) : base(renderPass) { }
        public RenderCommandMesh2D(
            int renderPass,
            XRMeshRenderer mesh,
            Matrix4x4 worldMatrix,
            int zIndex,
            XRMaterial? materialOverride = null) : base(renderPass, zIndex)
        {
            RenderPass = renderPass;
            Mesh = mesh;
            WorldMatrix = worldMatrix;
            MaterialOverride = materialOverride;
        }

        public override void Render()
        {
            if (Mesh is null)
                return;

            OnPreRender();
            BeginCrop(WorldCropRegion);
            Mesh.Render(WorldMatrix, MaterialOverride, Instances);
            EndCrop();
            OnPostRender();
        }

        private static void BeginCrop(BoundingRectangle? cropRegion)
        {
            var rend = AbstractRenderer.Current;
            if (rend is null)
                return;
            
            if (cropRegion is null)
                rend.SetCroppingEnabled(false);
            else
            {
                rend.SetCroppingEnabled(true);
                rend.CropRenderArea(cropRegion.Value);
            }
        }
        private static void EndCrop()
            => AbstractRenderer.Current?.SetCroppingEnabled(false);
    }
}
