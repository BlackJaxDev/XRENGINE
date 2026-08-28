using XREngine.Data.Rendering;
using XREngine.Rendering.Info;
using XREngine.Rendering.Models;

namespace XREngine
{
    public interface IBaseSubMesh
    {
        EventList<SubMeshLOD> LODs { get; }
        int RenderPass { get; set; }
        RenderInfo RenderInfo { get; set; }
    }
}
