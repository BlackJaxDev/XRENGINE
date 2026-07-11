using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan
{
    public unsafe partial class VulkanRenderer
    {
        /// <summary>
        /// The Vulkan descriptor pool associated with this renderer.
        /// </summary>
        private DescriptorPool descriptorPool;

        /// <summary>
        /// Destroys the Vulkan descriptor pool associated with this renderer, if it exists.
        /// Ensures that the destruction process is properly tracked and the pool is no longer referenced.
        /// </summary>
        private void DestroyDescriptorPool()
        {
            if (descriptorPool.Handle == 0)
                return;

            RetireDescriptorPool(descriptorPool);
            descriptorPool = default;
        }

        /// <summary>
        /// Creates the Vulkan descriptor pool associated with this renderer, if it does not already exist.
        /// </summary>
        /// <exception cref="Exception">Thrown if the creation of the descriptor pool fails.</exception>
        private void CreateDescriptorPool()
        {
            var poolSizes = new DescriptorPoolSize[]
            {
                new()
                {
                    Type = DescriptorType.UniformBuffer,
                    DescriptorCount = (uint)swapChainImages!.Length,
                },
                new()
                {
                    Type = DescriptorType.CombinedImageSampler,
                    DescriptorCount = (uint)swapChainImages!.Length,
                }
            };

            fixed (DescriptorPoolSize* poolSizesPtr = poolSizes)
            fixed (DescriptorPool* descriptorPoolPtr = &descriptorPool)
            {

                DescriptorPoolCreateInfo poolInfo = new()
                {
                    SType = StructureType.DescriptorPoolCreateInfo,
                    PoolSizeCount = (uint)poolSizes.Length,
                    PPoolSizes = poolSizesPtr,
                    MaxSets = (uint)swapChainImages!.Length,
                };

                if (Api!.CreateDescriptorPool(device, ref poolInfo, null, descriptorPoolPtr) != Result.Success)
                    throw new Exception("Failed to create descriptor pool.");

                RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanDescriptorPoolCreate();
            }
        }
    }
}
