using Silk.NET.Vulkan;
using XREngine.Rendering.Materials;

namespace XREngine.Rendering.Vulkan;

internal sealed class VulkanCommandBufferLifetimeRecord
{
    public readonly Dictionary<VulkanResourceLifetimeKey, ulong> Dependencies = new(64);
    public VulkanResourceSlotHandle RetainedPresentationImageSlot;
    public readonly List<KeyValuePair<VulkanResourceLifetimeKey, ulong>> TouchedDependencies = new(64);
    public ulong RecordingGeneration;
    public int QueuedSubmissionCount;
    public VulkanFrameDataGenerationLease FrameDataLease;
    public readonly HashSet<VulkanMappedFrameAllocation> FrameDataAllocations = [];
    public long FrameDataRetirementVersion = -1;
    public bool FrameDataAllocationsOpen;
    public CommandBufferLevel Level;
    public VulkanResourceLifetimeKey AllocatingCommandPool;
    public ulong AllocatingCommandPoolGeneration;
    public VulkanStableCommandSlotHandle StableCommandIdentity;
    public SealedSubmissionContract? SealedSubmissionContract;
    // Only a successful, completion-validated native reset can release these
    // arrays to scratch. Generic gateway invalidation does not end old readers.
    public SealedSubmissionContract? ReusableSealedSubmissionContract;
    public VulkanSubmissionPinReceipt SubmissionPinReceipt { get; } = new();
    public readonly List<GPUMaterialTableDescriptorClosure> MaterialDescriptorClosures = new(2);

    public void ReleaseMaterialDescriptorClosures()
    {
        foreach (GPUMaterialTableDescriptorClosure closure in MaterialDescriptorClosures)
            closure.Dispose();
        MaterialDescriptorClosures.Clear();
    }

    /// <summary>Releases range readers only after native command reset or destruction.</summary>
    public void ReleaseFrameDataAllocations(ulong commandBufferHandle)
    {
        foreach (VulkanMappedFrameAllocation allocation in FrameDataAllocations)
            allocation.ReleaseCommand(commandBufferHandle);
        FrameDataAllocations.Clear();
        FrameDataRetirementVersion = -1;
    }

    public void RefreshTouchedDependencies()
    {
        TouchedDependencies.Clear();
        TouchedDependencies.EnsureCapacity(Dependencies.Count);
        foreach (KeyValuePair<VulkanResourceLifetimeKey, ulong> dependency in Dependencies)
            TouchedDependencies.Add(dependency);
    }

    public void InvalidateSealedSubmissionContract()
    {
        SealedSubmissionContract = null;
    }
}
