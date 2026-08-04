namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Immutable identity for the mesh portion of one prepared reusable
/// frame-data refresh stream. Frame-frequency generations are intentionally
/// excluded so an unchanged cohort can publish only its compact owner work.
/// </summary>
internal readonly record struct VulkanReusableFrameDataRefreshBatchInfo(
    ulong StableMeshSignature,
    int MeshRequestCount,
    bool SupportsDirectOwnerOnlyRefresh);
