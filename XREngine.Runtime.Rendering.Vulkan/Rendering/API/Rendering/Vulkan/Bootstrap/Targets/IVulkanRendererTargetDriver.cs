namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Supplies the Vulkan bootstrap requirements and target-owned lifecycle hooks
/// for one renderer execution mode.
/// </summary>
internal interface IVulkanRendererTargetDriver
{
    RenderExecutionMode ExecutionMode { get; }
    bool RequiresPresentQueue { get; }
    bool RequiresSwapchainOutput { get; }
    bool SupportsStreamlinePresentation { get; }
    IReadOnlyList<string> RequiredDeviceExtensions { get; }
    string[] GetRequiredInstanceExtensions();
    void CreateInstanceResources(VulkanTargetSurfaceAuthority surfaces);
    void InitializeFinalOutput(VulkanTargetOutputContext output);
    void DestroyFinalOutput(VulkanTargetOutputContext output);
    void DestroyInstanceResources(VulkanTargetSurfaceAuthority surfaces);
}
