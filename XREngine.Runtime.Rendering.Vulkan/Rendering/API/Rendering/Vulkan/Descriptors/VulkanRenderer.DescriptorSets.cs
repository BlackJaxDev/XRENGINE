using System.Buffers;
using Silk.NET.Vulkan;
namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    private readonly VulkanDescriptorManager _descriptorManager = new();
    private DescriptorSet[]? descriptorSets;

    private long SnapshotDescriptorSetContentUpdateGeneration()
        => _descriptorManager.SnapshotDescriptorSetContentUpdateGeneration();

    private bool HaveDescriptorSetContentsUpdatedSince(long generation)
        => _descriptorManager.HaveDescriptorSetContentsUpdatedSince(generation);

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

        ulong[]? dependentCommandBuffers;
        int dependentCommandBufferCount;
        bool invalidatesRecordedCommandBuffers;
        VulkanDescriptorUpdateInvalidation firstInvalidation;
        lock (_resourceLifetimeTracker.SyncRoot)
        {
            ValidateAndRecordVulkanDescriptorWrites(descriptorWriteCount, descriptorWrites);
            Api!.UpdateDescriptorSets(device, descriptorWriteCount, descriptorWrites, 0, null);
            invalidatesRecordedCommandBuffers = TryCaptureDescriptorUpdateInvalidations_NoLock(
                descriptorWriteCount,
                descriptorWrites,
                out dependentCommandBuffers,
                out dependentCommandBufferCount,
                out firstInvalidation);
        }

        PublishDescriptorSetContentUpdate(
            invalidatesRecordedCommandBuffers,
            dependentCommandBuffers,
            dependentCommandBufferCount,
            firstInvalidation,
            "vkUpdateDescriptorSets");
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

        ulong[]? dependentCommandBuffers;
        int dependentCommandBufferCount;
        bool invalidatesRecordedCommandBuffers;
        VulkanDescriptorUpdateInvalidation firstInvalidation;
        lock (_resourceLifetimeTracker.SyncRoot)
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
            invalidatesRecordedCommandBuffers = TryCaptureDescriptorUpdateInvalidations_NoLock(
                descriptorWriteCount,
                descriptorWrites,
                out dependentCommandBuffers,
                out dependentCommandBufferCount,
                out firstInvalidation);
        }

        PublishDescriptorSetContentUpdate(
            invalidatesRecordedCommandBuffers,
            dependentCommandBuffers,
            dependentCommandBufferCount,
            firstInvalidation,
            "vkUpdateDescriptorSets");
        failureReason = string.Empty;
        return true;
    }

    /// <summary>
    /// Captures the cached command buffers invalidated by ordinary descriptor writes.
    /// The caller must hold <see cref="_resourceLifetimeTracker.SyncRoot"/>.
    /// </summary>
    private bool TryCaptureDescriptorUpdateInvalidations_NoLock(
        uint descriptorWriteCount,
        WriteDescriptorSet* descriptorWrites,
        out ulong[]? dependentCommandBuffers,
        out int dependentCommandBufferCount,
        out VulkanDescriptorUpdateInvalidation firstInvalidation)
    {
        dependentCommandBuffers = null;
        dependentCommandBufferCount = 0;
        firstInvalidation = default;
        if (descriptorWriteCount == 0 || descriptorWrites is null)
            return false;

        bool invalidatesRecordedCommandBuffers = false;
        int dependentCapacity = 0;
        for (uint writeIndex = 0; writeIndex < descriptorWriteCount; writeIndex++)
        {
            WriteDescriptorSet write = descriptorWrites[writeIndex];
            if (!DescriptorWriteInvalidatesRecordedCommands_NoLock(write))
                continue;

            invalidatesRecordedCommandBuffers = true;
            VulkanResourceLifetimeKey setKey = ResourceKey(ObjectType.DescriptorSet, write.DstSet.Handle);
            if (_resourceLifetimeTracker.ResourceCommandBufferDependencies.TryGetValue(setKey, out HashSet<ulong>? dependents))
            {
                dependentCapacity = checked(dependentCapacity + dependents.Count);
                if (firstInvalidation.DescriptorSetHandle == 0 && dependents.Count > 0)
                {
                    firstInvalidation = new VulkanDescriptorUpdateInvalidation(
                        write.DstSet.Handle,
                        write.DstBinding,
                        write.DstArrayElement,
                        write.DescriptorType,
                        write.DescriptorCount);
                }
            }
        }

        if (!invalidatesRecordedCommandBuffers || dependentCapacity == 0)
            return invalidatesRecordedCommandBuffers;

        dependentCommandBuffers = ArrayPool<ulong>.Shared.Rent(dependentCapacity);
        for (uint writeIndex = 0; writeIndex < descriptorWriteCount; writeIndex++)
        {
            WriteDescriptorSet write = descriptorWrites[writeIndex];
            if (!DescriptorWriteInvalidatesRecordedCommands_NoLock(write))
                continue;

            VulkanResourceLifetimeKey setKey = ResourceKey(ObjectType.DescriptorSet, write.DstSet.Handle);
            if (!_resourceLifetimeTracker.ResourceLifetimes.TryGetValue(setKey, out VulkanResourceLifetimeRecord? setResource) ||
                !_resourceLifetimeTracker.ResourceCommandBufferDependencies.TryGetValue(setKey, out HashSet<ulong>? dependents))
            {
                continue;
            }

            foreach (ulong commandBufferHandle in dependents)
            {
                if (!_resourceLifetimeTracker.CommandBufferLifetimes.TryGetValue(
                        commandBufferHandle,
                        out VulkanCommandBufferLifetimeRecord? commandBufferLifetime) ||
                    !commandBufferLifetime.Dependencies.TryGetValue(
                        setKey,
                        out ulong recordedGeneration) ||
                    recordedGeneration != setResource.Generation ||
                    ContainsCommandBufferHandle(
                        dependentCommandBuffers.AsSpan(0, dependentCommandBufferCount),
                        commandBufferHandle))
                {
                    continue;
                }

                dependentCommandBuffers[dependentCommandBufferCount++] = commandBufferHandle;
            }
        }

        return true;
    }

    private bool DescriptorWriteInvalidatesRecordedCommands_NoLock(in WriteDescriptorSet write)
    {
        if (write.DstSet.Handle == 0 || write.DescriptorCount == 0)
            return false;

        // UPDATE_AFTER_BIND preserves the native set handle, but an image/view/layout
        // payload change would otherwise leave cached secondary descriptor-image
        // requirements describing the old payload. Invalidate only the command
        // buffers that actually bind this set; buffer-only UAB writes remain safe.
        if (IsLifetimeTrackedImageDescriptorType(write.DescriptorType))
            return true;

        return !_resourceLifetimeTracker.DescriptorSetLifetimes.TryGetValue(
                write.DstSet.Handle,
                out VulkanDescriptorSetLifetimeRecord? setState) ||
            !setState.UsesUpdateAfterBind ||
            !CanUseUpdateAfterBind(write.DescriptorType);
    }

    private void PublishDescriptorSetContentUpdate(
        bool invalidatesRecordedCommandBuffers,
        ulong[]? dependentCommandBuffers,
        int dependentCommandBufferCount,
        in VulkanDescriptorUpdateInvalidation firstInvalidation,
        string updateKind)
    {
        try
        {
            if (!invalidatesRecordedCommandBuffers)
                return;

            _descriptorManager.RecordDescriptorSetContentUpdate();
            if (dependentCommandBufferCount == 0 || dependentCommandBuffers is null)
                return;

            if (CommandRecordingDiagnosticsEnabled &&
                _descriptorManager.RecordDescriptorUpdateInvalidationDiagnostic() <= 128)
            {
                Debug.WriteAuxiliaryLog(
                    "vulkan-descriptor-invalidations.log",
                    $"frame={VulkanFrameCounter} update={updateKind} set=0x{firstInvalidation.DescriptorSetHandle:X} binding={firstInvalidation.Binding} array={firstInvalidation.ArrayElement} type={firstInvalidation.DescriptorType} count={firstInvalidation.DescriptorCount} dependentCommandBuffers={dependentCommandBufferCount}");
            }

            VulkanExactInvalidationResult result = InvalidateCachedCommandBuffersByHandle(
                dependentCommandBuffers.AsSpan(0, dependentCommandBufferCount),
                $"{updateKind} changed a descriptor payload required by a recorded command buffer");
            RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanExactResourceInvalidation(
                result.ExactVariantsDirtied,
                result.ExactCommandChainsDirtied,
                result.UnrelatedVariantsPreserved,
                result.GlobalFallbackInvalidations);
        }
        finally
        {
            if (dependentCommandBuffers is not null)
                ArrayPool<ulong>.Shared.Return(dependentCommandBuffers);
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
