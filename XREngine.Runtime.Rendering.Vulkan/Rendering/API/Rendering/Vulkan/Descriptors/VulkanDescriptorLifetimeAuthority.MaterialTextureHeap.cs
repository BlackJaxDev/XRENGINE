using System.Buffers;
using Silk.NET.Vulkan;
using XREngine.Rendering.Materials;

namespace XREngine.Rendering.Vulkan;

internal sealed unsafe partial class VulkanDescriptorLifetimeAuthority
{
    /// <summary>
    /// Publishes the sparse, sealed material-table closure into an append-only
    /// arena. The arena is deliberately outside the generic immutable binding
    /// cache: its fixed heap buffers are shared, while old arena ranges remain
    /// untouched after growth.
    /// </summary>
    internal bool TryPublishGlobalMaterialTextureHeapArena(
        VkRenderProgram program,
        DescriptorBindingInfo binding,
        DescriptorHeapPushDataPayload payload,
        VulkanBindlessMaterialTextureHeapArena arena,
        ReadOnlySpan<MaterialTextureDescriptorSlot> slots,
        ReadOnlySpan<GPUMaterialTextureReference> references,
        out string reason)
    {
        reason = string.Empty;
        if (!IsDescriptorHeapActive || !VulkanBindlessMaterialDescriptors.IsBindlessTextureArrayBinding(binding) ||
            binding.DescriptorType != DescriptorType.CombinedImageSampler || program.DescriptorHeapLayout is not { } layout ||
            !layout.TryGetBinding(binding.Set, binding.Binding, out DescriptorHeapBindingLayout bindingLayout) ||
            !bindingLayout.HasResource || !bindingLayout.HasSampler ||
            bindingLayout.ResourceDescriptorType != DescriptorType.SampledImage ||
            bindingLayout.ResourceStride == 0 || bindingLayout.SamplerStride == 0 ||
            bindingLayout.ResourceStride != ResolveHeapDescriptorStride(DescriptorType.SampledImage) ||
            bindingLayout.SamplerStride != ResolveHeapDescriptorStride(DescriptorType.Sampler) ||
            bindingLayout.ResourcePushOffset == uint.MaxValue || bindingLayout.SamplerPushOffset == uint.MaxValue ||
            bindingLayout.ResourcePushOffset == bindingLayout.SamplerPushOffset || !payload.IsValidFor(layout))
        {
            reason = "global material texture heap arena requires a valid sampled-image/sampler heap binding.";
            return false;
        }
        if (slots.Length == 0 || slots[0].ImageInfo.ImageView.Handle == 0 ||
            slots[0].ImageInfo.Sampler.Handle == 0 || slots[0].HeapRewriteSerial == 0 || slots[0].Dirty)
        {
            reason = "global material texture heap arena has no slot-zero descriptor.";
            return false;
        }
        uint highest = 0;
        for (int i = 0; i < references.Length; i++)
        {
            ref readonly GPUMaterialTextureReference reference = ref references[i];
            if (reference.Kind != EGPUMaterialTextureReferenceKind.VulkanDescriptorIndex)
                continue;
            uint index = reference.VulkanDescriptorIndex;
            if (index <= highest || index >= (uint)slots.Length)
            {
                reason = $"sealed material texture reference {index} is not a unique ascending resident table slot.";
                return false;
            }
            ref readonly MaterialTextureDescriptorSlot slot = ref slots[(int)index];
            if (slot.Generation != reference.VulkanDescriptorGeneration || slot.HeapRewriteSerial == 0 ||
                slot.Dirty || slot.PendingRetirement || !slot.IsGenerationSnapshot)
            {
                reason = $"sealed material texture reference {index} is not an exact published slot.";
                return false;
            }
            highest = Math.Max(highest, index);
        }
        uint required = highest + 1u;
        if (required > bindingLayout.DescriptorCount)
        {
            reason = $"global material texture heap arena requires {required} descriptors but reflection permits {bindingLayout.DescriptorCount}.";
            return false;
        }
        if (arena.Capacity != 0 && (arena.ResourceStride != bindingLayout.ResourceStride ||
            arena.SamplerStride != bindingLayout.SamplerStride || arena.PublishedRewriteSerials.Length < arena.Capacity ||
            arena.ResourceOffset % bindingLayout.ResourceStride != 0 || arena.SamplerOffset % bindingLayout.SamplerStride != 0 ||
            arena.ResourceBaseIndex != arena.ResourceOffset / bindingLayout.ResourceStride ||
            arena.SamplerBaseIndex != arena.SamplerOffset / bindingLayout.SamplerStride))
        {
            reason = "global material texture heap arena descriptor strides changed without table teardown.";
            return false;
        }

        int pinCapacity = checked((references.Length + 1) * 3);
        VulkanPinnedResourceGeneration[]? rented = null;
        scoped Span<VulkanPinnedResourceGeneration> pinned;
        if (pinCapacity <= 16)
            pinned = stackalloc VulkanPinnedResourceGeneration[16];
        else
        {
            rented = ArrayPool<VulkanPinnedResourceGeneration>.Shared.Rent(pinCapacity);
            pinned = rented;
        }
        try
        {
            using (VulkanFrameLockScope.Enter(RequireSubmissionStateGate(), EVulkanFrameWaitReason.SubmissionStateLock))
            using (VulkanFrameLockScope.Enter(_lifetime.Tracker.SyncRoot, EVulkanFrameWaitReason.ResourceLifetimeLock))
            {
                // Capture the sealed closure before mutating native bytes. The
                // payload receives it only after every scalar pair succeeds.
                int pinnedCount = 0;
                VulkanBackendObjectContext context = RequireBackendContext();
                for (int referenceIndex = -1; referenceIndex < references.Length; referenceIndex++)
                {
                    uint slotIndex;
                    if (referenceIndex < 0)
                        slotIndex = 0u;
                    else
                    {
                        ref readonly GPUMaterialTextureReference reference = ref references[referenceIndex];
                        if (reference.Kind != EGPUMaterialTextureReferenceKind.VulkanDescriptorIndex)
                            continue;
                        slotIndex = reference.VulkanDescriptorIndex;
                    }
                    ref readonly MaterialTextureDescriptorSlot slot = ref slots[(int)slotIndex];
                    if (slot.ImageViewGeneration == 0 || slot.SamplerGeneration == 0)
                    {
                        reason = $"global material texture slot {slotIndex} has no exact native resource generations.";
                        return false;
                    }
                    if (!TryAppendDescriptorHeapResourceGeneration(
                            pinned, ref pinnedCount, context, ObjectType.ImageView,
                            slot.ImageInfo.ImageView.Handle, out reason, slot.ImageViewGeneration) ||
                        !TryAppendDescriptorHeapResourceGeneration(
                            pinned, ref pinnedCount, context, ObjectType.Sampler,
                            slot.ImageInfo.Sampler.Handle, out reason, slot.SamplerGeneration))
                        return false;
                }
                if (!TryUpdateGlobalMaterialTextureHeapArena(bindingLayout, arena, slots, references, highest, out reason))
                    return false;
                payload.SetResourceGenerations(bindingLayout.Key, pinned[..pinnedCount]);
                payload.SetDword(bindingLayout.ResourcePushOffset, arena.ResourceBaseIndex);
                payload.SetDword(bindingLayout.SamplerPushOffset, arena.SamplerBaseIndex);
                return true;
            }
        }
        finally
        {
            if (rented is not null)
                ArrayPool<VulkanPinnedResourceGeneration>.Shared.Return(rented);
        }
    }

