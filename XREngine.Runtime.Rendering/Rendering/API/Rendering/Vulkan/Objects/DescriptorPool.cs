using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan
{
    public unsafe partial class VulkanRenderer
    {
        private DescriptorPool descriptorPool;

        private void DestroyDescriptorPool()
        {
            if (descriptorPool.Handle == 0)
                return;

            Api!.DestroyDescriptorPool(device, descriptorPool, null);
            descriptorPool = default;
            Engine.Rendering.Stats.RecordVulkanDescriptorPoolDestroy();
        }

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

                Engine.Rendering.Stats.RecordVulkanDescriptorPoolCreate();
            }
        }
    }
}