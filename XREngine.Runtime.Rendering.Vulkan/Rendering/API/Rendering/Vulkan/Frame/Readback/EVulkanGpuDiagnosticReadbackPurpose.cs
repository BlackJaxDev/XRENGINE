namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Classifies a delayed GPU diagnostic observation without changing the sealed
/// mesh submission strategy that produced its source data.
/// </summary>
internal enum EVulkanGpuDiagnosticReadbackPurpose : byte
{
    /// <summary>Ordinary readback attached to an instrumented submission.</summary>
    Instrumented,

    /// <summary>
    /// Fence-delayed evidence copied from a dedicated snapshot while validating
    /// the production meshlet zero-readback lane.
    /// </summary>
    MeshletZeroReadbackEvidence,
}
