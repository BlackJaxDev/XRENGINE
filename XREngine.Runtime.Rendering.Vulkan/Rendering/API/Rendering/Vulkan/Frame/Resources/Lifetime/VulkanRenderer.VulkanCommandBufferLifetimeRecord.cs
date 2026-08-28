using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

internal sealed class VulkanCommandBufferLifetimeRecord
{
    public readonly Dictionary<VulkanResourceLifetimeKey, ulong> Dependencies = new(64);
    public readonly List<KeyValuePair<VulkanResourceLifetimeKey, ulong>> TouchedDependencies = new(64);
    public ulong RecordingGeneration;
    public int QueuedSubmissionCount;
    public VulkanFrameDataGenerationLease FrameDataLease;
    public CommandBufferLevel Level;
    public VulkanResourceLifetimeKey AllocatingCommandPool;
    public ulong AllocatingCommandPoolGeneration;
    public SealedSubmissionContract? SealedSubmissionContract;
    public VulkanSubmissionPinReceipt SubmissionPinReceipt { get; } = new();

    public void RefreshTouchedDependencies()
    {
        TouchedDependencies.Clear();
        TouchedDependencies.EnsureCapacity(Dependencies.Count);
        foreach (KeyValuePair<VulkanResourceLifetimeKey, ulong> dependency in Dependencies)
            TouchedDependencies.Add(dependency);
    }

    public void InvalidateSealedSubmissionContract()
        => SealedSubmissionContract = null;
}
