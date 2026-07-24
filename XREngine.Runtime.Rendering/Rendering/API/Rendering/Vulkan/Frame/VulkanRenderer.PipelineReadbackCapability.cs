namespace XREngine.Rendering.Vulkan;

public partial class VulkanRenderer :
    IRenderPipelineReadbackBackendCapability,
    IOpenXrDeviceOwnershipBackendCapability
{
    bool IOpenXrDeviceOwnershipBackendCapability.UsesOpenXrManagedDeviceCreation
        => UsesOpenXrVulkanEnable2Creation;
}
