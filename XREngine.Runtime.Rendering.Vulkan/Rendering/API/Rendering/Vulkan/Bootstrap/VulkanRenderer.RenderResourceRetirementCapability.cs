namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Exposes completion-aware Vulkan resource retirement to stable pipeline code.
/// </summary>
public sealed partial class VulkanRenderer : IRenderResourceRetirementBackendCapability
{
    /// <summary>
    /// Invalidates Vulkan descriptor and command references so native resource
    /// destruction can enter the existing completion-tracked retirement path
    /// without an unrelated device-wide idle wait.
    /// </summary>
    void IRenderResourceRetirementBackendCapability.PrepareForPhysicalResourceDestruction(
        string reason)
        => VulkanPrepareForPhysicalResourceDestruction(reason);

    private void VulkanPrepareForPhysicalResourceDestruction(string reason)
        => _frameLoop.ReleaseDescriptorReferencesForPhysicalResourceDestruction(reason);
}
