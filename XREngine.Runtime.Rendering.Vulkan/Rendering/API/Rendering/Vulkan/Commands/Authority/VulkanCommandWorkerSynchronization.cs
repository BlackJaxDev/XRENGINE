namespace XREngine.Rendering.Vulkan;

/// <summary>Bounded render-domain command-recording state.</summary>
internal sealed class VulkanCommandWorkerSynchronization
{
    internal int ActiveWorkerCount;
    internal int Faulted;
    internal VulkanCommandChainRecordingBatch Batch { get; set; } = new();
}
