namespace XREngine.Rendering.Vulkan;

public partial class VulkanRenderer : IRenderResourceRetirementBackendCapability
{
    /// <inheritdoc />
    public void PrepareForPhysicalResourceDestruction(string reason)
    {
        if (IsDeviceLost)
        {
            Debug.VulkanWarningEvery(
                $"Vulkan.RenderPipeline.ResourceDestroy.DeviceLost.{reason}",
                TimeSpan.FromSeconds(1),
                "[Vulkan] Skipping descriptor-reference release because the device is lost: {0}",
                reason);
            return;
        }

        ReleaseDescriptorReferencesForPhysicalResourceDestruction(reason);
        Debug.VulkanEvery(
            $"Vulkan.RenderPipeline.ResourceDestroy.Deferred.{reason}",
            TimeSpan.FromSeconds(1),
            "[Vulkan] Prepared render-pipeline physical resources for completion-aware retirement without a device idle: {0}",
            reason);
    }
}
