namespace XREngine.Rendering.API.Rendering.OpenXR;

/// <summary>Static OpenXR loader and Vulkan requirement state owned by the native backend.</summary>
public interface IOpenXrRuntimeConfiguration
{
    bool CanChangeRuntimeConfiguration { get; }
    string? RuntimeConfigurationChangeFailureReason { get; }
    void ClearVulkanRuntimeRequirementsCache();
}
