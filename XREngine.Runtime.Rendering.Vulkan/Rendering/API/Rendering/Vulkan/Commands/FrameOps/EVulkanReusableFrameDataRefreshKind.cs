namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Identifies the immutable producer payload consumed by reusable
/// command-buffer frame-data publication.
/// </summary>
internal enum EVulkanReusableFrameDataRefreshKind : byte
{
    Mesh,
    IndirectMesh,
    FrequencyOwnerMesh,
    Compute,
}