    /// <summary>Commits complete scalar pairs and reserves a new range atomically on growth.</summary>
    private bool TryUpdateGlobalMaterialTextureHeapArena(
        DescriptorHeapBindingLayout bindingLayout,
        VulkanBindlessMaterialTextureHeapArena arena,
        ReadOnlySpan<MaterialTextureDescriptorSlot> slots,
        ReadOnlySpan<GPUMaterialTextureReference> references,
        uint highest,
        out string reason)
    {
        reason = string.Empty;
        uint capacity = arena.Capacity;
        while (capacity <= highest)
            capacity = capacity == 0 ? 1u : checked(capacity * 2u);
        bool grow = capacity != arena.Capacity;
        ulong oldResource = _descriptors.Heap.ResourceHighWaterBytes;
        ulong oldSampler = _descriptors.Heap.SamplerHighWaterBytes;
        ulong resourceOffset = arena.ResourceOffset, samplerOffset = arena.SamplerOffset;
        ulong resourceSize = checked((ulong)capacity * bindingLayout.ResourceStride);
        ulong samplerSize = checked((ulong)capacity * bindingLayout.SamplerStride);
        bool growthPublished = !grow;
        try
        {
            if (grow && (!TryAllocateHeapRange(false, DescriptorType.SampledImage, capacity, out resourceOffset, out resourceSize, out reason) ||
                         !TryAllocateHeapRange(true, DescriptorType.Sampler, capacity, out samplerOffset, out samplerSize, out reason)))
            {
                reason = $"global material texture heap arena growth failed: highest={highest}, capacity={arena.Capacity}->{capacity}, " +
                    $"resource={oldResource}/{_descriptors.Heap.ResourceStorage.Size}, sampler={oldSampler}/{_descriptors.Heap.SamplerStorage.Size}; {reason}";
                return false;
            }
            // Growth allocates only at powers of two; successful generations
            // retain their own serial array while old descriptor bytes survive.
            ulong[] serials = grow ? new ulong[capacity] : arena.PublishedRewriteSerials;
            for (int referenceIndex = -1; referenceIndex < references.Length; referenceIndex++)
            {
                uint index;
                if (referenceIndex < 0)
                    index = 0;
                else
                {
                    ref readonly GPUMaterialTextureReference reference = ref references[referenceIndex];
                    if (reference.Kind != EGPUMaterialTextureReferenceKind.VulkanDescriptorIndex)
                        continue;
                    index = reference.VulkanDescriptorIndex;
                }
                ref readonly MaterialTextureDescriptorSlot slot = ref slots[(int)index];
                if (serials[index] == slot.HeapRewriteSerial)
                    continue;
                if (!TryWriteCombinedImageSamplerHeapDescriptorAtOffsets(in slot.ImageInfo,
                        resourceOffset + (ulong)index * bindingLayout.ResourceStride, bindingLayout.ResourceStride,
                        samplerOffset + (ulong)index * bindingLayout.SamplerStride, bindingLayout.SamplerStride, out reason))
                {
                    return false;
                }
                // A completed pair is exact even if a later unused slot fails.
                // Recycled slots are writable only after their old leases reach
                // zero. Growth serials remain local until the whole range commits.
                serials[index] = slot.HeapRewriteSerial;
            }
            if (grow)
            {
                uint resourceBaseIndex = checked((uint)(resourceOffset / bindingLayout.ResourceStride));
                uint samplerBaseIndex = checked((uint)(samplerOffset / bindingLayout.SamplerStride));
                arena.ResourceOffset = resourceOffset;
                arena.SamplerOffset = samplerOffset;
                arena.ResourceBaseIndex = resourceBaseIndex;
                arena.SamplerBaseIndex = samplerBaseIndex;
                arena.ResourceStride = bindingLayout.ResourceStride;
                arena.SamplerStride = bindingLayout.SamplerStride;
                arena.Capacity = capacity;
                arena.PublishedRewriteSerials = serials;
                growthPublished = true;
            }
            return true;
        }
        finally
        {
            if (!growthPublished)
                RestoreHeapBindingTransaction(oldResource, oldSampler);
        }
    }

    private bool TryWriteCombinedImageSamplerHeapDescriptorAtOffsets(in DescriptorImageInfo source, ulong resourceOffset, ulong resourceStride, ulong samplerOffset, ulong samplerStride, out string reason)
    {
        reason = string.Empty;
        if (!_resources.Images.TryGetDescriptorHeapCreateInfo(source.ImageView, out ImageViewCreateInfo view) ||
            !_descriptors.TryGetSamplerCreateInfo(source.Sampler, out SamplerCreateInfo sampler))
        {
            reason = "global material texture heap descriptor has unavailable image-view or sampler metadata.";
            return false;
        }
        ImageDescriptorInfoEXTNative image = new() { SType = VulkanDescriptorHeapExt.ImageDescriptorInfoSType, View = &view, Layout = source.ImageLayout };
        ResourceDescriptorInfoEXTNative resource = new() { SType = VulkanDescriptorHeapExt.ResourceDescriptorInfoSType, Type = DescriptorType.SampledImage };
        resource.Data.Image = &image;
        return TryWriteResourceDescriptors(1, &resource, resourceOffset, resourceStride, out reason) &&
               TryWriteSamplerDescriptors(1, &sampler, samplerOffset, samplerStride, out reason);
    }

}
