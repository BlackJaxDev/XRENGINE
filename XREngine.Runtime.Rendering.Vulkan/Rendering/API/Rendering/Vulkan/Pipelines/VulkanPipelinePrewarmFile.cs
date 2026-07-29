namespace XREngine.Rendering.Vulkan;

internal sealed class VulkanPipelinePrewarmFile
{
    public int Version { get; set; }
    public string DeviceProfile { get; set; } = string.Empty;
    public DateTime GeneratedUtc { get; set; }
    public VulkanPipelinePrewarmEntry[]? Entries { get; set; }
}
