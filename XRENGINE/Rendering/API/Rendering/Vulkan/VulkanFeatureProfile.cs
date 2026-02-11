namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Centralised feature-gate queries for the Vulkan CPU-octree render path.
/// Pipelines and render commands should consult this profile to decide whether
/// compute-dependent passes, GPU dispatch, and other Vulkan-sensitive features
/// are safe to enable at the current point in the backend bring-up.
/// </summary>
public static class VulkanFeatureProfile
{
    /// <summary>
    /// Returns <c>true</c> when the Vulkan renderer is the currently active backend
    /// and the safe feature profile should be used to restrict unsupported features.
    /// </summary>
    public static bool IsActive
        => Engine.Rendering.IsVulkanRendererActive();

    /// <summary>
    /// Compute-dependent render passes (Forward+ light culling, ReSTIR GI, Surfel GI,
    /// radiance cascades, voxel cone tracing, spatial-hash AO, etc.) require fully wired
    /// compute dispatch + descriptor set support.  Returns <c>false</c> when the Vulkan
    /// backend is active and these systems are not yet verified.
    /// </summary>
    public static bool EnableComputeDependentPasses
        => !IsActive;

    /// <summary>
    /// GPU-driven render dispatch (GPU culling, indirect draws) requires compute + buffer
    /// infrastructure that is still being hardened on Vulkan.  Returns <c>false</c> when
    /// the Vulkan backend is active.
    /// </summary>
    public static bool EnableGpuRenderDispatch
        => !IsActive;

    /// <summary>
    /// GPU BVH raycast dispatch.  Returns <c>false</c> when the Vulkan backend is active.
    /// </summary>
    public static bool EnableGpuBvh
        => !IsActive;

    /// <summary>
    /// ImGui rendering through the Vulkan pipeline.  Currently disabled on Vulkan
    /// (<c>SupportsImGui == false</c>).
    /// </summary>
    public static bool EnableImGui
        => !IsActive;
}
