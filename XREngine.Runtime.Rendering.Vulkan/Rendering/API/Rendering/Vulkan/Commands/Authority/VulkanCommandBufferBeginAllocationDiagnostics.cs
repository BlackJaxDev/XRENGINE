namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Thread-local attribution for a single explicitly requested command-buffer
/// begin. This is enabled only by the presentationless benchmark diagnostic.
/// </summary>
internal static class VulkanCommandBufferBeginAllocationDiagnostics
{
    [ThreadStatic]
    internal static bool Enabled;

    [ThreadStatic]
    internal static VulkanCommandBufferBeginAllocationCounters Last;
}

internal readonly record struct VulkanCommandBufferBeginAllocationCounters(
    long BindStateInitialization,
    long TrackingInitialization,
    long NativeBegin);
