namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    internal sealed class VulkanCommandBufferLifetimeRecord
    {
        public readonly Dictionary<VulkanResourceLifetimeKey, ulong> Dependencies = new(64);
        public readonly List<KeyValuePair<VulkanResourceLifetimeKey, ulong>> TouchedDependencies = new(64);
        public ulong RecordingGeneration;
        public int QueuedSubmissionCount;
        public VulkanFrameDataGenerationLease FrameDataLease;

        public void RefreshTouchedDependencies()
        {
            TouchedDependencies.Clear();
            TouchedDependencies.EnsureCapacity(Dependencies.Count);
            foreach (KeyValuePair<VulkanResourceLifetimeKey, ulong> dependency in Dependencies)
                TouchedDependencies.Add(dependency);
        }
    }
}
