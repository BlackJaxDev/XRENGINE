using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering;

/// <summary>
/// Supplies override materials used by reusable scene render commands.
/// </summary>
public interface IRenderPipelinePassMaterialProvider
{
    XRMaterial GetMotionVectorsMaterial();
    XRMaterial GetDepthNormalPrePassMaterial();
    XRMaterial GetFullOverdrawCountMaterial();
}
