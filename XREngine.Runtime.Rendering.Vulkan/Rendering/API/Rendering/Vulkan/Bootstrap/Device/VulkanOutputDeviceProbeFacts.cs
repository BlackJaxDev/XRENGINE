namespace XREngine.Rendering.Vulkan.DeviceBootstrap;

/// <summary>
/// Per-device output observations captured after the target creates its
/// surface. No surface, output runtime, or native query delegate is retained.
/// </summary>
internal sealed class VulkanOutputDeviceProbeFacts
{
    public VulkanOutputDeviceProbeFacts(
        bool surfaceCreated,
        bool[] presentationSupportByQueueFamily,
        int swapchainFormatCount,
        int swapchainPresentModeCount)
    {
        ArgumentNullException.ThrowIfNull(presentationSupportByQueueFamily);
        SurfaceCreated = surfaceCreated;
        _presentationSupportByQueueFamily = presentationSupportByQueueFamily.ToArray();
        SwapchainFormatCount = swapchainFormatCount;
        SwapchainPresentModeCount = swapchainPresentModeCount;
    }

    public bool SurfaceCreated { get; }
    public int SwapchainFormatCount { get; }
    public int SwapchainPresentModeCount { get; }

    private readonly bool[] _presentationSupportByQueueFamily;

    public bool SupportsPresentation(uint queueFamilyIndex)
        => queueFamilyIndex < _presentationSupportByQueueFamily.Length &&
           _presentationSupportByQueueFamily[queueFamilyIndex];

    public static VulkanOutputDeviceProbeFacts Presentationless { get; } =
        new(false, [], 0, 0);
}
