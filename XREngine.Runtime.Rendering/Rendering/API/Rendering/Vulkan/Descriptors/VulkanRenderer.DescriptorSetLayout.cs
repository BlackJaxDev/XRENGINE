using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan
{
    public unsafe partial class VulkanRenderer
    {
        private DescriptorSetLayout descriptorSetLayout;

        private void DestroyDescriptorSetLayout()
        {
            if (TryBeginDestroyDescriptorSetLayout(descriptorSetLayout, "Swapchain.DescriptorSetLayout"))
                Api!.DestroyDescriptorSetLayout(device, descriptorSetLayout, null);
            descriptorSetLayout = default;
        }

        private void CreateDescriptorSetLayout()
        {
            DescriptorSetLayoutBinding uboLayoutBinding = new()
            {
                Binding = 0,
                DescriptorCount = 1,
                DescriptorType = DescriptorType.UniformBuffer,
                PImmutableSamplers = null,
                StageFlags = ShaderStageFlags.VertexBit,
            };

            DescriptorSetLayoutCreateInfo layoutInfo = new()
            {
                SType = StructureType.DescriptorSetLayoutCreateInfo,
                BindingCount = 1,
                PBindings = &uboLayoutBinding,
            };

            fixed (DescriptorSetLayout* descriptorSetLayoutPtr = &descriptorSetLayout)
            {
                if (Api!.CreateDescriptorSetLayout(device, ref layoutInfo, null, descriptorSetLayoutPtr) != Result.Success)
                    throw new Exception("failed to create descriptor set layout!");
            }

            TrackLiveDescriptorSetLayout(descriptorSetLayout, "Swapchain.DescriptorSetLayout");
        }
    }
}
