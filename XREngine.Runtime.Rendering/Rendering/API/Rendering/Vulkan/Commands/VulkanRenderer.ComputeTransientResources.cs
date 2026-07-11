using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan
{
    public unsafe partial class VulkanRenderer
    {
        private sealed class ComputeTransientResources
        {
            public object SyncRoot { get; } = new();
            public DescriptorPool ActiveDescriptorPool;
            public ulong ActiveDescriptorPoolSignature;
            public List<DescriptorPool> DescriptorPools { get; } = [];
            public List<ulong> DescriptorPoolSignatures { get; } = [];
            public List<(Silk.NET.Vulkan.Buffer buffer, DeviceMemory memory)> UniformBuffers { get; } = [];
            public bool DescriptorPoolsInitialized;
        }

    }
}
