using System.Collections.Frozen;

namespace XREngine.Rendering.Vulkan.DeviceBootstrap;

/// <summary>
/// Immutable creation policy for one Vulkan device lifetime. Output-specific
/// support is supplied separately through <see cref="VulkanPresentationSupportProbe"/>
/// so the device authority never owns a surface or a renderer facade.
/// </summary>
internal sealed class VulkanDeviceContextConfiguration
{
    public VulkanDeviceContextConfiguration(
        bool requirePresentQueue,
        bool requireSwapchainOutput,
        IEnumerable<string>? requiredDeviceExtensions = null,
        IEnumerable<string>? optionalDeviceExtensions = null)
    {
        RequirePresentQueue = requirePresentQueue;
        RequireSwapchainOutput = requireSwapchainOutput;
        RequiredDeviceExtensions = (requiredDeviceExtensions ?? []).ToFrozenSet(StringComparer.Ordinal);
        OptionalDeviceExtensions = (optionalDeviceExtensions ?? []).ToFrozenSet(StringComparer.Ordinal);
    }

    public static VulkanDeviceContextConfiguration Default { get; } = new(false, false);

    public bool RequirePresentQueue { get; }
    public bool RequireSwapchainOutput { get; }
    public FrozenSet<string> RequiredDeviceExtensions { get; }
    public FrozenSet<string> OptionalDeviceExtensions { get; }
}
