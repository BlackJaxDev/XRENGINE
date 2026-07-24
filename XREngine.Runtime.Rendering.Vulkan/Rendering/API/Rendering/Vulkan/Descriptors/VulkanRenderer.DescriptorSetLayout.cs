using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan
{
    public unsafe partial class VulkanRenderer
    {
        /// <summary>
        /// The Vulkan descriptor set layout associated with this renderer.
        /// </summary>
        private DescriptorSetLayout descriptorSetLayout;

        /// <summary>
        /// Destroys the Vulkan descriptor set layout associated with this renderer, if it exists.
        /// Ensures that the destruction process is properly tracked and the layout is no longer referenced.
        /// </summary>
        private void DestroyDescriptorSetLayout()
        {
            if (TryBeginDestroyDescriptorSetLayout(descriptorSetLayout, "Swapchain.DescriptorSetLayout"))
                Api!.DestroyDescriptorSetLayout(device, descriptorSetLayout, null);
            descriptorSetLayout = default;
        }

        /// <summary>
        /// Creates the Vulkan descriptor set layout associated with this renderer, if it does not already exist.
        /// </summary>
        /// <exception cref="Exception">Thrown if the creation of the descriptor set layout fails.</exception>
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
