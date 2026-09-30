namespace XREngine.Rendering.API.Rendering.OpenXR;

/// <summary>Native OpenXR Vulkan bootstrap operations installed by the XR backend.</summary>
public interface IOpenXrVulkanBootstrapServices
{
    OpenXrVulkanRuntimeRequirements GetRequestedVulkanRuntimeRequirements();
    bool TryGetRequestedVulkanPhysicalDevice(nint instanceHandle, out nint deviceHandle, out string? failureReason);
    bool TryCreateVulkanEnable2BootstrapContext(out IOpenXrVulkanBootstrapLease? lease, out string? failureReason);
}
