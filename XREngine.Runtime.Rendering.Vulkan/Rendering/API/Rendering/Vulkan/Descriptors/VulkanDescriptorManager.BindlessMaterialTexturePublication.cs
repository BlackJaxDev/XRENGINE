using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

internal sealed unsafe partial class VulkanDescriptorManager
{
    /// <summary>
    /// Republishes a streamed texture before its old image generation enters the
    /// retirement queues. The bindless table state remains singular in
    /// <see cref="BindlessMaterialTextures"/>; this descriptor authority owns
    /// the native update rather than routing through the renderer facade.
    /// </summary>
    internal EVulkanTextureDescriptorPublicationDisposition
        PublishGlobalMaterialTextureDescriptor(
            XRTexture texture,
            ImageView imageView,
            Sampler sampler,
            ImageLayout imageLayout,
            long streamingGeneration,
            ulong wrapperDescriptorGeneration,
            out VulkanBindlessMaterialTextureSlotTransfer retainedResourceTransfer,
            out string? failureDetail)
    {
        ArgumentNullException.ThrowIfNull(texture);
        retainedResourceTransfer = default;
        VulkanBackendObjectContext context = _backendContext ?? throw new InvalidOperationException(
            "The descriptor manager has not been bound to a Vulkan backend context.");
        VulkanBindlessMaterialTextureTableState state = BindlessMaterialTextures;
        lock (state.Sync)
        {
            if (state.Set.Handle == 0)
            {
                failureDetail =
                    "The global material texture descriptor table is not available.";
                return EVulkanTextureDescriptorPublicationDisposition.NotBound;
            }
            if (!state.SlotsByTexture.TryGetValue(texture, out uint slotIndex))
            {
                failureDetail =
                    "The texture has no published global material descriptor slot.";
                return EVulkanTextureDescriptorPublicationDisposition.NotBound;
            }
            if (slotIndex >= state.Slots.Length ||
                !ReferenceEquals(state.Slots[slotIndex].Texture, texture))
            {
                failureDetail =
                    $"The global material descriptor slot {slotIndex} is no " +
                    "longer owned by the streamed texture.";
                return EVulkanTextureDescriptorPublicationDisposition.Failed;
            }
            if (imageView.Handle == 0 ||
                !context.Resources.Images.IsAvailableForDescriptor(imageView))
            {
                failureDetail =
                    $"The exact streamed image view 0x{imageView.Handle:X} is not descriptor-ready.";
                return EVulkanTextureDescriptorPublicationDisposition.Failed;
            }
            if (sampler.Handle == 0 || !IsLiveSampler(sampler))
            {
                failureDetail =
                    $"The exact streamed sampler 0x{sampler.Handle:X} is not live.";
                return EVulkanTextureDescriptorPublicationDisposition.Failed;
            }

            DescriptorImageInfo imageInfo = new()
            {
                ImageLayout = imageLayout,
                ImageView = imageView,
                Sampler = sampler,
            };

            ulong imageViewGeneration =
                context.Resources.GetPublishedGeneration(
                    ObjectType.ImageView,
                    imageInfo.ImageView.Handle);
            ulong samplerGeneration =
                context.Resources.GetPublishedGeneration(
                    ObjectType.Sampler,
                    imageInfo.Sampler.Handle);
            ulong frameId = RuntimeEngine.Rendering.State.RenderFrameId;
            ref MaterialTextureDescriptorSlot currentSlot =
                ref state.Slots[slotIndex];
            currentSlot.LastUsedFrameId = frameId;
            currentSlot.PendingRetirement = false;
            currentSlot.RetireAfterFrameId = 0UL;
            if (!currentSlot.Dirty &&
                currentSlot.IsGenerationSnapshot &&
                currentSlot.ImageInfo.ImageView.Handle == imageInfo.ImageView.Handle &&
                currentSlot.ImageInfo.Sampler.Handle == imageInfo.Sampler.Handle &&
                currentSlot.ImageInfo.ImageLayout == imageInfo.ImageLayout &&
                currentSlot.ImageViewGeneration == imageViewGeneration &&
                currentSlot.SamplerGeneration == samplerGeneration &&
                currentSlot.WrapperDescriptorGeneration == wrapperDescriptorGeneration &&
                currentSlot.StreamingGeneration == streamingGeneration)
            {
                failureDetail = null;
                return EVulkanTextureDescriptorPublicationDisposition.ExactPublished;
            }

            // A published slot is immutable. Older material-table rows and
            // submitted command buffers keep their exact descriptor element;
            // the streamed generation is written into a new, unused element.
            // UPDATE_UNUSED_WHILE_PENDING makes this legal while the shared set
            // remains in flight.
            bool reservedReplacement = currentSlot.IsGenerationSnapshot;
            bool recycledReplacement = false;
            uint publicationSlotIndex = slotIndex;
            if (reservedReplacement &&
                !TryReserveGlobalMaterialTextureDescriptorSlot(
                    out publicationSlotIndex,
                    out recycledReplacement,
                    out string reservationFailure))
            {
                failureDetail = reservationFailure;
                return EVulkanTextureDescriptorPublicationDisposition.Failed;
            }

            bool slotWasDirty =
                !reservedReplacement &&
                state.Slots[publicationSlotIndex].Dirty;
            WriteDescriptorSet write = new()
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = state.Set,
                DstBinding = VulkanBindlessMaterialDescriptors.TextureArrayBinding,
                DstArrayElement = publicationSlotIndex,
                DescriptorType = DescriptorType.CombinedImageSampler,
                DescriptorCount = 1U,
                PImageInfo = &imageInfo,
            };
            try
            {
                context.Resources.DescriptorLifetime.UpdateDescriptorSets(
                    1U,
                    &write);
            }
            catch (Exception exception)
            {
                if (reservedReplacement)
                {
                    RollbackGlobalMaterialTextureDescriptorSlotReservation(
                        publicationSlotIndex,
                        recycledReplacement);
                }
                failureDetail =
                    $"Exact descriptor update for slot {publicationSlotIndex} failed: " +
                    exception.Message;
                return EVulkanTextureDescriptorPublicationDisposition.Failed;
            }

            ref MaterialTextureDescriptorSlot slot =
                ref state.Slots[publicationSlotIndex];
            slot.Texture = texture;
            slot.ImageInfo = imageInfo;
            slot.ExpectedImageLayout = imageInfo.ImageLayout;
            slot.ImageViewGeneration = imageViewGeneration;
            slot.SamplerGeneration = samplerGeneration;
            slot.WrapperDescriptorGeneration = wrapperDescriptorGeneration;
            slot.StreamingGeneration = streamingGeneration;
            slot.Generation++;
            slot.LastUsedFrameId = frameId;
            slot.PendingRetirement = false;
            slot.RetireAfterFrameId = 0UL;
            slot.IsGenerationSnapshot = true;
            // Preserve a pre-existing general-stream entry. Its eventual flush
            // will republish the same exact CPU slot; clearing the flag here
            // would allow a duplicate dirty ID to be appended meanwhile.
            slot.Dirty = slotWasDirty;
            if (reservedReplacement)
            {
                currentSlot.LeaseCount++;
                retainedResourceTransfer = new(
                    slotIndex,
                    currentSlot.Generation);
            }
            state.SlotsByTexture[texture] = publicationSlotIndex;
            state.WritesLastFlush = 1UL;
            state.WritesTotal++;
            try
            {
                RecordVulkanDescriptorTableGeneration(
                    "GlobalMaterialTextureDescriptorSet.ExactUpdate");
            }
            catch (Exception exception)
            {
                // The native and CPU descriptor commits are already complete.
                // Generation telemetry must never turn that committed update
                // into a false terminal upload failure whose cleanup would
                // release the descriptor's live handles.
                Debug.VulkanWarning(
                    "[Vulkan] Exact streamed texture descriptor publication " +
                    "committed, but descriptor-table generation telemetry " +
                    "failed for slot {0}: {1}",
                    publicationSlotIndex,
                    exception.Message);
            }
            failureDetail = null;
            return EVulkanTextureDescriptorPublicationDisposition.ExactPublished;
        }
    }

    /// <summary>
    /// Transfers the wrapper allocation replaced by streaming into the exact
    /// immutable descriptor element that still references it. The temporary
    /// transfer lease is always released here; accepted-frame leases may keep
    /// the element alive longer.
    /// </summary>
    internal bool CompleteGlobalMaterialTextureRetainedResourceTransfer(
        in VulkanBindlessMaterialTextureSlotTransfer transfer,
        in RetiredImageResources resources)
    {
        if (!transfer.IsValid)
            return false;

        VulkanBindlessMaterialTextureTableState state = BindlessMaterialTextures;
        lock (state.Sync)
        {
            if (transfer.DescriptorIndex >= state.Slots.Length)
                return false;

            ref MaterialTextureDescriptorSlot slot =
                ref state.Slots[transfer.DescriptorIndex];
            if (slot.Generation != transfer.SlotGeneration ||
                !slot.IsGenerationSnapshot ||
                slot.LeaseCount <= 0)
            {
                return false;
            }

            bool hasResources =
                resources.Image.Handle != 0 ||
                resources.Memory.Handle != 0 ||
                resources.PrimaryView.Handle != 0 ||
                resources.AttachmentViews is { Length: > 0 } ||
                resources.Sampler.Handle != 0;
            if (hasResources)
            {
                if (slot.HasRetainedResources)
                    return false;
                slot.RetainedResources = resources;
                slot.HasRetainedResources = true;
            }

            slot.LeaseCount--;
            return true;
        }
    }
}
