using XREngine.Rendering.API.Rendering.OpenXR;

namespace XREngine;

/// <summary>Installs the native OpenXR runtime factory at application composition.</summary>
public static class OpenXrRuntimeBackend
{
    public static void Register()
    {
        OpenXrRuntimeServices.Register(static () => new OpenXRAPI());
        OpenXrRuntimeSettings.Register(new RuntimeConfiguration());
        OpenXrVulkanBootstrapServices.Register(new VulkanBootstrapServices());
    }

    private sealed class RuntimeConfiguration : IOpenXrRuntimeConfiguration
    {
        public bool CanChangeRuntimeConfiguration => OpenXRAPI.CanChangeRuntimeConfiguration;
        public string? RuntimeConfigurationChangeFailureReason => OpenXRAPI.RuntimeConfigurationChangeFailureReason;
        public void ClearVulkanRuntimeRequirementsCache() => OpenXRAPI.ClearVulkanRuntimeRequirementsCache();
    }

    private sealed class VulkanBootstrapServices : IOpenXrVulkanBootstrapServices
    {
        public OpenXrVulkanRuntimeRequirements GetRequestedVulkanRuntimeRequirements()
            => OpenXRAPI.GetRequestedVulkanRuntimeRequirements();

        public bool TryGetRequestedVulkanPhysicalDevice(
            nint instanceHandle, out nint deviceHandle, out string? failureReason)
            => OpenXRAPI.TryGetRequestedVulkanPhysicalDevice(instanceHandle, out deviceHandle, out failureReason);

        public bool TryCreateVulkanEnable2BootstrapContext(
            out IOpenXrVulkanBootstrapLease? lease, out string? failureReason)
        {
            bool created = OpenXRAPI.TryCreateVulkanEnable2BootstrapContext(
                out OpenXrVulkanEnable2BootstrapContext? context, out failureReason);
            lease = context;
            return created;
        }
    }
}
