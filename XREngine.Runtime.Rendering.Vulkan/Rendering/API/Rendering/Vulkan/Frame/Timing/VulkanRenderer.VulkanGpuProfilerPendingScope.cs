namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    private readonly struct VulkanGpuProfilerPendingScope(string[] path, uint startQuery, uint endQuery)
    {
        public string[] Path { get; } = path;
        public uint StartQuery { get; } = startQuery;
        public uint EndQuery { get; } = endQuery;
    }
}
