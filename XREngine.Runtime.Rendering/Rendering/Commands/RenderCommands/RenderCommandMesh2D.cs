using System.Numerics;
using System.Threading;
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
        private string? _gpuProfilingLabel;

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
        /// Optional stable source label for GPU timing dumps.
        /// </summary>
        public string? GpuProfilingLabel
        {
            get => _gpuProfilingLabel;
            set => SetField(ref _gpuProfilingLabel, value);
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

        private static int s_prepareFailureLogBudget = 24;

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

            const bool forceNoStereo = true;
            OnPreRender();
            try
            {
                BeginCrop(WorldCropRegion);
                if (!Mesh.TryPrepareForRendering(forceNoStereo))
                {
                    if (Interlocked.Decrement(ref s_prepareFailureLogBudget) >= 0)
                    {
                        Mesh.TryPrepareForRendering(out string reason, forceNoStereo);
                        Debug.UI(
                            "[RenderCommandMesh2D] Skipping draw while mesh prepares. pass={0} z={1} label={2} mesh={3} material={4} reason={5}",
                            RenderPass,
                            ZIndex,
                            GpuProfilingLabel ?? "<none>",
                            Mesh.Mesh?.Name ?? Mesh.Name ?? "<unnamed>",
                            (MaterialOverride ?? Mesh.Material)?.Name ?? "<unnamed>",
                            reason);
                    }
                    return;
                }

                Mesh.Render(WorldMatrix, WorldMatrix, MaterialOverride, Instances, forceNoStereo, RenderOptionsOverride);
            }
            finally
            {
                EndCrop();
                OnPostRender();
            }
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
