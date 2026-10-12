using XREngine.Data.Rendering;

namespace XREngine.Rendering.Commands;

/// <summary>Shared authored priority rule used by CPU snapshots and resident ordering publication.</summary>
internal static class RenderCommandSortPriority
{
    internal static int Capture(RenderCommand command)
    {
        if (command.RenderPass != (int)EDefaultRenderPass.TransparentForward || command is not IRenderCommandMesh mesh)
            return 0;
        XRMaterial? material = mesh.MaterialOverride ?? mesh.Mesh?.Material;
        // Earlier and arbitrary shader profiles retain neutral priority in mixed transparent lists.
        return material?.EngineSemantic.IsColorCoverage() == true ? material.TransparentSortPriority : 0;
    }
}
