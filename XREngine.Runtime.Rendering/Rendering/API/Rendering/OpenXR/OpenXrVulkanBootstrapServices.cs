using XREngine.Rendering;

namespace XREngine.Rendering.API.Rendering.OpenXR;

/// <summary>Composition point for the native OpenXR Vulkan bootstrap owner.</summary>
public static class OpenXrVulkanBootstrapServices
{
    private static IOpenXrVulkanBootstrapServices? _services;

    private static IOpenXrVulkanBootstrapServices? InstalledServices
    {
        get
        {
            IOpenXrVulkanBootstrapServices? services = Volatile.Read(ref _services);
            if (services is null && RuntimeRenderingHostServices.Presentation.IsOpenXrRuntimeRequested)
                throw new InvalidOperationException("OpenXR Vulkan bootstrap backend has not been registered for the requested runtime.");
            return services;
        }
    }

    public static void Register(IOpenXrVulkanBootstrapServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        Volatile.Write(ref _services, services);
    }

    public static OpenXrVulkanRuntimeRequirements GetRequestedVulkanRuntimeRequirements()
        => InstalledServices?.GetRequestedVulkanRuntimeRequirements()
            ?? OpenXrVulkanRuntimeRequirements.Empty;

    public static bool TryGetRequestedVulkanPhysicalDevice(
        nint instanceHandle, out nint deviceHandle, out string? failureReason)
    {
        IOpenXrVulkanBootstrapServices? services = InstalledServices;
        if (services is not null)
            return services.TryGetRequestedVulkanPhysicalDevice(instanceHandle, out deviceHandle, out failureReason);
        deviceHandle = 0;
        failureReason = null;
        return false;
    }

    public static bool TryCreateVulkanEnable2BootstrapContext(
        out IOpenXrVulkanBootstrapLease? lease, out string? failureReason)
    {
        IOpenXrVulkanBootstrapServices? services = InstalledServices;
        if (services is not null)
            return services.TryCreateVulkanEnable2BootstrapContext(out lease, out failureReason);
        lease = null;
        failureReason = null;
        return false;
    }
}
