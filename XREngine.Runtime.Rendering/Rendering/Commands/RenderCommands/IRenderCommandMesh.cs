using System.Numerics;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.Commands
{
    public interface IRenderCommandMesh : IRenderCommand
    {
        /// <summary>
        /// Process-stable, non-zero identity shared by CPU-direct and GPU-driven rendering.
        /// </summary>
        uint StableQueryKey { get; }
        uint GPUCommandIndex { get; set; }
        uint Instances { get; set; }
        XRMeshRenderer? Mesh { get; set; }
        Matrix4x4 WorldMatrix { get; set; }
        bool WorldMatrixIsModelMatrix { get; set; }
        XRMaterial? MaterialOverride { get; set; }
        RenderingParameters? RenderOptionsOverride { get; set; }
        bool ForceCpuRendering { get; set; }
    }
}
