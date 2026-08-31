using XREngine.Rendering.Materials;

namespace XREngine.Rendering.Vulkan;

public sealed unsafe partial class VulkanExplicitTargetRendererHost
{
    /// <summary>Reads native rows outside production intervals under an authentic completed receipt.</summary>
    public bool TryReadbackMaterialTablePublication(in VulkanExplicitProductionSubmissionReceipt receipt,
        GPUMaterialTablePublication publication, out VulkanMaterialTableDiagnosticSnapshot? snapshot)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _renderer.TryReadbackMaterialTablePublication(in receipt, publication, out snapshot);
    }

    public VulkanMaterialTableDiagnosticCounters GetMaterialTableDiagnostics()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _renderer.GetMaterialTableDiagnostics();
    }
}
