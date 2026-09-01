namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Frozen authority inputs for one imported-texture upload scheduling sequence.
/// The upload service retains this concrete request rather than a renderer facade.
/// </summary>
internal readonly record struct VulkanTextureUploadSchedulingContext(
    VulkanRenderer Owner,
    long BackendGeneration,
    VulkanBackendObjectContext BackendObjects,
    VulkanResourceRuntime Resources,
    VulkanCommandRuntime Commands)
{
    /// <summary>
    /// True while this request still belongs to the live renderer generation
    /// that created its Vulkan resource graph.
    /// </summary>
    internal bool IsDeviceOperational
        => Owner.BackendGeneration == BackendGeneration &&
           Owner.AcceptsBackendWork &&
           !Owner.IsDeviceLost &&
           BackendObjects.IsDeviceOperational;

    /// <summary>
    /// Wrapper creation is render-thread-affine and may run only while the
    /// request's exact renderer is the active thread owner. Generic render-job
    /// pumps can execute outside that scope and must defer the work.
    /// </summary>
    internal bool IsOwnerCurrent
        => IsDeviceOperational &&
           Owner.Active &&
           ReferenceEquals(AbstractRenderer.Current, Owner);
}
