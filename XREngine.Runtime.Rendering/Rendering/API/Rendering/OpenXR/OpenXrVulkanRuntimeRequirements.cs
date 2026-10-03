namespace XREngine.Rendering.API.Rendering.OpenXR;

/// <summary>Vulkan extensions and API version bounds requested by the selected OpenXR runtime.</summary>
public sealed record OpenXrVulkanRuntimeRequirements(
    string[] InstanceExtensions,
    string[] DeviceExtensions,
    ulong MinApiVersionSupported,
    ulong MaxApiVersionSupported,
    string? FailureReason)
{
    public static OpenXrVulkanRuntimeRequirements Empty { get; } = new([], [], 0, 0, null);
}
