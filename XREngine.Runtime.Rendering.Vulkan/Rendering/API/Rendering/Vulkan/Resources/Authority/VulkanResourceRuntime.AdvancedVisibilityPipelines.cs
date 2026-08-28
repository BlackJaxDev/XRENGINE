namespace XREngine.Rendering.Vulkan;

internal sealed partial class VulkanResourceRuntime
{
    private VulkanAdvancedVisibilityPipelineRuntime? _advancedVisibilityPipelines;

    /// <summary>Generation-owned executable compute lane for advanced visibility.</summary>
    internal VulkanAdvancedVisibilityPipelineRuntime AdvancedVisibilityPipelines
        => _advancedVisibilityPipelines ??=
            new VulkanAdvancedVisibilityPipelineRuntime(this);
}
