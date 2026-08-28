namespace XREngine.Rendering.Vulkan;

internal sealed partial class VulkanResourceRuntime
{
    private VulkanAdvancedVisibilityResourceRuntime? _advancedVisibilityResources;

    /// <summary>
    /// Lazily owns the fixed set-1 visibility producer lane. Creation does not
    /// allocate Vulkan objects; initialization remains at the device boundary.
    /// </summary>
    internal VulkanAdvancedVisibilityResourceRuntime AdvancedVisibilityResources
        => _advancedVisibilityResources ??= new VulkanAdvancedVisibilityResourceRuntime(
            this,
            Lifetime.Retirement.Framebuffers.Length);
}
