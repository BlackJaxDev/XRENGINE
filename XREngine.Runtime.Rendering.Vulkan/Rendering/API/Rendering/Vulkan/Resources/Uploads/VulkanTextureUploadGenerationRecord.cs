namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Bounded durable truth for scheduled Vulkan upload generations of one
/// streaming texture. Queue membership is deliberately absent: queues may
/// remove completed or failed work without erasing readiness or failure truth.
/// </summary>
internal sealed class VulkanTextureUploadGenerationRecord
{
    internal const int Capacity = 64;
    internal readonly object Sync = new();
    internal readonly List<VulkanTextureUploadGenerationEntry> Entries =
        new(Capacity);
    internal long LatestPublishedStreamingGeneration;
}
