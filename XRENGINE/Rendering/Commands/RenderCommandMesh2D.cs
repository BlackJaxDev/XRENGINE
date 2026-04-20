using System.Numerics;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Models.Materials;
using YamlDotNet.Serialization;

namespace XREngine.Rendering.Commands
{
    public class RenderCommandMesh2D : RenderCommand2D, IRenderCommandMesh
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
        private BoundingRectangle? _worldCropRegion = null;
        private bool _forceCpuRendering;

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
        public RenderingParameters? RenderOptionsOverride
        {
            get => _renderOptionsOverride;
            set => SetField(ref _renderOptionsOverride, value);
        }
        /// <summary>
        /// The number of instances to tell the GPU to render.
        /// </summary>
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
        /// If not null, the mesh will be cropped to this region before rendering.
        /// Region is in the UI's world space, with the origin at the bottom-left corner of the screen.
        /// </summary>
        public BoundingRectangle? WorldCropRegion
        {
            get => _worldCropRegion;
            set => SetField(ref _worldCropRegion, value);
        }

        public bool WorldMatrixIsModelMatrix { get; set; }

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
            Mesh.Render(WorldMatrix, WorldMatrix, MaterialOverride, Instances, renderOptionsOverride: RenderOptionsOverride);
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
