using Silk.NET.Vulkan;
namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    private DescriptorSet[]? descriptorSets;

    /// <summary>
    /// Updates the Vulkan descriptor sets with the specified descriptor writes, while tracking the updates for validation and debugging purposes.
    /// </summary>
    /// <param name="descriptorWriteCount">The number of descriptor writes to apply.</param>
    /// <param name="descriptorWrites">A pointer to an array of Vulkan write descriptor set structures.</param>
    /// <exception cref="InvalidOperationException">Thrown if the Vulkan device is not in an operational state.</exception>
    internal void UpdateDescriptorSetsTracked(uint descriptorWriteCount, WriteDescriptorSet* descriptorWrites)
    {
        if (!IsDeviceOperational)
            throw new InvalidOperationException($"Cannot update Vulkan descriptors while device state is {DeviceState}.");

        lock (_vulkanResourceLifetimeLock)
        {
            ValidateAndRecordVulkanDescriptorWrites(descriptorWriteCount, descriptorWrites);
            Api!.UpdateDescriptorSets(device, descriptorWriteCount, descriptorWrites, 0, null);
        }
    }

    /// <summary>
    /// Attempts a tracked descriptor update that may race a render-resource generation
    /// retirement. Callers that can rebuild their descriptor inputs on the next frame use
    /// this path so a retired image view defers that draw instead of aborting the frame and
    /// leaving the previously published descriptor snapshot permanently stale.
    /// </summary>
    internal bool TryUpdateDescriptorSetsTracked(
        uint descriptorWriteCount,
        WriteDescriptorSet* descriptorWrites,
        out string failureReason)
    {
        if (!IsDeviceOperational)
        {
            failureReason = $"Cannot update Vulkan descriptors while device state is {DeviceState}.";
            return false;
        }

        lock (_vulkanResourceLifetimeLock)
        {
            if (!TryPrevalidateVulkanDescriptorWrites_NoLock(
                    descriptorWriteCount,
                    descriptorWrites,
                    out failureReason))
            {
                return false;
            }

            // Keep retirement excluded until Vulkan has copied the descriptor payload. Otherwise a
            // generation can retire after validation but before vkUpdateDescriptorSets, leaving the
            // lifetime ledger and native descriptor contents describing different resources.
            ValidateAndRecordVulkanDescriptorWrites(descriptorWriteCount, descriptorWrites);
            Api!.UpdateDescriptorSets(device, descriptorWriteCount, descriptorWrites, 0, null);
            failureReason = string.Empty;
            return true;
        }
    }

    /// <summary>
    /// Creates and allocates Vulkan descriptor sets for the swapchain images, and registers them for tracking and debugging purposes.
    /// </summary>
    /// <exception cref="Exception">Thrown if the allocation of Vulkan descriptor sets fails.</exception>
    private void CreateDescriptorSets()
    {
        // Prepare an array of descriptor set layouts for allocation. 
        // Each swapchain image will have its own descriptor set.
        var layouts = new DescriptorSetLayout[swapChainImages!.Length];
        Array.Fill(layouts, descriptorSetLayout);

        // Allocate the descriptor sets using the prepared layouts.
        fixed (DescriptorSetLayout* layoutsPtr = layouts)
        {
            // Set up the allocation info structure for the descriptor sets.
            DescriptorSetAllocateInfo allocateInfo = new()
            {
                SType = StructureType.DescriptorSetAllocateInfo,
                DescriptorPool = descriptorPool,
                DescriptorSetCount = (uint)swapChainImages!.Length,
                PSetLayouts = layoutsPtr,
            };

            // Allocate the descriptor sets and handle any allocation failures.
            descriptorSets = new DescriptorSet[swapChainImages.Length];
            fixed (DescriptorSet* descriptorSetsPtr = descriptorSets)
            {
                if (Api!.AllocateDescriptorSets(device, ref allocateInfo, descriptorSetsPtr) != Result.Success)
                    throw new Exception("Failed to allocate descriptor sets.");
            }
        }

        // Register the allocated descriptor sets for tracking and debugging purposes.
        RegisterVulkanDescriptorSets(descriptorPool, descriptorSets, usesUpdateAfterBind: false, "Swapchain.DescriptorSet");
        
        // Set debug names for the allocated descriptor sets.
        SetDebugDescriptorSetNames(descriptorSets, "Swapchain.DescriptorSet");

        // Record the generation of the Vulkan descriptor table for the swapchain descriptor sets.
        RecordVulkanDescriptorTableGeneration("SwapchainDescriptorSets.Allocated");

        // Update each descriptor set with the appropriate buffer and image information.
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
