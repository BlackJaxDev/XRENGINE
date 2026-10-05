namespace XREngine.Rendering.Vulkan;

/// <summary>Thread-local diagnostic recording context installed only around a selected production capture.</summary>
internal static class VulkanSelectedGpuPassContext
{
    [ThreadStatic]
    private static IVulkanSelectedGpuPassSink? _current;

    public static IVulkanSelectedGpuPassSink? Current
    {
        get => _current;
        set => _current = value;
    }
}
