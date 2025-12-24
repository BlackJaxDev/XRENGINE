using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    private DescriptorSet[]? descriptorSets;

    private void CreateDescriptorSets()
    {
        var layouts = new DescriptorSetLayout[swapChainImages!.Length];
        Array.Fill(layouts, descriptorSetLayout);

        fixed (DescriptorSetLayout* layoutsPtr = layouts)
        {
            DescriptorSetAllocateInfo allocateInfo = new()
            {
                SType = StructureType.DescriptorSetAllocateInfo,
                DescriptorPool = descriptorPool,
                DescriptorSetCount = (uint)swapChainImages!.Length,
                PSetLayouts = layoutsPtr,
            };

            descriptorSets = new DescriptorSet[swapChainImages.Length];
            fixed (DescriptorSet* descriptorSetsPtr = descriptorSets)
            {
                if (Api!.AllocateDescriptorSets(device, ref allocateInfo, descriptorSetsPtr) != Result.Success)
                    throw new Exception("Failed to allocate descriptor sets.");
            }
        }

        for (int i = 0; i < swapChainImages.Length; i++)
        {
            //DescriptorBufferInfo ubo = new()
            //{
            //    Buffer = uniformBuffers![i].Buffer,
            //    Offset = 0,
            //    Range = (ulong)Unsafe.SizeOf<UniformBufferObject>(),
            //};

            //WriteDescriptorSet[] descriptorWrites;
            //if (_testModel?.Textures != null && _testModel.Textures[0] != null)
            //{
            //    DescriptorImageInfo imageInfo = _testModel.Textures[0].CreateImageInfo();
            //    descriptorWrites =
            //    [
            //        new()
            //        {
            //            //Uniforms
            //            SType = StructureType.WriteDescriptorSet,
            //            DstSet = descriptorSets[i],
            //            DstBinding = 0,
            //            DstArrayElement = 0,
            //            DescriptorType = DescriptorType.UniformBuffer,
            //            DescriptorCount = 1,
            //            PBufferInfo = &ubo,
            //        },
            //        new()
            //        {
            //            //Textures
            //            SType = StructureType.WriteDescriptorSet,
            //            DstSet = descriptorSets[i],
            //            DstBinding = 1,
            //            DstArrayElement = 0,
            //            DescriptorType = DescriptorType.CombinedImageSampler,
            //            DescriptorCount = 1,
            //            PImageInfo = &imageInfo,
            //        }
            //    ];
            //}
            //else
            //{
            //    descriptorWrites =
            //    [
            //        new()
            //        {
            //            SType = StructureType.WriteDescriptorSet,
            //            DstSet = descriptorSets[i],
            //            DstBinding = 0,
            //            DstArrayElement = 0,
            //            DescriptorType = DescriptorType.UniformBuffer,
            //            DescriptorCount = 1,
            //            PBufferInfo = &ubo,
            //        },
            //    ];
            //}

            //fixed (WriteDescriptorSet* descriptorWritesPtr = descriptorWrites)
            //{
            //    Api!.UpdateDescriptorSets(device, (uint)descriptorWrites.Length, descriptorWritesPtr, 0, null);
            //}
        }
    }
}