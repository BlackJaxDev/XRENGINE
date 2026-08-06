using System.Diagnostics;
using System.Threading;
using Silk.NET.Vulkan;
using Semaphore = Silk.NET.Vulkan.Semaphore;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    private static VulkanResourceLifetimeKey ResourceKey(ObjectType type, ulong handle)
        => new(type, handle);

    internal ulong GetCurrentVulkanResourceGeneration(ObjectType type, ulong handle)
        => ResourceRuntime.Lifetime.Tracker.GetPublishedGeneration(ResourceKey(type, handle));

    internal bool TryGetBufferViewBackingBuffer(
        BufferView bufferView,
        out Silk.NET.Vulkan.Buffer buffer)
    {
        lock (ResourceRuntime.Lifetime.Tracker.SyncRoot)
        {
            if (bufferView.Handle != 0 &&
                ResourceRuntime.Lifetime.Tracker.BufferViewBackingBuffers.TryGetValue(
                    bufferView.Handle,
                    out ulong handle) &&
                handle != 0)
            {
                buffer = new Silk.NET.Vulkan.Buffer(handle);
                return true;
            }
        }

        buffer = default;
        return false;
    }



    internal void RegisterVulkanResource(
        ObjectType type,
        ulong handle,
        string owner,
        bool externallyOwned = false)
        => ResourceRuntime.Lifetime.Tracker.RegisterResource(
            ResourceKey(type, handle),
            owner,
            externallyOwned);

    internal void RegisterVulkanPipeline(Pipeline pipeline, string owner)
        => RegisterVulkanResource(ObjectType.Pipeline, pipeline.Handle, owner);

    internal Result CreateVulkanImageTracked(
        ref ImageCreateInfo createInfo,
        Image* image,
        string owner)
    {
        ThrowIfVulkanDeviceOperationNotAdmitted("vkCreateImage." + owner);
        ThrowIfPersistentResourceAllocationDuringRecording(owner);
        Result result = Api!.CreateImage(_deviceContext.Device, ref createInfo, null, image);
        if (result == Result.Success && image is not null)
            RegisterVulkanResource(ObjectType.Image, image->Handle, owner);
        return result;
    }

    internal Result CreateVulkanImageTracked(
        ref ImageCreateInfo createInfo,
        out Image image,
        string owner)
    {
        image = default;
        fixed (Image* imagePtr = &image)
            return CreateVulkanImageTracked(ref createInfo, imagePtr, owner);
    }

    internal void DestroyVulkanImageImmediateTracked(Image image, string owner)
    {
        if (image.Handle == 0)
            return;

        VulkanRetirementTicket ticket = CaptureVulkanRetirementTicket(
            ObjectType.Image,
            image.Handle,
            owner);
        if (!IsVulkanRetirementReady(ticket))
        {
            throw new InvalidOperationException(
                $"Cannot immediately destroy image 0x{image.Handle:X} in {owner} before its GPU completion point.");
        }

        Api!.DestroyImage(_deviceContext.Device, image, null);
        CompleteVulkanResourceDestruction(ObjectType.Image, image.Handle);
    }

    internal void RegisterVulkanFramebuffer(
        Framebuffer framebuffer,
        ReadOnlySpan<ImageView> attachments,
        string owner)
    {
        if (framebuffer.Handle == 0)
            return;

        RegisterVulkanResource(ObjectType.Framebuffer, framebuffer.Handle, owner);
        VulkanResourceLifetimeKey[] attachmentKeys = new VulkanResourceLifetimeKey[attachments.Length];
        for (int i = 0; i < attachments.Length; i++)
            attachmentKeys[i] = ResourceKey(ObjectType.ImageView, attachments[i].Handle);

        lock (ResourceRuntime.Lifetime.Tracker.SyncRoot)
            ResourceRuntime.Lifetime.Tracker.FramebufferAttachments[framebuffer.Handle] = attachmentKeys;
    }

    private void RegisterVulkanImageViewResource(
        ImageView imageView,
        Image backingImage,
        string owner,
        bool backingImageExternallyOwned)
    {
        RegisterVulkanResource(ObjectType.ImageView, imageView.Handle, owner);
        if (backingImage.Handle == 0)
            return;

        RegisterVulkanResource(ObjectType.Image, backingImage.Handle, $"{owner}.BackingImage", backingImageExternallyOwned);
        lock (ResourceRuntime.Lifetime.Tracker.SyncRoot)
            ResourceRuntime.Lifetime.Tracker.ImageViewBackingImages[imageView.Handle] = backingImage.Handle;
    }

    private void RegisterVulkanBufferViewResource(
        BufferView bufferView,
        Silk.NET.Vulkan.Buffer backingBuffer,
        string owner)
    {
        RegisterVulkanResource(ObjectType.BufferView, bufferView.Handle, owner);
        if (backingBuffer.Handle == 0)
            return;

        RegisterVulkanResource(ObjectType.Buffer, backingBuffer.Handle, $"{owner}.BackingBuffer");
        lock (ResourceRuntime.Lifetime.Tracker.SyncRoot)
            ResourceRuntime.Lifetime.Tracker.BufferViewBackingBuffers[bufferView.Handle] = backingBuffer.Handle;
    }

    private VulkanResourceLifetimeRecord GetOrRegisterVulkanResource_NoLock(
        VulkanResourceLifetimeKey key,
        string owner)
        => ResourceRuntime.Lifetime.Tracker.GetOrRegisterResourceNoLock(key, owner);

    private void ResetVulkanCommandBufferLifetime(CommandBuffer commandBuffer)
    {
        if (commandBuffer.Handle == 0)
            return;

        ulong handle = unchecked((ulong)commandBuffer.Handle);
        _invalidatedCommandBuffersPendingReset.TryRemove(handle, out _);
        RegisterVulkanResource(ObjectType.CommandBuffer, handle, "CommandBuffer");
        lock (ResourceRuntime.Lifetime.Tracker.SyncRoot)
        {
            VulkanResourceLifetimeRecord commandRecord = GetOrRegisterVulkanResource_NoLock(
                ResourceKey(ObjectType.CommandBuffer, handle),
                "CommandBuffer");
            if ((commandRecord.State & EVulkanResourceLifetimeState.PendingRetirement) != 0)
            {
                throw new InvalidOperationException(
                    $"Command buffer 0x{handle:X} cannot be reset while pending retirement.");
            }
            if (commandRecord.Pins.HasRecordedReferences)
            {
                throw new InvalidOperationException(
                    $"Command buffer 0x{handle:X} cannot be reset while referenced by {commandRecord.Pins.RecordedReferenceCount} recorded command buffer(s).");
            }

            if (!UpdateVulkanResourceCompletionState_NoLock(commandRecord))
            {
                throw new InvalidOperationException(
                    $"Command buffer 0x{handle:X} cannot be reset before its submitted completion ticket.");
            }
            commandRecord.State |= EVulkanResourceLifetimeState.CpuOwned;

            if (!ResourceRuntime.Lifetime.Tracker.CommandBufferLifetimes.TryGetValue(handle, out VulkanCommandBufferLifetimeRecord? lifetime))
            {
                lifetime = new VulkanCommandBufferLifetimeRecord();
                ResourceRuntime.Lifetime.Tracker.CommandBufferLifetimes[handle] = lifetime;
            }

            if (lifetime.QueuedSubmissionCount != 0)
            {
                throw new InvalidOperationException(
                    $"Command buffer 0x{handle:X} cannot be reset while queued for submission.");
            }

            if (!IsVulkanCommandBufferPoolOwnershipAvailable_NoLock(handle, lifetime, out string poolFailureReason))
                throw new InvalidOperationException(poolFailureReason);

            ReleaseVulkanCommandBufferDependencies_NoLock(handle, lifetime);
            lifetime.FrameDataLease.EvictCachedVariant();
            lifetime.FrameDataLease.Reset();
            lifetime.RecordingGeneration++;
        }
    }

    /// <summary>
    /// Verifies that a command buffer is no longer recording, queued, submitted, or
    /// pending retirement before a native reset is issued. Vulkan requires this
    /// check to happen before <c>vkResetCommandBuffer</c>; doing it from the
    /// post-begin bind-state reset is too late to prevent invalid pending reuse.
    /// </summary>
    private bool CanResetVulkanCommandBuffer(CommandBuffer commandBuffer, out string reason)
    {
        if (commandBuffer.Handle == 0)
        {
            reason = "command buffer handle is null";
            return false;
        }

        ulong handle = unchecked((ulong)commandBuffer.Handle);
        lock (ResourceRuntime.Lifetime.Tracker.SyncRoot)
        {
            if (_commandBufferTrackingBatches.TryGetValue(handle, out VulkanCommandBufferTrackingBatch? batch))
            {
                lock (batch)
                {
                    if (batch.IsRecording)
                    {
                        reason = "command buffer is still recording";
                        return false;
                    }

                    if (batch.QueuedSubmissionCount != 0)
                    {
                        reason = "command buffer is queued for submission";
                        return false;
                    }
                }
            }

            if (ResourceRuntime.Lifetime.Tracker.CommandBufferLifetimes.TryGetValue(handle, out VulkanCommandBufferLifetimeRecord? lifetime) &&
                lifetime.QueuedSubmissionCount != 0)
            {
                reason = "command buffer occupies the submission gateway";
                return false;
            }

            if (lifetime is not null &&
                !IsVulkanCommandBufferPoolOwnershipAvailable_NoLock(handle, lifetime, out reason))
            {
                return false;
            }

            VulkanResourceLifetimeRecord commandRecord = GetOrRegisterVulkanResource_NoLock(
                ResourceKey(ObjectType.CommandBuffer, handle),
                "CommandBuffer.Reset");
            if ((commandRecord.State & EVulkanResourceLifetimeState.PendingRetirement) != 0)
            {
                reason = "command buffer is pending retirement";
                return false;
            }
            if (commandRecord.Pins.HasRecordedReferences)
            {
                reason = $"command buffer is referenced by {commandRecord.Pins.RecordedReferenceCount} recorded command buffer(s)";
                return false;
            }


            if ((commandRecord.State & EVulkanResourceLifetimeState.Destroyed) != 0)
            {
                reason = "command buffer is destroyed";
                return false;
            }

            if (!UpdateVulkanResourceCompletionState_NoLock(commandRecord))
            {
                reason =
                    $"submission is incomplete (graphics={commandRecord.Pins.LastGraphicsSequence}/{ResourceRuntime.Lifetime.Tracker.CompletedGraphicsSequence}, " +
                    $"transfer={commandRecord.Pins.LastTransferSequence}/{ResourceRuntime.Lifetime.Tracker.CompletedTransferSequence}, " +
                    $"other={commandRecord.Pins.LastOtherSequence}/{ResourceRuntime.Lifetime.Tracker.CompletedOtherSequence})";
                return false;
            }
        }

        reason = string.Empty;
        return true;
    }

    private Result ResetVulkanCommandBufferTracked(CommandBuffer commandBuffer)
    {
        if (!CanResetVulkanCommandBuffer(commandBuffer, out string reason))
        {
            throw new InvalidOperationException(
                $"Command buffer 0x{unchecked((ulong)commandBuffer.Handle):X} cannot be reset: {reason}.");
        }

        Result result = Api!.ResetCommandBuffer(commandBuffer, 0);
        RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanResetCommandBufferCall();
        return result;
    }

    private void RemoveVulkanCommandBufferLifetime(CommandBuffer commandBuffer, bool destroyed = false)
    {
        if (commandBuffer.Handle == 0)
            return;

        ulong handle = unchecked((ulong)commandBuffer.Handle);
        _invalidatedCommandBuffersPendingReset.TryRemove(handle, out _);
        lock (ResourceRuntime.Lifetime.Tracker.SyncRoot)
        {
            if (ResourceRuntime.Lifetime.Tracker.CommandBufferLifetimes.TryGetValue(handle, out VulkanCommandBufferLifetimeRecord? lifetime) &&
                lifetime.QueuedSubmissionCount != 0)
            {
                throw new InvalidOperationException(
                    $"Command buffer 0x{handle:X} lifetime cannot be removed while queued for submission.");
            }

            if (ResourceRuntime.Lifetime.Tracker.CommandBufferLifetimes.Remove(handle, out lifetime))
            {
                ReleaseVulkanCommandBufferDependencies_NoLock(handle, lifetime);
                RemoveVulkanCommandBufferPoolOwnership_NoLock(handle, lifetime);
            }
            if (destroyed && ResourceRuntime.Lifetime.Tracker.ResourceLifetimes.TryGetValue(
                    ResourceKey(ObjectType.CommandBuffer, handle),
                    out VulkanResourceLifetimeRecord? record))
            {
                record.State = EVulkanResourceLifetimeState.Destroyed;
            }
        }
    }

    internal void TrackVulkanCommandBufferResource(
        CommandBuffer commandBuffer,
        ObjectType type,
        ulong handle,
        string owner)
    {
        if (commandBuffer.Handle == 0 || handle == 0)
            return;

        ulong commandBufferHandle = unchecked((ulong)commandBuffer.Handle);
        VulkanResourceLifetimeKey key = ResourceKey(type, handle);
        ulong expectedGeneration = ResourceRuntime.Lifetime.Tracker.GetPublishedGeneration(key);
        if (expectedGeneration != 0 &&
            TryRecordCommandBufferDependency(commandBuffer, type, handle))
        {
            ulong observedGeneration = ResourceRuntime.Lifetime.Tracker.GetPublishedGeneration(key);
            if (observedGeneration == expectedGeneration)
                return;

            throw new InvalidOperationException(
                $"Command buffer 0x{commandBufferHandle:X} lost recording admission for Vulkan resource {key} " +
                $"generation {expectedGeneration} while binding it; current published generation is {observedGeneration}.");
        }

        lock (ResourceRuntime.Lifetime.Tracker.SyncRoot)
            TrackVulkanCommandBufferResource_NoLock(commandBufferHandle, key, owner);
    }

    /// <summary>
    /// Rejects an image view before a native rendering command can dereference a retired
    /// or handle-recycled attachment. Command-buffer dependency batches normally validate
    /// at end-of-recording; attachment handles require this additional immediate boundary.
    /// </summary>
    private void EnsureVulkanImageViewAvailableForCommandRecording(
        CommandBuffer commandBuffer,
        ImageView imageView,
        string owner,
        ulong expectedGeneration = 0)
    {
        if (imageView.Handle == 0)
            return;

        VulkanResourceLifetimeKey key = ResourceKey(ObjectType.ImageView, imageView.Handle);
        lock (ResourceRuntime.Lifetime.Tracker.SyncRoot)
        {
            VulkanResourceLifetimeRecord resource = GetOrRegisterVulkanResource_NoLock(key, owner);
            bool generationChanged = expectedGeneration != 0 &&
                resource.Generation != expectedGeneration;
            bool retired =
                (resource.State &
                 (EVulkanResourceLifetimeState.PendingRetirement |
                  EVulkanResourceLifetimeState.Destroyed)) != 0;
            if (generationChanged || retired)
            {
                throw new InvalidOperationException(
                    $"Command buffer 0x{unchecked((ulong)commandBuffer.Handle):X} attempted to record retired Vulkan resource {key} generation {expectedGeneration} " +
                    $"but current generation is {resource.Generation} with state {resource.State} owned by {resource.Owner}; requested by {owner}.");
            }

            if (!ResourceRuntime.Lifetime.Tracker.ImageViewBackingImages.TryGetValue(
                    imageView.Handle,
                    out ulong backingImageHandle) ||
                backingImageHandle == 0)
            {
                return;
            }

            VulkanResourceLifetimeKey imageKey = ResourceKey(ObjectType.Image, backingImageHandle);
            // The view-specific owner is already carried into the final diagnostic.
            // Reuse it here instead of allocating an owner suffix for every attachment
            // checked during command-buffer recording.
            VulkanResourceLifetimeRecord image = GetOrRegisterVulkanResource_NoLock(
                imageKey,
                owner);
            if ((image.State &
                 (EVulkanResourceLifetimeState.PendingRetirement |
                  EVulkanResourceLifetimeState.Destroyed)) == 0)
            {
                return;
            }

            throw new InvalidOperationException(
                $"Command buffer 0x{unchecked((ulong)commandBuffer.Handle):X} attempted to record retired Vulkan resource {key} generation {resource.Generation} " +
                $"backed by retired {imageKey} generation {image.Generation} with state {image.State}; requested by {owner}.");
        }
    }

    internal void TrackVulkanCommandBufferResource_NoLock(
        ulong commandBufferHandle,
        VulkanResourceLifetimeKey key,
        string owner)
    {
        if (!TryTrackVulkanCommandBufferResource_NoLock(commandBufferHandle, key, owner, out string failureReason))
            throw new InvalidOperationException(failureReason);
    }

    private bool TryTrackVulkanCommandBufferResource_NoLock(
        ulong commandBufferHandle,
        VulkanResourceLifetimeKey key,
        string owner,
        out string failureReason,
        bool allowQueuedSubmission = false)
    {
        VulkanResourceLifetimeRecord resource = GetOrRegisterVulkanResource_NoLock(key, owner);
        if ((resource.State & (EVulkanResourceLifetimeState.PendingRetirement | EVulkanResourceLifetimeState.Destroyed)) != 0)
        {
            failureReason =
                $"Command buffer 0x{commandBufferHandle:X} attempted to record retired Vulkan resource {key} generation {resource.Generation} owned by {resource.Owner}; requested by {owner}.";
            return false;
        }

        if (!ResourceRuntime.Lifetime.Tracker.CommandBufferLifetimes.TryGetValue(commandBufferHandle, out VulkanCommandBufferLifetimeRecord? commandLifetime))
        {
            commandLifetime = new VulkanCommandBufferLifetimeRecord();
            ResourceRuntime.Lifetime.Tracker.CommandBufferLifetimes[commandBufferHandle] = commandLifetime;
        }

        if (!IsVulkanCommandBufferPoolOwnershipAvailable_NoLock(commandBufferHandle, commandLifetime, out failureReason))
            return false;

        if (commandLifetime.QueuedSubmissionCount != 0 && !allowQueuedSubmission)
        {
            failureReason =
                $"Command buffer 0x{commandBufferHandle:X} attempted to record {key} while queued for submission.";
            return false;
        }

        AddVulkanCommandBufferDependency_NoLock(commandBufferHandle, commandLifetime, resource);

        if (key.Type == ObjectType.ImageView &&
            ResourceRuntime.Lifetime.Tracker.ImageViewBackingImages.TryGetValue(key.Handle, out ulong backingImageHandle) &&
            backingImageHandle != 0)
        {
            VulkanResourceLifetimeKey imageKey = ResourceKey(ObjectType.Image, backingImageHandle);
            VulkanResourceLifetimeRecord image = GetOrRegisterVulkanResource_NoLock(
                imageKey,
                "CommandBufferDependency.BackingImage");
            if ((image.State & (EVulkanResourceLifetimeState.PendingRetirement | EVulkanResourceLifetimeState.Destroyed)) != 0)
            {
                failureReason =
                    $"Command buffer 0x{commandBufferHandle:X} attempted to record image view {key} backed by retired image {imageKey}.";
                return false;
            }

            AddVulkanCommandBufferDependency_NoLock(commandBufferHandle, commandLifetime, image);
        }

        if (key.Type == ObjectType.BufferView &&
            ResourceRuntime.Lifetime.Tracker.BufferViewBackingBuffers.TryGetValue(key.Handle, out ulong backingBufferHandle) &&
            backingBufferHandle != 0)
        {
            VulkanResourceLifetimeKey bufferKey = ResourceKey(ObjectType.Buffer, backingBufferHandle);
            VulkanResourceLifetimeRecord buffer = GetOrRegisterVulkanResource_NoLock(
                bufferKey,
                "CommandBufferDependency.BackingBuffer");
            if ((buffer.State & (EVulkanResourceLifetimeState.PendingRetirement | EVulkanResourceLifetimeState.Destroyed)) != 0)
            {
                failureReason =
                    $"Command buffer 0x{commandBufferHandle:X} attempted to record buffer view {key} backed by retired buffer {bufferKey}.";
                return false;
            }

            AddVulkanCommandBufferDependency_NoLock(commandBufferHandle, commandLifetime, buffer);
        }

        if (key.Type == ObjectType.Framebuffer &&
            ResourceRuntime.Lifetime.Tracker.FramebufferAttachments.TryGetValue(key.Handle, out VulkanResourceLifetimeKey[]? attachmentKeys))
        {
            for (int i = 0; i < attachmentKeys.Length; i++)
            {
                VulkanResourceLifetimeKey attachmentKey = attachmentKeys[i];
                if (attachmentKey.IsValid &&
                    !TryTrackVulkanCommandBufferResource_NoLock(
                        commandBufferHandle,
                        attachmentKey,
                        "Framebuffer.Attachment",
                        out failureReason,
                        allowQueuedSubmission))
                {
                    return false;
                }
            }
        }

        failureReason = string.Empty;
        return true;
    }

    /// <summary>
    /// Validates the complete dependency expansion without publishing any pins.
    /// The caller holds the lifetime lock, so a successful validation followed by
    /// publication is transactional with respect to resource retirement.
    /// </summary>
    private bool TryValidateVulkanCommandBufferResource_NoLock(
        ulong commandBufferHandle,
        VulkanResourceLifetimeKey key,
        string owner,
        out string failureReason,
        bool allowQueuedSubmission = false)
    {
        if (ResourceRuntime.Lifetime.Tracker.ResourceLifetimes.TryGetValue(
                key,
                out VulkanResourceLifetimeRecord? resource) &&
            (resource.State & (EVulkanResourceLifetimeState.PendingRetirement |
                               EVulkanResourceLifetimeState.Destroyed)) != 0)
        {
            failureReason =
                $"Command buffer 0x{commandBufferHandle:X} attempted to record retired Vulkan resource {key} generation {resource.Generation} owned by {resource.Owner}; requested by {owner}.";
            return false;
        }

        if (ResourceRuntime.Lifetime.Tracker.CommandBufferLifetimes.TryGetValue(
                commandBufferHandle,
                out VulkanCommandBufferLifetimeRecord? commandLifetime) &&
            commandLifetime.QueuedSubmissionCount != 0 &&
            !allowQueuedSubmission)
        {
            failureReason =
                $"Command buffer 0x{commandBufferHandle:X} attempted to record {key} while queued for submission.";
            return false;
        }

        if (commandLifetime is not null &&
            !IsVulkanCommandBufferPoolOwnershipAvailable_NoLock(commandBufferHandle, commandLifetime, out failureReason))
        {
            return false;
        }

        if (key.Type == ObjectType.ImageView &&
            ResourceRuntime.Lifetime.Tracker.ImageViewBackingImages.TryGetValue(
                key.Handle,
                out ulong backingImageHandle) &&
            backingImageHandle != 0 &&
            !TryValidateVulkanCommandBufferResource_NoLock(
                commandBufferHandle,
                ResourceKey(ObjectType.Image, backingImageHandle),
                "CommandBufferDependency.BackingImage",
                out failureReason,
                allowQueuedSubmission))
        {
            return false;
        }

        if (key.Type == ObjectType.BufferView &&
            ResourceRuntime.Lifetime.Tracker.BufferViewBackingBuffers.TryGetValue(
                key.Handle,
                out ulong backingBufferHandle) &&
            backingBufferHandle != 0 &&
            !TryValidateVulkanCommandBufferResource_NoLock(
                commandBufferHandle,
                ResourceKey(ObjectType.Buffer, backingBufferHandle),
                "CommandBufferDependency.BackingBuffer",
                out failureReason,
                allowQueuedSubmission))
        {
            return false;
        }

        if (key.Type == ObjectType.Framebuffer &&
            ResourceRuntime.Lifetime.Tracker.FramebufferAttachments.TryGetValue(
                key.Handle,
                out VulkanResourceLifetimeKey[]? attachmentKeys))
        {
            for (int i = 0; i < attachmentKeys.Length; i++)
            {
                VulkanResourceLifetimeKey attachmentKey = attachmentKeys[i];
                if (attachmentKey.IsValid &&
                    !TryValidateVulkanCommandBufferResource_NoLock(
                        commandBufferHandle,
                        attachmentKey,
                        "Framebuffer.Attachment",
                        out failureReason,
                        allowQueuedSubmission))
                {
                    return false;
                }
            }
        }

        failureReason = string.Empty;
        return true;
    }

    private void AddVulkanCommandBufferDependency_NoLock(
        ulong commandBufferHandle,
        VulkanCommandBufferLifetimeRecord commandLifetime,
        VulkanResourceLifetimeRecord resource)
    {
        if (!AddVulkanRecordedGenerationPin(commandLifetime, resource))
            return;

        if (!ResourceRuntime.Lifetime.Tracker.ResourceCommandBufferDependencies.TryGetValue(resource.Key, out HashSet<ulong>? commandBuffers))
        {
            commandBuffers = [];
            ResourceRuntime.Lifetime.Tracker.ResourceCommandBufferDependencies[resource.Key] = commandBuffers;
        }
        commandBuffers.Add(commandBufferHandle);
    }

    internal static bool AddVulkanRecordedGenerationPin(
        VulkanCommandBufferLifetimeRecord commandLifetime,
        VulkanResourceLifetimeRecord resource)
    {
        if (commandLifetime.Dependencies.TryGetValue(resource.Key, out ulong generation) &&
            generation == resource.Generation)
        {
            return false;
        }

        commandLifetime.Dependencies[resource.Key] = resource.Generation;
        resource.Pins.AddRecordedReference();
        resource.State |= EVulkanResourceLifetimeState.Recorded;
        return true;
    }

    internal static void ReleaseVulkanRecordedGenerationPin(VulkanResourceLifetimeRecord resource)
    {
        resource.Pins.ReleaseRecordedReference();
        if (!resource.Pins.HasRecordedReferences)
            resource.State &= ~EVulkanResourceLifetimeState.Recorded;
    }

    private void ReleaseVulkanCommandBufferDependencies_NoLock(
        ulong commandBufferHandle,
        VulkanCommandBufferLifetimeRecord commandLifetime)
    {
        foreach ((VulkanResourceLifetimeKey key, ulong generation) in commandLifetime.Dependencies)
        {
            if (!ResourceRuntime.Lifetime.Tracker.ResourceLifetimes.TryGetValue(key, out VulkanResourceLifetimeRecord? resource) ||
                resource.Generation != generation)
            {
                continue;
            }

            ReleaseVulkanRecordedGenerationPin(resource);

            if (ResourceRuntime.Lifetime.Tracker.ResourceCommandBufferDependencies.TryGetValue(key, out HashSet<ulong>? commandBuffers))
                commandBuffers.Remove(commandBufferHandle);
        }

        commandLifetime.Dependencies.Clear();
        commandLifetime.TouchedDependencies.Clear();
    }

    /// <summary>
    /// Records the allocation owner once. This deliberately does not add a normal
    /// command-recording dependency: those are released on reset, while a pool
    /// remains the native owner of a cached command buffer across every reset.
    /// </summary>
    private void RegisterVulkanCommandBufferPoolOwnership_NoLock(
        ulong commandBufferHandle,
        VulkanCommandBufferLifetimeRecord lifetime,
        CommandPool commandPool)
    {
        VulkanResourceLifetimeKey poolKey = ResourceKey(ObjectType.CommandPool, commandPool.Handle);
        VulkanResourceLifetimeRecord pool = GetOrRegisterVulkanResource_NoLock(
            poolKey,
            "CommandBuffer.AllocationPool");
        if ((pool.State & (EVulkanResourceLifetimeState.PendingRetirement | EVulkanResourceLifetimeState.Destroyed)) != 0)
        {
            throw new InvalidOperationException(
                $"Cannot allocate command buffer 0x{commandBufferHandle:X} from retiring command pool {poolKey} generation {pool.Generation}.");
        }

        if (lifetime.AllocatingCommandPool.IsValid &&
            (lifetime.AllocatingCommandPool != poolKey ||
             lifetime.AllocatingCommandPoolGeneration != pool.Generation))
        {
            throw new InvalidOperationException(
                $"Command buffer 0x{commandBufferHandle:X} was unexpectedly reallocated from {poolKey} generation {pool.Generation} while still owned by " +
                $"{lifetime.AllocatingCommandPool} generation {lifetime.AllocatingCommandPoolGeneration}.");
        }

        lifetime.AllocatingCommandPool = poolKey;
        lifetime.AllocatingCommandPoolGeneration = pool.Generation;
        if (!ResourceRuntime.Lifetime.Tracker.CommandBuffersByPool.TryGetValue(poolKey, out HashSet<ulong>? children))
        {
            children = [];
            ResourceRuntime.Lifetime.Tracker.CommandBuffersByPool[poolKey] = children;
        }
        children.Add(commandBufferHandle);
    }

    private bool IsVulkanCommandBufferPoolOwnershipAvailable_NoLock(
        ulong commandBufferHandle,
        VulkanCommandBufferLifetimeRecord lifetime,
        out string failureReason)
    {
        if (!lifetime.AllocatingCommandPool.IsValid)
        {
            failureReason = string.Empty;
            return true;
        }

        if (!ResourceRuntime.Lifetime.Tracker.ResourceLifetimes.TryGetValue(
                lifetime.AllocatingCommandPool,
                out VulkanResourceLifetimeRecord? pool) ||
            pool.Generation != lifetime.AllocatingCommandPoolGeneration ||
            (pool.State & (EVulkanResourceLifetimeState.PendingRetirement | EVulkanResourceLifetimeState.Destroyed)) != 0)
        {
            failureReason =
                $"Command buffer 0x{commandBufferHandle:X} cannot be recorded because its allocating command pool " +
                $"{lifetime.AllocatingCommandPool} generation {lifetime.AllocatingCommandPoolGeneration} is retiring or destroyed.";
            return false;
        }

        failureReason = string.Empty;
        return true;
    }

    private void RemoveVulkanCommandBufferPoolOwnership_NoLock(
        ulong commandBufferHandle,
        VulkanCommandBufferLifetimeRecord lifetime)
    {
        VulkanResourceLifetimeKey poolKey = lifetime.AllocatingCommandPool;
        if (poolKey.IsValid &&
            ResourceRuntime.Lifetime.Tracker.CommandBuffersByPool.TryGetValue(poolKey, out HashSet<ulong>? children))
        {
            children.Remove(commandBufferHandle);
            if (children.Count == 0)
                ResourceRuntime.Lifetime.Tracker.CommandBuffersByPool.Remove(poolKey);
        }

        lifetime.AllocatingCommandPool = default;
        lifetime.AllocatingCommandPoolGeneration = 0;
    }

    private void MergeVulkanSecondaryCommandBufferDependencies(
        CommandBuffer primary,
        ReadOnlySpan<CommandBuffer> secondaries)
    {
        if (primary.Handle == 0 || secondaries.Length == 0)
            return;

        ulong primaryHandle = unchecked((ulong)primary.Handle);
        FlushCommandBufferTrackingBatch(primary);
        for (int i = 0; i < secondaries.Length; i++)
            FlushCommandBufferTrackingBatch(secondaries[i]);
        lock (ResourceRuntime.Lifetime.Tracker.SyncRoot)
        {
            if (!ResourceRuntime.Lifetime.Tracker.CommandBufferLifetimes.TryGetValue(primaryHandle, out VulkanCommandBufferLifetimeRecord? primaryLifetime))
            {
                primaryLifetime = new VulkanCommandBufferLifetimeRecord();
                ResourceRuntime.Lifetime.Tracker.CommandBufferLifetimes[primaryHandle] = primaryLifetime;
            }

            for (int i = 0; i < secondaries.Length; i++)
            {
                ulong secondaryHandle = unchecked((ulong)secondaries[i].Handle);
                if (secondaryHandle == 0)
                    continue;

                TrackVulkanCommandBufferResource_NoLock(
                    primaryHandle,
                    ResourceKey(ObjectType.CommandBuffer, secondaryHandle),
                    "CommandBuffer.SecondaryExecution");

                if (!ResourceRuntime.Lifetime.Tracker.CommandBufferLifetimes.TryGetValue(secondaryHandle, out VulkanCommandBufferLifetimeRecord? secondaryLifetime))
                    continue;

                foreach ((VulkanResourceLifetimeKey key, ulong generation) in secondaryLifetime.Dependencies)
                {
                    if (ResourceRuntime.Lifetime.Tracker.ResourceLifetimes.TryGetValue(key, out VulkanResourceLifetimeRecord? resource) &&
                        resource.Generation == generation)
                    {
                        AddVulkanCommandBufferDependency_NoLock(primaryHandle, primaryLifetime, resource);
                    }
                }
            }

            primaryLifetime.RefreshTouchedDependencies();
        }
    }

    private void CmdExecuteCommandsTracked(
        CommandBuffer primary,
        uint commandBufferCount,
        CommandBuffer* secondaryCommandBuffers)
    {
        if (commandBufferCount == 0 || secondaryCommandBuffers is null)
            return;

        ReadOnlySpan<CommandBuffer> secondaries = new(secondaryCommandBuffers, checked((int)commandBufferCount));
        MergeVulkanSecondaryCommandBufferDependencies(primary, secondaries);
        MergeRecordedImageLayoutStates(primary, secondaries);
        Api!.CmdExecuteCommands(primary, commandBufferCount, secondaryCommandBuffers);
        InvalidatePrimaryBindStateAfterSecondaryExecution(primary);
        RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanExecuteSecondaryCommandBuffers(
            commandBufferCount);
    }

    private void CmdBeginRenderPassTracked(
        CommandBuffer commandBuffer,
        RenderPassBeginInfo* beginInfo,
        SubpassContents contents)
    {
        if (beginInfo is not null)
        {
            TrackVulkanCommandBufferResource(
                commandBuffer,
                ObjectType.RenderPass,
                beginInfo->RenderPass.Handle,
                "RenderPass.Begin");
            TrackVulkanCommandBufferResource(
                commandBuffer,
                ObjectType.Framebuffer,
                beginInfo->Framebuffer.Handle,
                "Framebuffer.BeginRenderPass");
        }

        Api!.CmdBeginRenderPass(commandBuffer, beginInfo, contents);
    }

    private Result AllocateVulkanCommandBuffersTracked(
        ref CommandBufferAllocateInfo allocateInfo,
        CommandBuffer* commandBuffers,
        string owner = "CommandBuffer.Allocation")
    {
        ThrowIfVulkanDeviceOperationNotAdmitted("vkAllocateCommandBuffers." + owner);
        // Native allocation and persistent pool-child registration are one atomic
        // operation with respect to command-pool retirement.
        lock (CommandPoolsGate)
        {
            Result result = AllocateCommandBuffersHostSynchronized(ref allocateInfo, commandBuffers);
            RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanAllocateCommandBuffersCall(
                allocateInfo.CommandBufferCount,
                result == Result.Success);
            if (result != Result.Success || commandBuffers is null)
                return result;

            for (int i = 0; i < allocateInfo.CommandBufferCount; i++)
            {
                CommandBuffer commandBuffer = commandBuffers[i];
                ulong commandBufferHandle = unchecked((ulong)commandBuffer.Handle);
                RegisterVulkanResource(
                    ObjectType.CommandBuffer,
                    commandBufferHandle,
                    owner);

                lock (ResourceRuntime.Lifetime.Tracker.SyncRoot)
                {
                    if (!ResourceRuntime.Lifetime.Tracker.CommandBufferLifetimes.TryGetValue(
                            commandBufferHandle,
                            out VulkanCommandBufferLifetimeRecord? lifetime))
                    {
                        lifetime = new VulkanCommandBufferLifetimeRecord();
                        ResourceRuntime.Lifetime.Tracker.CommandBufferLifetimes[commandBufferHandle] = lifetime;
                    }
                    lifetime.Level = allocateInfo.Level;
                    RegisterVulkanCommandBufferPoolOwnership_NoLock(
                        commandBufferHandle,
                        lifetime,
                        allocateInfo.CommandPool);
                }
            }

            return result;
        }
    }

    private Result AllocateVulkanCommandBuffersTracked(
        ref CommandBufferAllocateInfo allocateInfo,
        out CommandBuffer commandBuffer,
        string owner = "CommandBuffer.Allocation")
    {
        commandBuffer = default;
        fixed (CommandBuffer* commandBufferPtr = &commandBuffer)
            return AllocateVulkanCommandBuffersTracked(ref allocateInfo, commandBufferPtr, owner);
    }

    private void FreeVulkanCommandBuffersTracked(
        CommandPool commandPool,
        uint commandBufferCount,
        CommandBuffer* commandBuffers,
        string owner)
    {
        if (commandPool.Handle == 0 || commandBufferCount == 0 || commandBuffers is null)
            return;

        lock (CommandPoolsGate)
        {
            for (int i = 0; i < commandBufferCount; i++)
            {
                CommandBuffer commandBuffer = commandBuffers[i];
                if (commandBuffer.Handle == 0)
                    continue;

                ulong handle = unchecked((ulong)commandBuffer.Handle);
                VulkanRetirementTicket ticket = CaptureVulkanRetirementTicket(
                    ObjectType.CommandBuffer,
                    handle,
                    owner);
                if (!IsVulkanCommandBufferRetirementReady(commandBuffer, ticket))
                {
                    RetireCommandBuffer(commandPool, commandBuffer);
                    commandBuffers[i] = default;
                    continue;
                }

                FreeCommandBuffersHostSynchronized(commandPool, 1, &commandBuffer);
                RemoveCommandBufferBindState(commandBuffers[i]);
                CompleteVulkanResourceDestruction(ObjectType.CommandBuffer, handle);
                commandBuffers[i] = default;
            }
        }
    }

    private void FreeVulkanCommandBufferTracked(
        CommandPool commandPool,
        ref CommandBuffer commandBuffer,
        string owner)
    {
        fixed (CommandBuffer* commandBufferPtr = &commandBuffer)
            FreeVulkanCommandBuffersTracked(commandPool, 1, commandBufferPtr, owner);
    }

    private void CmdCopyBufferTracked(
        CommandBuffer commandBuffer,
        Silk.NET.Vulkan.Buffer source,
        Silk.NET.Vulkan.Buffer destination,
        uint regionCount,
        BufferCopy* regions)
    {
        TrackVulkanCommandBufferResource(commandBuffer, ObjectType.Buffer, source.Handle, "CopyBuffer.Source");
        TrackVulkanCommandBufferResource(commandBuffer, ObjectType.Buffer, destination.Handle, "CopyBuffer.Destination");
        Api!.CmdCopyBuffer(commandBuffer, source, destination, regionCount, regions);
    }

    internal void CmdCopyBufferToImageTracked(
        CommandBuffer commandBuffer,
        Silk.NET.Vulkan.Buffer source,
        Image destination,
        ImageLayout destinationLayout,
        uint regionCount,
        BufferImageCopy* regions)
    {
        TrackVulkanCommandBufferResource(commandBuffer, ObjectType.Buffer, source.Handle, "CopyBufferToImage.Source");
        TrackVulkanCommandBufferResource(commandBuffer, ObjectType.Image, destination.Handle, "CopyBufferToImage.Destination");
        Api!.CmdCopyBufferToImage(commandBuffer, source, destination, destinationLayout, regionCount, regions);
    }

    internal void CmdCopyBufferToImageTracked(
        CommandBuffer commandBuffer,
        Silk.NET.Vulkan.Buffer source,
        Image destination,
        ImageLayout destinationLayout,
        uint regionCount,
        ref BufferImageCopy region)
    {
        TrackVulkanCommandBufferResource(commandBuffer, ObjectType.Buffer, source.Handle, "CopyBufferToImage.Source");
        TrackVulkanCommandBufferResource(commandBuffer, ObjectType.Image, destination.Handle, "CopyBufferToImage.Destination");
        Api!.CmdCopyBufferToImage(commandBuffer, source, destination, destinationLayout, regionCount, ref region);
    }

    private void CmdCopyImageToBufferTracked(
        CommandBuffer commandBuffer,
        Image source,
        ImageLayout sourceLayout,
        Silk.NET.Vulkan.Buffer destination,
        uint regionCount,
        BufferImageCopy* regions)
    {
        TrackVulkanCommandBufferResource(commandBuffer, ObjectType.Image, source.Handle, "CopyImageToBuffer.Source");
        TrackVulkanCommandBufferResource(commandBuffer, ObjectType.Buffer, destination.Handle, "CopyImageToBuffer.Destination");
        Api!.CmdCopyImageToBuffer(commandBuffer, source, sourceLayout, destination, regionCount, regions);
    }

    private void CmdCopyImageTracked(
        CommandBuffer commandBuffer,
        Image source,
        ImageLayout sourceLayout,
        Image destination,
        ImageLayout destinationLayout,
        uint regionCount,
        ImageCopy* regions)
    {
        TrackVulkanCommandBufferResource(commandBuffer, ObjectType.Image, source.Handle, "CopyImage.Source");
        TrackVulkanCommandBufferResource(commandBuffer, ObjectType.Image, destination.Handle, "CopyImage.Destination");
        Api!.CmdCopyImage(
            commandBuffer,
            source,
            sourceLayout,
            destination,
            destinationLayout,
            regionCount,
            regions);
    }

    private void CmdResolveImageTracked(
        CommandBuffer commandBuffer,
        Image source,
        ImageLayout sourceLayout,
        Image destination,
        ImageLayout destinationLayout,
        uint regionCount,
        ImageResolve* regions)
    {
        TrackVulkanCommandBufferResource(commandBuffer, ObjectType.Image, source.Handle, "ResolveImage.Source");
        TrackVulkanCommandBufferResource(commandBuffer, ObjectType.Image, destination.Handle, "ResolveImage.Destination");
        Api!.CmdResolveImage(
            commandBuffer,
            source,
            sourceLayout,
            destination,
            destinationLayout,
            regionCount,
            regions);
    }

    internal void CmdBlitImageTracked(
        CommandBuffer commandBuffer,
        Image source,
        ImageLayout sourceLayout,
        Image destination,
        ImageLayout destinationLayout,
        uint regionCount,
        ImageBlit* regions,
        Filter filter)
    {
        if (SynchronizationThreadContext.ExcludeDesktopSwapchainBarriers &&
            (IsDesktopSwapchainImage(source) || IsDesktopSwapchainImage(destination)))
        {
            return;
        }

        TrackVulkanCommandBufferResource(commandBuffer, ObjectType.Image, source.Handle, "BlitImage.Source");
        TrackVulkanCommandBufferResource(commandBuffer, ObjectType.Image, destination.Handle, "BlitImage.Destination");
        Api!.CmdBlitImage(
            commandBuffer,
            source,
            sourceLayout,
            destination,
            destinationLayout,
            regionCount,
            regions,
            filter);
    }

    internal void CmdBlitImageTracked(
        CommandBuffer commandBuffer,
        Image source,
        ImageLayout sourceLayout,
        Image destination,
        ImageLayout destinationLayout,
        uint regionCount,
        ref ImageBlit region,
        Filter filter)
    {
        if (SynchronizationThreadContext.ExcludeDesktopSwapchainBarriers &&
            (IsDesktopSwapchainImage(source) || IsDesktopSwapchainImage(destination)))
        {
            return;
        }

        TrackVulkanCommandBufferResource(commandBuffer, ObjectType.Image, source.Handle, "BlitImage.Source");
        TrackVulkanCommandBufferResource(commandBuffer, ObjectType.Image, destination.Handle, "BlitImage.Destination");
        Api!.CmdBlitImage(
            commandBuffer,
            source,
            sourceLayout,
            destination,
            destinationLayout,
            regionCount,
            ref region,
            filter);
    }

    internal void CmdClearColorImageTracked(
        CommandBuffer commandBuffer,
        Image image,
        ImageLayout imageLayout,
        ref ClearColorValue clearValue,
        uint rangeCount,
        ref ImageSubresourceRange ranges)
    {
        TrackVulkanCommandBufferResource(commandBuffer, ObjectType.Image, image.Handle, "ClearColorImage");
        Api!.CmdClearColorImage(
            commandBuffer,
            image,
            imageLayout,
            ref clearValue,
            rangeCount,
            ref ranges);
    }

    internal void CmdClearDepthStencilImageTracked(
        CommandBuffer commandBuffer,
        Image image,
        ImageLayout imageLayout,
        ref ClearDepthStencilValue clearValue,
        uint rangeCount,
        ref ImageSubresourceRange ranges)
    {
        TrackVulkanCommandBufferResource(commandBuffer, ObjectType.Image, image.Handle, "ClearDepthStencilImage");
        Api!.CmdClearDepthStencilImage(
            commandBuffer,
            image,
            imageLayout,
            ref clearValue,
            rangeCount,
            ref ranges);
    }

    internal void RegisterVulkanDescriptorSet(
        DescriptorPool pool,
        DescriptorSet descriptorSet,
        bool usesUpdateAfterBind,
        string owner,
        uint setIndex = 0,
        IReadOnlyList<DescriptorBindingInfo>? reflectedBindings = null)
    {
        if (descriptorSet.Handle == 0)
            return;

        RegisterVulkanResource(ObjectType.DescriptorPool, pool.Handle, $"{owner}.Pool");
        RegisterVulkanResource(ObjectType.DescriptorSet, descriptorSet.Handle, owner);
        lock (ResourceRuntime.Lifetime.Tracker.SyncRoot)
        {
            if (!ResourceRuntime.Lifetime.Tracker.DescriptorSetLifetimes.TryGetValue(descriptorSet.Handle, out VulkanDescriptorSetLifetimeRecord? state))
            {
                state = new VulkanDescriptorSetLifetimeRecord();
                ResourceRuntime.Lifetime.Tracker.DescriptorSetLifetimes[descriptorSet.Handle] = state;
            }

            UpdateVulkanDescriptorSetPoolIndex_NoLock(descriptorSet.Handle, state.Pool.Handle, pool.Handle);
            state.Pool = pool;
            state.UsesUpdateAfterBind = usesUpdateAfterBind;
            state.HasReflection = reflectedBindings is not null;
            state.ReflectedImageBindings.Clear();
            if (reflectedBindings is not null)
            {
                for (int i = 0; i < reflectedBindings.Count; i++)
                {
                    DescriptorBindingInfo binding = reflectedBindings[i];
                    if (binding.Set == setIndex && IsLifetimeTrackedImageDescriptorType(binding.DescriptorType))
                        state.ReflectedImageBindings.Add(binding.Binding);
                }
            }

            state.Generation++;
            PublishVulkanDescriptorSetSnapshot_NoLock(descriptorSet.Handle, state);
        }
    }

    private void PublishVulkanDescriptorSetSnapshot_NoLock(
        ulong descriptorSetHandle,
        VulkanDescriptorSetLifetimeRecord state)
    {
        HashSet<VulkanResourceLifetimeKey> uniqueReferences = ResourceRuntime.Lifetime.Tracker.DescriptorReferencesScratch.Value!;
        HashSet<VulkanResourceLifetimeKey> pinnedReferences = ResourceRuntime.Lifetime.Tracker.DescriptorPinnedReferencesScratch.Value!;
        uniqueReferences.Clear();
        pinnedReferences.Clear();
        try
        {
            foreach (VulkanDescriptorReferencePair pair in state.References.Values)
            {
                if (pair.First.IsValid)
                    uniqueReferences.Add(pair.First);
                if (pair.Second.IsValid)
                    uniqueReferences.Add(pair.Second);
            }

            UpdateVulkanDescriptorSetReferenceIndex_NoLock(descriptorSetHandle, state, uniqueReferences);
            foreach (VulkanResourceLifetimeKey key in uniqueReferences)
                AddVulkanDescriptorPinnedReferenceClosure_NoLock(key, pinnedReferences);
            UpdateVulkanDescriptorSetGenerationPins_NoLock(state, pinnedReferences);

            VulkanPublishedDescriptorImageReference[] imageReferences = state.ImageReferences.Count == 0
                ? []
                : new VulkanPublishedDescriptorImageReference[state.ImageReferences.Count];
            int imageIndex = 0;
            foreach (((uint binding, uint element), VulkanDescriptorImageReference reference) in state.ImageReferences)
                imageReferences[imageIndex++] = new VulkanPublishedDescriptorImageReference(binding, element, reference);

            VulkanResourceLifetimeKey[] publishedReferences = uniqueReferences.Count == 0
                ? []
                : new VulkanResourceLifetimeKey[uniqueReferences.Count];
            uniqueReferences.CopyTo(publishedReferences);
            uint[] reflectedImageBindings = state.ReflectedImageBindings.Count == 0
                ? []
                : new uint[state.ReflectedImageBindings.Count];
            state.ReflectedImageBindings.CopyTo(reflectedImageBindings);

            ResourceRuntime.Lifetime.Tracker.PublishedDescriptorSets[descriptorSetHandle] = new VulkanPublishedDescriptorSetSnapshot(
                state.Generation,
                publishedReferences,
                imageReferences,
                reflectedImageBindings,
                state.HasReflection);
        }
        finally
        {
            uniqueReferences.Clear();
            pinnedReferences.Clear();
        }
    }

    private void AddVulkanDescriptorPinnedReferenceClosure_NoLock(
        VulkanResourceLifetimeKey key,
        HashSet<VulkanResourceLifetimeKey> pinnedReferences)
    {
        if (!key.IsValid || !pinnedReferences.Add(key))
            return;

        if (key.Type == ObjectType.ImageView &&
            ResourceRuntime.Lifetime.Tracker.ImageViewBackingImages.TryGetValue(key.Handle, out ulong backingImageHandle) &&
            backingImageHandle != 0)
        {
            pinnedReferences.Add(ResourceKey(ObjectType.Image, backingImageHandle));
        }

        if (key.Type == ObjectType.BufferView &&
            ResourceRuntime.Lifetime.Tracker.BufferViewBackingBuffers.TryGetValue(key.Handle, out ulong backingBufferHandle) &&
            backingBufferHandle != 0)
        {
            pinnedReferences.Add(ResourceKey(ObjectType.Buffer, backingBufferHandle));
        }
    }

    private void UpdateVulkanDescriptorSetGenerationPins_NoLock(
        VulkanDescriptorSetLifetimeRecord state,
        HashSet<VulkanResourceLifetimeKey> currentReferences)
    {
        ReleaseVulkanDescriptorSetGenerationPins_NoLock(state);
        foreach (VulkanResourceLifetimeKey key in currentReferences)
        {
            if (!ResourceRuntime.Lifetime.Tracker.ResourceLifetimes.TryGetValue(
                    key,
                    out VulkanResourceLifetimeRecord? resource) ||
                (resource.State & EVulkanResourceLifetimeState.Destroyed) != 0)
            {
                continue;
            }

            resource.Pins.AddDescriptorReference();
            state.PinnedReferences[key] = resource.Generation;
        }
    }

    private void ReleaseVulkanDescriptorSetGenerationPins_NoLock(
        VulkanDescriptorSetLifetimeRecord state)
    {
        foreach ((VulkanResourceLifetimeKey key, ulong generation) in state.PinnedReferences)
        {
            if (ResourceRuntime.Lifetime.Tracker.ResourceLifetimes.TryGetValue(
                    key,
                    out VulkanResourceLifetimeRecord? resource) &&
                resource.Generation == generation)
            {
                resource.Pins.ReleaseDescriptorReference();
            }
        }

        state.PinnedReferences.Clear();
    }

    private void UpdateVulkanDescriptorSetPoolIndex_NoLock(
        ulong descriptorSetHandle,
        ulong previousPoolHandle,
        ulong poolHandle)
    {
        if (previousPoolHandle == poolHandle)
            return;

        if (previousPoolHandle != 0 &&
            ResourceRuntime.Lifetime.Tracker.DescriptorSetsByPool.TryGetValue(previousPoolHandle, out HashSet<ulong>? previousSets))
        {
            previousSets.Remove(descriptorSetHandle);
            if (previousSets.Count == 0)
                ResourceRuntime.Lifetime.Tracker.DescriptorSetsByPool.Remove(previousPoolHandle);
        }

        if (poolHandle == 0)
            return;

        if (!ResourceRuntime.Lifetime.Tracker.DescriptorSetsByPool.TryGetValue(poolHandle, out HashSet<ulong>? ownedSets))
        {
            ownedSets = [];
            ResourceRuntime.Lifetime.Tracker.DescriptorSetsByPool[poolHandle] = ownedSets;
        }

        ownedSets.Add(descriptorSetHandle);
    }

    private void UpdateVulkanDescriptorSetReferenceIndex_NoLock(
        ulong descriptorSetHandle,
        VulkanDescriptorSetLifetimeRecord state,
        HashSet<VulkanResourceLifetimeKey> currentReferences)
    {
        foreach (VulkanResourceLifetimeKey previousReference in state.IndexedReferences)
        {
            if (currentReferences.Contains(previousReference) ||
                !ResourceRuntime.Lifetime.Tracker.DescriptorSetsByReferencedResource.TryGetValue(previousReference, out HashSet<ulong>? sets))
            {
                continue;
            }

            sets.Remove(descriptorSetHandle);
            if (sets.Count == 0)
                ResourceRuntime.Lifetime.Tracker.DescriptorSetsByReferencedResource.Remove(previousReference);
        }

        foreach (VulkanResourceLifetimeKey currentReference in currentReferences)
        {
            if (state.IndexedReferences.Contains(currentReference))
                continue;

            if (!ResourceRuntime.Lifetime.Tracker.DescriptorSetsByReferencedResource.TryGetValue(currentReference, out HashSet<ulong>? sets))
            {
                sets = [];
                ResourceRuntime.Lifetime.Tracker.DescriptorSetsByReferencedResource[currentReference] = sets;
            }

            sets.Add(descriptorSetHandle);
        }

        state.IndexedReferences.Clear();
        foreach (VulkanResourceLifetimeKey currentReference in currentReferences)
            state.IndexedReferences.Add(currentReference);
    }

    internal void RegisterVulkanDescriptorSets(
        DescriptorPool pool,
        ReadOnlySpan<DescriptorSet> descriptorSets,
        bool usesUpdateAfterBind,
        string owner,
        IReadOnlyList<DescriptorBindingInfo>? reflectedBindings = null)
    {
        for (int i = 0; i < descriptorSets.Length; i++)
            RegisterVulkanDescriptorSet(
                pool,
                descriptorSets[i],
                usesUpdateAfterBind,
                owner,
                unchecked((uint)i),
                reflectedBindings);
    }

    private void ValidateAndRecordVulkanDescriptorWrites(uint writeCount, WriteDescriptorSet* writes)
    {
        if (writeCount == 0 || writes is null)
            return;

        HashSet<ulong> changedSets = ResourceRuntime.Lifetime.Tracker.ChangedDescriptorSetsScratch.Value!;
        changedSets.Clear();
        try
        {
            lock (ResourceRuntime.Lifetime.Tracker.SyncRoot)
            {
                for (int writeIndex = 0; writeIndex < writeCount; writeIndex++)
                {
                    WriteDescriptorSet write = writes[writeIndex];
                    if (write.DstSet.Handle == 0)
                        continue;

                    VulkanResourceLifetimeKey setKey = ResourceKey(ObjectType.DescriptorSet, write.DstSet.Handle);
                    VulkanResourceLifetimeRecord setResource = GetOrRegisterVulkanResource_NoLock(setKey, "DescriptorSet.Update");
                    if ((setResource.State & (EVulkanResourceLifetimeState.PendingRetirement | EVulkanResourceLifetimeState.Destroyed)) != 0)
                        throw new InvalidOperationException($"Cannot update retired Vulkan descriptor set {setKey}.");

                    if (!ResourceRuntime.Lifetime.Tracker.DescriptorSetLifetimes.TryGetValue(write.DstSet.Handle, out VulkanDescriptorSetLifetimeRecord? setState))
                    {
                        setState = new VulkanDescriptorSetLifetimeRecord();
                        ResourceRuntime.Lifetime.Tracker.DescriptorSetLifetimes[write.DstSet.Handle] = setState;
                    }

                    bool setUseCompleted = UpdateVulkanResourceCompletionState_NoLock(setResource);
                    bool bindingSupportsUpdateAfterBind =
                        setState.UsesUpdateAfterBind && CanUseUpdateAfterBind(write.DescriptorType);
                    if (!setUseCompleted && !bindingSupportsUpdateAfterBind)
                    {
                        throw new InvalidOperationException(
                            $"Cannot update in-flight Vulkan descriptor set {setKey}; binding={write.DstBinding} type={write.DescriptorType} was not registered for update-after-bind.");
                    }

                    for (uint descriptorIndex = 0; descriptorIndex < write.DescriptorCount; descriptorIndex++)
                    {
                        (uint Binding, uint Element) bindingKey =
                            (write.DstBinding, write.DstArrayElement + descriptorIndex);
                        VulkanDescriptorReferencePair references = ResolveDescriptorReferences(write, descriptorIndex);
                        ValidateAndPropagateVulkanDescriptorReference_NoLock(
                            setKey,
                            setResource,
                            references.First,
                            setUseCompleted);
                        ValidateAndPropagateVulkanDescriptorReference_NoLock(
                            setKey,
                            setResource,
                            references.Second,
                            setUseCompleted);
                        if (!setState.References.TryGetValue(bindingKey, out VulkanDescriptorReferencePair previousReferences) ||
                            previousReferences != references)
                        {
                            setState.References[bindingKey] = references;
                            changedSets.Add(write.DstSet.Handle);
                        }
                        if (write.PImageInfo is not null && IsLifetimeTrackedImageDescriptorType(write.DescriptorType))
                        {
                            DescriptorImageInfo imageInfo = write.PImageInfo[descriptorIndex];
                            VulkanDescriptorImageReference imageReference = new(
                                imageInfo.ImageView,
                                imageInfo.ImageLayout,
                                write.DescriptorType);
                            if (!setState.ImageReferences.TryGetValue(bindingKey, out VulkanDescriptorImageReference previousImage) ||
                                previousImage != imageReference)
                            {
                                setState.ImageReferences[bindingKey] = imageReference;
                                changedSets.Add(write.DstSet.Handle);
                            }
                        }
                        else if (setState.ImageReferences.Remove(bindingKey))
                        {
                            changedSets.Add(write.DstSet.Handle);
                        }
                    }
                }

                foreach (ulong descriptorSetHandle in changedSets)
                {
                    VulkanDescriptorSetLifetimeRecord state = ResourceRuntime.Lifetime.Tracker.DescriptorSetLifetimes[descriptorSetHandle];
                    state.Generation++;
                    PublishVulkanDescriptorSetSnapshot_NoLock(descriptorSetHandle, state);
                }
            }
        }
        finally
        {
            changedSets.Clear();
        }
    }

    /// <summary>
    /// Performs the non-mutating portion of descriptor lifetime validation while the caller owns
    /// <see cref="ResourceRuntime.Lifetime.Tracker.SyncRoot"/>. Recoverable generation-retirement races use this
    /// path so they can rebuild descriptor snapshots without throwing first-chance exceptions.
    /// </summary>
    private bool TryPrevalidateVulkanDescriptorWrites_NoLock(
        uint writeCount,
        WriteDescriptorSet* writes,
        out string failureReason)
    {
        failureReason = string.Empty;
        if (writeCount == 0 || writes is null)
            return true;

        for (int writeIndex = 0; writeIndex < writeCount; writeIndex++)
        {
            WriteDescriptorSet write = writes[writeIndex];
            if (write.DstSet.Handle == 0)
                continue;

            VulkanResourceLifetimeKey setKey = ResourceKey(ObjectType.DescriptorSet, write.DstSet.Handle);
            VulkanResourceLifetimeRecord setResource = GetOrRegisterVulkanResource_NoLock(setKey, "DescriptorSet.Update.Prevalidate");
            if ((setResource.State & (EVulkanResourceLifetimeState.PendingRetirement | EVulkanResourceLifetimeState.Destroyed)) != 0)
            {
                failureReason = $"Cannot update retired Vulkan descriptor set {setKey}.";
                return false;
            }

            bool setUseCompleted = UpdateVulkanResourceCompletionState_NoLock(setResource);
            bool usesUpdateAfterBind =
                ResourceRuntime.Lifetime.Tracker.DescriptorSetLifetimes.TryGetValue(write.DstSet.Handle, out VulkanDescriptorSetLifetimeRecord? setState) &&
                setState.UsesUpdateAfterBind &&
                CanUseUpdateAfterBind(write.DescriptorType);
            if (!setUseCompleted && !usesUpdateAfterBind)
            {
                failureReason =
                    $"Cannot update in-flight Vulkan descriptor set {setKey}; binding={write.DstBinding} type={write.DescriptorType} was not registered for update-after-bind.";
                return false;
            }

            for (uint descriptorIndex = 0; descriptorIndex < write.DescriptorCount; descriptorIndex++)
            {
                VulkanDescriptorReferencePair references = ResolveDescriptorReferences(write, descriptorIndex);
                if (!TryPrevalidateVulkanDescriptorReference_NoLock(setKey, references.First, out failureReason) ||
                    !TryPrevalidateVulkanDescriptorReference_NoLock(setKey, references.Second, out failureReason))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private bool TryPrevalidateVulkanDescriptorReference_NoLock(
        VulkanResourceLifetimeKey setKey,
        VulkanResourceLifetimeKey referenceKey,
        out string failureReason)
    {
        failureReason = string.Empty;
        if (!referenceKey.IsValid)
            return true;

        VulkanResourceLifetimeRecord reference = GetOrRegisterVulkanResource_NoLock(
            referenceKey,
            "DescriptorSet.Reference.Prevalidate");
        if ((reference.State & (EVulkanResourceLifetimeState.PendingRetirement | EVulkanResourceLifetimeState.Destroyed)) == 0)
            return true;

        failureReason =
            $"Cannot update descriptor set {setKey} with retired Vulkan resource {referenceKey} generation {reference.Generation}.";
        return false;
    }

    private void ValidateAndPropagateVulkanDescriptorReference_NoLock(
        VulkanResourceLifetimeKey setKey,
        VulkanResourceLifetimeRecord setResource,
        VulkanResourceLifetimeKey referenceKey,
        bool setUseCompleted)
    {
        if (!referenceKey.IsValid)
            return;

        VulkanResourceLifetimeRecord reference = GetOrRegisterVulkanResource_NoLock(
            referenceKey,
            "DescriptorSet.Reference");
        if ((reference.State & (EVulkanResourceLifetimeState.PendingRetirement | EVulkanResourceLifetimeState.Destroyed)) != 0)
        {
            throw new InvalidOperationException(
                $"Cannot update descriptor set {setKey} with retired Vulkan resource {referenceKey} generation {reference.Generation}.");
        }

        if (!setUseCompleted)
            PropagateVulkanDescriptorSetSubmission_NoLock(setResource, reference);

    }

    private static void PropagateVulkanDescriptorSetSubmission_NoLock(
        VulkanResourceLifetimeRecord descriptorSet,
        VulkanResourceLifetimeRecord reference)
    {
        reference.Pins.MergeSubmitted(in descriptorSet.Pins);
        reference.LastSubmissionSerial = Math.Max(reference.LastSubmissionSerial, descriptorSet.LastSubmissionSerial);
        reference.LastFrameOpContextId = descriptorSet.LastFrameOpContextId;
        reference.LastFrameOpKind = descriptorSet.LastFrameOpKind;
        reference.State &= ~EVulkanResourceLifetimeState.Completed;
        reference.State |= EVulkanResourceLifetimeState.Submitted;
    }

    private static VulkanDescriptorReferencePair ResolveDescriptorReferences(
        in WriteDescriptorSet write,
        uint descriptorIndex)
    {
        switch (write.DescriptorType)
        {
            case DescriptorType.Sampler:
            case DescriptorType.CombinedImageSampler:
            case DescriptorType.SampledImage:
            case DescriptorType.StorageImage:
            case DescriptorType.InputAttachment:
                if (write.PImageInfo is not null)
                {
                    DescriptorImageInfo info = write.PImageInfo[descriptorIndex];
                    return new VulkanDescriptorReferencePair(
                        ResourceKey(ObjectType.ImageView, info.ImageView.Handle),
                        ResourceKey(ObjectType.Sampler, info.Sampler.Handle));
                }
                break;

            case DescriptorType.UniformBuffer:
            case DescriptorType.StorageBuffer:
            case DescriptorType.UniformBufferDynamic:
            case DescriptorType.StorageBufferDynamic:
                if (write.PBufferInfo is not null)
                {
                    DescriptorBufferInfo info = write.PBufferInfo[descriptorIndex];
                    return new VulkanDescriptorReferencePair(
                        ResourceKey(ObjectType.Buffer, info.Buffer.Handle),
                        default);
                }
                break;

            case DescriptorType.UniformTexelBuffer:
            case DescriptorType.StorageTexelBuffer:
                if (write.PTexelBufferView is not null)
                {
                    return new VulkanDescriptorReferencePair(
                        ResourceKey(ObjectType.BufferView, write.PTexelBufferView[descriptorIndex].Handle),
                        default);
                }
                break;
        }

        return default;
    }

    private static bool IsLifetimeTrackedImageDescriptorType(DescriptorType type)
        => type is DescriptorType.CombinedImageSampler
            or DescriptorType.SampledImage
            or DescriptorType.StorageImage
            or DescriptorType.InputAttachment;

    private bool TryAcquireVulkanSubmissionGatewayPins(
        ref SubmitInfo submitInfo,
        in VulkanSubmissionDiagnosticContext diagnosticContext,
        out string failureReason)
    {
        lock (ResourceRuntime.Lifetime.Tracker.SyncRoot)
        {
            for (int commandIndex = 0; commandIndex < submitInfo.CommandBufferCount; commandIndex++)
            {
                ulong commandBufferHandle = unchecked((ulong)submitInfo.PCommandBuffers[commandIndex].Handle);
                if (commandBufferHandle == 0)
                    continue;

                bool duplicateInSubmission = false;
                for (int previousIndex = 0; previousIndex < commandIndex; previousIndex++)
                {
                    if (unchecked((ulong)submitInfo.PCommandBuffers[previousIndex].Handle) == commandBufferHandle)
                    {
                        duplicateInSubmission = true;
                        break;
                    }
                }

                VulkanResourceLifetimeKey commandBufferKey =
                    ResourceKey(ObjectType.CommandBuffer, commandBufferHandle);
                ResourceRuntime.Lifetime.Tracker.ResourceLifetimes.TryGetValue(
                    commandBufferKey,
                    out VulkanResourceLifetimeRecord? commandResource);
                bool lifetimeAlreadyQueued =
                    ResourceRuntime.Lifetime.Tracker.CommandBufferLifetimes.TryGetValue(
                        commandBufferHandle,
                        out VulkanCommandBufferLifetimeRecord? lifetime) &&
                    lifetime.QueuedSubmissionCount != 0;
                bool batchAlreadyQueued = false;
                if (_commandBufferTrackingBatches.TryGetValue(
                        commandBufferHandle,
                        out VulkanCommandBufferTrackingBatch? batch))
                {
                    lock (batch)
                        batchAlreadyQueued = batch.QueuedSubmissionCount != 0;
                }
                if (!duplicateInSubmission && !lifetimeAlreadyQueued && !batchAlreadyQueued)
                    continue;

                failureReason = DescribeVulkanLifetimeRejection(
                    commandBufferKey,
                    commandResource?.Owner ?? "<untracked>",
                    commandResource?.Generation ?? 0,
                    commandResource?.Generation ?? 0,
                    diagnosticContext.OutputTargetName,
                    commandBufferHandle,
                    commandResource?.RetirementTicket ?? VulkanRetirementTicket.None,
                    commandResource?.State ?? EVulkanResourceLifetimeState.None,
                    duplicateInSubmission
                        ? "submission contains the same command buffer more than once"
                        : "command buffer already occupies the validation-to-queue-dispatch gateway");
                return false;
            }

            for (int commandIndex = 0; commandIndex < submitInfo.CommandBufferCount; commandIndex++)
            {
                ulong commandBufferHandle = unchecked((ulong)submitInfo.PCommandBuffers[commandIndex].Handle);
                if (commandBufferHandle == 0)
                    continue;

                if (!ResourceRuntime.Lifetime.Tracker.CommandBufferLifetimes.TryGetValue(
                        commandBufferHandle,
                        out VulkanCommandBufferLifetimeRecord? lifetime))
                {
                    lifetime = new VulkanCommandBufferLifetimeRecord();
                    ResourceRuntime.Lifetime.Tracker.CommandBufferLifetimes[commandBufferHandle] = lifetime;
                }

                lifetime.QueuedSubmissionCount++;
                if (_commandBufferTrackingBatches.TryGetValue(
                        commandBufferHandle,
                        out VulkanCommandBufferTrackingBatch? batch))
                {
                    lock (batch)
                    {
                        batch.IsRecording = false;
                        batch.QueuedSubmissionCount++;
                    }
                }
            }
        }

        failureReason = string.Empty;
        return true;
    }

    private bool IsSecondaryVulkanCommandBuffer(CommandBuffer commandBuffer)
    {
        if (commandBuffer.Handle == 0)
            return false;

        ulong commandBufferHandle = unchecked((ulong)commandBuffer.Handle);
        lock (ResourceRuntime.Lifetime.Tracker.SyncRoot)
        {
            return ResourceRuntime.Lifetime.Tracker.CommandBufferLifetimes.TryGetValue(
                    commandBufferHandle,
                    out VulkanCommandBufferLifetimeRecord? lifetime) &&
                lifetime.Level == CommandBufferLevel.Secondary;
        }
    }

    private void TrackVulkanDescriptorSetBinding(CommandBuffer commandBuffer, DescriptorSet descriptorSet)
    {
        if (commandBuffer.Handle == 0 || descriptorSet.Handle == 0)
            return;

        ulong commandBufferHandle = unchecked((ulong)commandBuffer.Handle);
        TrackVulkanCommandBufferResource(
            commandBuffer,
            ObjectType.DescriptorSet,
            descriptorSet.Handle,
            "DescriptorSet.Bind");
        bool isSecondaryCommandBuffer = IsSecondaryVulkanCommandBuffer(commandBuffer);
        VulkanPublishedDescriptorSetSnapshot? snapshotToValidate = null;
        bool expandedSnapshot = false;
        bool usedTrackingBatch = false;
        if (_commandBufferTrackingBatches.TryGetValue(
                commandBufferHandle,
                out VulkanCommandBufferTrackingBatch? batch) &&
            ResourceRuntime.Lifetime.Tracker.PublishedDescriptorSets.TryGetValue(
                descriptorSet.Handle,
                out VulkanPublishedDescriptorSetSnapshot? snapshot))
        {
            lock (batch)
            {
                if (_commandBufferTrackingBatches.TryGetValue(
                        commandBufferHandle,
                        out VulkanCommandBufferTrackingBatch? currentBatch) &&
                    ReferenceEquals(batch, currentBatch))
                {
                    if (batch.QueuedSubmissionCount != 0)
                    {
                        throw new InvalidOperationException(
                            $"Command buffer 0x{commandBufferHandle:X} cannot bind descriptor set " +
                            $"0x{descriptorSet.Handle:X} while queued for submission.");
                    }

                    expandedSnapshot = batch.MarkDescriptorExpanded(descriptorSet.Handle, snapshot.Generation);

                    if (batch.MarkDescriptorValidated(descriptorSet.Handle, snapshot.Generation))
                        snapshotToValidate = snapshot;
                    usedTrackingBatch = true;
                }
            }
        }

        if (usedTrackingBatch)
        {
            RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanDescriptorExpansion(
                expandedSnapshot ? 0 : 1,
                expandedSnapshot ? 1 : 0);
            if (snapshotToValidate is not null)
            {
                if (isSecondaryCommandBuffer)
                    RecordSecondaryDescriptorImageLayoutRequirements(commandBuffer, descriptorSet, snapshotToValidate);
                else
                    ValidateVulkanDescriptorImageLayouts(commandBuffer, descriptorSet, snapshotToValidate);
            }
            return;
        }

        lock (ResourceRuntime.Lifetime.Tracker.SyncRoot)
        {
            if (ResourceRuntime.Lifetime.Tracker.DescriptorSetLifetimes.TryGetValue(descriptorSet.Handle, out VulkanDescriptorSetLifetimeRecord? setState))
            {
                if (isSecondaryCommandBuffer)
                    RecordSecondaryDescriptorImageLayoutRequirements(commandBuffer, descriptorSet, setState);
                else
                    ValidateVulkanDescriptorImageLayouts(commandBuffer, descriptorSet, setState);
            }
        }
    }

    /// <summary>
    /// Verifies that a recorded command buffer binds every current native
    /// descriptor set supplied by a reusable draw. Logical descriptor identity
    /// alone cannot prove this when compatible allocation variants coexist.
    /// </summary>
    internal bool CommandBufferReferencesAllDescriptorSets(
        CommandBuffer commandBuffer,
        ReadOnlySpan<DescriptorSet> descriptorSets,
        out ulong missingDescriptorSetHandle)
    {
        missingDescriptorSetHandle = 0;
        if (commandBuffer.Handle == 0)
            return false;

        ulong commandBufferHandle = unchecked((ulong)commandBuffer.Handle);
        lock (ResourceRuntime.Lifetime.Tracker.SyncRoot)
        {
            if (!ResourceRuntime.Lifetime.Tracker.CommandBufferLifetimes.TryGetValue(
                    commandBufferHandle,
                    out VulkanCommandBufferLifetimeRecord? lifetime))
            {
                return false;
            }

            for (int i = 0; i < descriptorSets.Length; i++)
            {
                DescriptorSet descriptorSet = descriptorSets[i];
                if (descriptorSet.Handle == 0)
                    continue;

                VulkanResourceLifetimeKey key =
                    ResourceKey(ObjectType.DescriptorSet, descriptorSet.Handle);
                if (lifetime.Dependencies.ContainsKey(key))
                    continue;

                missingDescriptorSetHandle = descriptorSet.Handle;
                return false;
            }
        }

        return true;
    }

    private void RecordSecondaryDescriptorImageLayoutRequirements(
        CommandBuffer commandBuffer,
        DescriptorSet descriptorSet,
        VulkanPublishedDescriptorSetSnapshot snapshot)
    {
        RecordSecondaryDescriptorPayloadGeneration(
            commandBuffer,
            descriptorSet.Handle,
            snapshot.Generation);
        FlushPendingSecondaryImageAccesses(commandBuffer);
        bool recordedAny = false;
        for (int i = 0; i < snapshot.ImageReferences.Length; i++)
        {
            VulkanPublishedDescriptorImageReference published = snapshot.ImageReferences[i];
            if (snapshot.HasReflection && Array.IndexOf(snapshot.ReflectedImageBindings, published.Binding) < 0)
                continue;

            recordedAny |= RecordSecondaryDescriptorImageLayoutRequirement(
                commandBuffer,
                descriptorSet,
                published.Binding,
                published.Element,
                published.Reference);
        }

        if (recordedAny)
            AdvanceCommandBufferImageLayoutVersion(
                commandBuffer,
                descriptorSet.Handle,
                snapshot.Generation);
    }

    private void RecordSecondaryDescriptorImageLayoutRequirements(
        CommandBuffer commandBuffer,
        DescriptorSet descriptorSet,
        VulkanDescriptorSetLifetimeRecord setState)
    {
        RecordSecondaryDescriptorPayloadGeneration(
            commandBuffer,
            descriptorSet.Handle,
            setState.Generation);
        FlushPendingSecondaryImageAccesses(commandBuffer);
        bool recordedAny = false;
        foreach (((uint binding, uint element), VulkanDescriptorImageReference reference) in setState.ImageReferences)
        {
            if (setState.HasReflection && !setState.ReflectedImageBindings.Contains(binding))
                continue;

            recordedAny |= RecordSecondaryDescriptorImageLayoutRequirement(
                commandBuffer,
                descriptorSet,
                binding,
                element,
                reference);
        }

        if (recordedAny)
            AdvanceCommandBufferImageLayoutVersion(
                commandBuffer,
                descriptorSet.Handle,
                setState.Generation);
    }

    /// <summary>
    /// Publishes the exact native descriptor-set content generation used while
    /// recording a secondary. The primary uses this proof before trusting the
    /// secondary's frozen descriptor image/layout requirements.
    /// </summary>
    private void RecordSecondaryDescriptorPayloadGeneration(
        CommandBuffer commandBuffer,
        ulong descriptorSetHandle,
        ulong descriptorGeneration)
    {
        if (commandBuffer.Handle == 0 || descriptorSetHandle == 0)
            return;

        ulong commandBufferHandle = unchecked((ulong)commandBuffer.Handle);
        lock (_commandRuntime.Synchronization._vulkanImageLayoutLock)
        {
            if (!_commandRuntime.Synchronization._recordedImageLayoutsByCommandBuffer.TryGetValue(
                    commandBufferHandle,
                    out VulkanRecordedImageLayoutState? recorded))
            {
                recorded = new VulkanRecordedImageLayoutState
                {
                    RecordingGeneration = ResolveCommandBufferRecordingGeneration(commandBuffer),
                };
                _commandRuntime.Synchronization._recordedImageLayoutsByCommandBuffer[commandBufferHandle] = recorded;
            }

            recorded.SecondaryDescriptorPayloadGenerations[descriptorSetHandle] =
                descriptorGeneration;
        }
    }

    private void FlushPendingSecondaryImageAccesses(CommandBuffer commandBuffer)
    {
        ulong commandBufferHandle = unchecked((ulong)commandBuffer.Handle);
        if (commandBufferHandle == 0 ||
            !_commandBufferTrackingBatches.TryGetValue(
                commandBufferHandle,
                out VulkanCommandBufferTrackingBatch? batch))
        {
            return;
        }

        lock (batch)
        {
            if (_commandBufferTrackingBatches.TryGetValue(
                    commandBufferHandle,
                    out VulkanCommandBufferTrackingBatch? currentBatch) &&
                ReferenceEquals(batch, currentBatch) &&
                batch.PublishedImageDeltaCount < batch.ImageAccessDeltas.Count)
            {
                FlushCommandBufferImageAccessBatch(commandBuffer, batch);
            }
        }
    }

    private void AdvanceCommandBufferImageLayoutVersion(
        CommandBuffer commandBuffer,
        ulong descriptorSetHandle,
        ulong descriptorGeneration)
    {
        ulong commandBufferHandle = unchecked((ulong)commandBuffer.Handle);
        if (commandBufferHandle == 0 ||
            !_commandBufferTrackingBatches.TryGetValue(
                commandBufferHandle,
                out VulkanCommandBufferTrackingBatch? batch))
        {
            return;
        }

        lock (batch)
        {
            if (_commandBufferTrackingBatches.TryGetValue(
                    commandBufferHandle,
                    out VulkanCommandBufferTrackingBatch? currentBatch) &&
                ReferenceEquals(batch, currentBatch))
            {
                batch.LayoutVersion++;
                batch.ValidatedDescriptorGenerations[descriptorSetHandle] = (
                    descriptorGeneration,
                    batch.LayoutVersion);
            }
        }
    }

    private bool RecordSecondaryDescriptorImageLayoutRequirement(
        CommandBuffer commandBuffer,
        DescriptorSet descriptorSet,
        uint binding,
        uint element,
        VulkanDescriptorImageReference reference)
    {
        if (reference.View.Handle == 0 ||
            reference.Type is not (
                DescriptorType.CombinedImageSampler or
                DescriptorType.SampledImage or
                DescriptorType.InputAttachment or
                DescriptorType.StorageImage) ||
            !TryGetDescriptorHeapImageViewCreateInfo(reference.View, out ImageViewCreateInfo viewInfo) ||
            viewInfo.Image.Handle == 0)
        {
            return false;
        }

        ImageLayout requiredLayout = reference.Type == DescriptorType.StorageImage
            ? ImageLayout.General
            : reference.Layout;
        if (requiredLayout == ImageLayout.Undefined)
            return false;

        ImageSubresourceRange range = viewInfo.SubresourceRange;
        range.AspectMask = NormalizeBarrierAspectMask(viewInfo.Format, range.AspectMask);
        range.LevelCount = Math.Max(range.LevelCount, 1u);
        range.LayerCount = Math.Max(range.LayerCount, 1u);
        ulong commandBufferHandle = unchecked((ulong)commandBuffer.Handle);
        ulong resourceGeneration = GetCurrentVulkanResourceGeneration(
            ObjectType.Image,
            viewInfo.Image.Handle);
        bool compatible = true;
        lock (_commandRuntime.Synchronization._vulkanImageLayoutLock)
        {
            if (!_commandRuntime.Synchronization._recordedImageLayoutsByCommandBuffer.TryGetValue(
                    commandBufferHandle,
                    out VulkanRecordedImageLayoutState? recorded))
            {
                recorded = new VulkanRecordedImageLayoutState
                {
                    RecordingGeneration = ResolveCommandBufferRecordingGeneration(commandBuffer),
                };
                _commandRuntime.Synchronization._recordedImageLayoutsByCommandBuffer[commandBufferHandle] = recorded;
            }

            for (uint mipOffset = 0; mipOffset < range.LevelCount; mipOffset++)
            {
                uint mip = range.BaseMipLevel + mipOffset;
                for (uint layerOffset = 0; layerOffset < range.LayerCount; layerOffset++)
                {
                    uint layer = range.BaseArrayLayer + layerOffset;
                    compatible &= RecordSecondaryDescriptorImageAspectRequirement(
                        recorded,
                        viewInfo.Image.Handle,
                        mip,
                        layer,
                        range.AspectMask,
                        ImageAspectFlags.ColorBit,
                        requiredLayout,
                        resourceGeneration);
                    compatible &= RecordSecondaryDescriptorImageAspectRequirement(
                        recorded,
                        viewInfo.Image.Handle,
                        mip,
                        layer,
                        range.AspectMask,
                        ImageAspectFlags.DepthBit,
                        requiredLayout,
                        resourceGeneration);
                    compatible &= RecordSecondaryDescriptorImageAspectRequirement(
                        recorded,
                        viewInfo.Image.Handle,
                        mip,
                        layer,
                        range.AspectMask,
                        ImageAspectFlags.StencilBit,
                        requiredLayout,
                        resourceGeneration);
                }
            }

            recorded.RefreshTouchedSubresources();
        }

        if (compatible)
            return true;

        _liveImageViewHandles.TryGetValue(reference.View.Handle, out string? imageViewOwner);
        string message =
            $"Vulkan secondary descriptor image layout requirement conflicts with an earlier command: " +
            $"commandBuffer=0x{commandBuffer.Handle:X} set=0x{descriptorSet.Handle:X} " +
            $"binding={binding}[{element}] view=0x{reference.View.Handle:X} image=0x{viewInfo.Image.Handle:X} " +
            $"owner={imageViewOwner ?? "<unknown>"} required={requiredLayout} type={reference.Type}.";
        Debug.VulkanWarning("[Vulkan.Layout] {0}", message);
        if (RuntimeEngine.Rendering.State.VulkanValidationLayersEnabled)
            throw new InvalidOperationException(message);
        if (System.Diagnostics.Debugger.IsAttached)
            System.Diagnostics.Debug.Fail(message);
        return true;
    }

    private static bool RecordSecondaryDescriptorImageAspectRequirement(
        VulkanRecordedImageLayoutState recorded,
        ulong imageHandle,
        uint mip,
        uint layer,
        ImageAspectFlags rangeAspect,
        ImageAspectFlags trackedAspect,
        ImageLayout requiredLayout,
        ulong resourceGeneration)
    {
        if ((rangeAspect & trackedAspect) == 0)
            return true;

        VulkanTrackedImageSubresource key = new(imageHandle, mip, layer, trackedAspect);
        VulkanImageAccessState? prior = null;
        if (recorded.Subresources.TryGetValue(key, out VulkanImageAccessState recordedState))
            prior = recordedState;
        else if (recorded.EntrySubresources.TryGetValue(key, out VulkanImageAccessState entryState))
            prior = entryState;

        bool compatible = !prior.HasValue ||
            (prior.Value.Layout == requiredLayout &&
             (prior.Value.ResourceGeneration == 0 ||
              resourceGeneration == 0 ||
              prior.Value.ResourceGeneration == resourceGeneration));

        uint queueFamilyIndex = prior?.QueueFamilyIndex ?? Vk.QueueFamilyIgnored;
        VulkanImageAccessState requiredState = ResolveVulkanImageAccessState(
            requiredLayout,
            trackedAspect,
            queueFamilyIndex,
            resourceGeneration: resourceGeneration);
        if (!recorded.SecondaryDescriptorRequirements.ContainsKey(key))
            recorded.SecondaryDescriptorRequirements[key] = requiredState;
        if (!recorded.EntrySubresources.ContainsKey(key))
            recorded.EntrySubresources[key] = requiredState;
        recorded.Subresources[key] = requiredState;
        return compatible;
    }

    [Conditional("DEBUG")]
    private void ValidateVulkanDescriptorImageLayouts(
        CommandBuffer commandBuffer,
        DescriptorSet descriptorSet,
        VulkanPublishedDescriptorSetSnapshot snapshot)
    {
        for (int i = 0; i < snapshot.ImageReferences.Length; i++)
        {
            VulkanPublishedDescriptorImageReference published = snapshot.ImageReferences[i];
            if (snapshot.HasReflection && Array.IndexOf(snapshot.ReflectedImageBindings, published.Binding) < 0)
                continue;

            ValidateVulkanDescriptorImageLayout(
                commandBuffer,
                descriptorSet,
                published.Binding,
                published.Element,
                published.Reference);
        }
    }

    [Conditional("DEBUG")]
    private void ValidateVulkanDescriptorImageLayouts(
        CommandBuffer commandBuffer,
        DescriptorSet descriptorSet,
        VulkanDescriptorSetLifetimeRecord setState)
    {
        foreach (((uint binding, uint element), VulkanDescriptorImageReference reference) in setState.ImageReferences)
        {
            if (setState.HasReflection && !setState.ReflectedImageBindings.Contains(binding))
                continue;

            ValidateVulkanDescriptorImageLayout(commandBuffer, descriptorSet, binding, element, reference);
        }
    }

    [Conditional("DEBUG")]
    private void ValidateVulkanDescriptorImageLayout(
        CommandBuffer commandBuffer,
        DescriptorSet descriptorSet,
        uint binding,
        uint element,
        VulkanDescriptorImageReference reference)
    {
        if (reference.View.Handle == 0 ||
            !TryGetDescriptorHeapImageViewCreateInfo(reference.View, out ImageViewCreateInfo viewInfo))
        {
            return;
        }

        ImageSubresourceRange range = viewInfo.SubresourceRange;
        if (!TryGetRecordedImageLayout(commandBuffer, viewInfo.Image, range, out ImageLayout trackedLayout))
            return;

        bool attachmentOrTransfer = trackedLayout is
            ImageLayout.ColorAttachmentOptimal or
            ImageLayout.DepthAttachmentOptimal or
            ImageLayout.StencilAttachmentOptimal or
            ImageLayout.DepthStencilAttachmentOptimal or
            ImageLayout.AttachmentOptimal or
            ImageLayout.TransferSrcOptimal or
            ImageLayout.TransferDstOptimal;
        bool compatible = reference.Type == DescriptorType.StorageImage
            ? trackedLayout == ImageLayout.General && reference.Layout == ImageLayout.General
            : !attachmentOrTransfer && trackedLayout == reference.Layout;
        if (compatible)
            return;

        _liveImageViewHandles.TryGetValue(reference.View.Handle, out string? imageViewOwner);
        string message =
            $"Vulkan descriptor image layout mismatch at command recording: set=0x{descriptorSet.Handle:X} " +
            $"binding={binding}[{element}] view=0x{reference.View.Handle:X} image=0x{viewInfo.Image.Handle:X} " +
            $"owner={imageViewOwner ?? "<unknown>"} descriptor={reference.Layout} tracked={trackedLayout} type={reference.Type}.";
        Debug.VulkanWarning("[Vulkan.Layout] {0}", message);
        if (RuntimeEngine.Rendering.State.VulkanValidationLayersEnabled)
            throw new InvalidOperationException(message);
        if (System.Diagnostics.Debugger.IsAttached)
            System.Diagnostics.Debug.Fail(message);
    }

    private bool ValidateVulkanSubmissionResourceLifetimes(
        ref SubmitInfo submitInfo,
        in VulkanSubmissionDiagnosticContext diagnosticContext,
        out string failureReason,
        out EOpenXrStrictSpsFaultInjectionStage injectedFailureStage)
    {
        injectedFailureStage = EOpenXrStrictSpsFaultInjectionStage.None;
        if (!TryAcquireVulkanSubmissionGatewayPins(
                ref submitInfo,
                in diagnosticContext,
                out failureReason))
        {
            return false;
        }

        bool retainGatewayPins = false;
        try
        {
            if (diagnosticContext.OpenXrStrictSpsFaultInjectionStage ==
                EOpenXrStrictSpsFaultInjectionStage.LifetimeValidation)
            {
                injectedFailureStage = EOpenXrStrictSpsFaultInjectionStage.LifetimeValidation;
                failureReason = "injected strict-SPS lifetime-validation boundary failure";
                return false;
            }

            for (int commandIndex = 0; commandIndex < submitInfo.CommandBufferCount; commandIndex++)
            {
                CommandBuffer commandBuffer = submitInfo.PCommandBuffers[commandIndex];
                if (TryFlushCommandBufferTrackingBatch(commandBuffer, out string trackingFailure))
                    continue;

                ulong commandBufferHandle = unchecked((ulong)commandBuffer.Handle);
                VulkanResourceLifetimeKey commandBufferKey = ResourceKey(ObjectType.CommandBuffer, commandBufferHandle);
                lock (ResourceRuntime.Lifetime.Tracker.SyncRoot)
                {
                    ResourceRuntime.Lifetime.Tracker.ResourceLifetimes.TryGetValue(
                        commandBufferKey,
                        out VulkanResourceLifetimeRecord? commandResource);
                    failureReason = DescribeVulkanLifetimeRejection(
                        commandBufferKey,
                        commandResource?.Owner ?? "<untracked>",
                        commandResource?.Generation ?? 0,
                        commandResource?.Generation ?? 0,
                        diagnosticContext.OutputTargetName,
                        commandBufferHandle,
                        commandResource?.RetirementTicket ?? VulkanRetirementTicket.None,
                        commandResource?.State ?? EVulkanResourceLifetimeState.None,
                        $"tracking publication failed: {trackingFailure}");
                }
                if (commandBufferHandle != 0)
                {
                    _ = InvalidateCachedCommandBuffersByHandle(
                        [commandBufferHandle],
                        $"submission tracking rejected: {failureReason}");
                }
                return false;
            }

            lock (ResourceRuntime.Lifetime.Tracker.SyncRoot)
            {
                for (int commandIndex = 0; commandIndex < submitInfo.CommandBufferCount; commandIndex++)
                {
                    ulong commandBufferHandle = unchecked((ulong)submitInfo.PCommandBuffers[commandIndex].Handle);
                    if (commandBufferHandle == 0)
                        continue;

                    VulkanResourceLifetimeKey commandBufferKey = ResourceKey(ObjectType.CommandBuffer, commandBufferHandle);
                    if (ResourceRuntime.Lifetime.Tracker.ResourceLifetimes.TryGetValue(commandBufferKey, out VulkanResourceLifetimeRecord? commandResource) &&
                        (commandResource.State & (EVulkanResourceLifetimeState.PendingRetirement | EVulkanResourceLifetimeState.Destroyed)) != 0)
                    {
                        failureReason = DescribeVulkanLifetimeRejection(
                            commandBufferKey,
                            commandResource.Owner,
                            commandResource.Generation,
                            commandResource.Generation,
                            diagnosticContext.OutputTargetName,
                            commandBufferHandle,
                            commandResource.RetirementTicket,
                            commandResource.State,
                            "submission references a retired command buffer");
                        return false;
                    }

                    if (!ResourceRuntime.Lifetime.Tracker.CommandBufferLifetimes.TryGetValue(commandBufferHandle, out VulkanCommandBufferLifetimeRecord? commandLifetime))
                    {
                        continue;
                    }

                if (!RefreshSubmittedDescriptorDependencies_NoLock(
                        commandLifetime,
                        out VulkanResourceLifetimeKey descriptorFailureKey,
                        out string descriptorFailure))
                {
                    ResourceRuntime.Lifetime.Tracker.ResourceLifetimes.TryGetValue(
                        descriptorFailureKey,
                        out VulkanResourceLifetimeRecord? descriptorFailureResource);
                    failureReason = DescribeVulkanLifetimeRejection(
                        descriptorFailureKey,
                        descriptorFailureResource?.Owner ?? "<untracked>",
                        descriptorFailureResource?.Generation ?? 0,
                        descriptorFailureResource?.Generation ?? 0,
                        diagnosticContext.OutputTargetName,
                        commandBufferHandle,
                        descriptorFailureResource?.RetirementTicket ?? VulkanRetirementTicket.None,
                        descriptorFailureResource?.State ?? EVulkanResourceLifetimeState.None,
                        descriptorFailure);
                    return false;
                }

                foreach ((VulkanResourceLifetimeKey key, ulong recordedGeneration) in commandLifetime.TouchedDependencies)
                {
                    if (!ResourceRuntime.Lifetime.Tracker.ResourceLifetimes.TryGetValue(key, out VulkanResourceLifetimeRecord? resource))
                    {
                        failureReason = DescribeVulkanLifetimeRejection(
                            key,
                            "<untracked>",
                            recordedGeneration,
                            0,
                            diagnosticContext.OutputTargetName,
                            commandBufferHandle,
                            VulkanRetirementTicket.None,
                            EVulkanResourceLifetimeState.None,
                            "recorded dependency is no longer tracked");
                        return false;
                    }

                    if (resource.Generation != recordedGeneration)
                    {
                        failureReason = DescribeVulkanLifetimeRejection(
                            key,
                            resource.Owner,
                            recordedGeneration,
                            resource.Generation,
                            diagnosticContext.OutputTargetName,
                            commandBufferHandle,
                            resource.RetirementTicket,
                            resource.State,
                            "recorded generation no longer matches the published generation");
                        return false;
                    }

                    if ((resource.State & EVulkanResourceLifetimeState.Destroyed) != 0)
                    {
                        failureReason = DescribeVulkanLifetimeRejection(
                            key,
                            resource.Owner,
                            recordedGeneration,
                            resource.Generation,
                            diagnosticContext.OutputTargetName,
                            commandBufferHandle,
                            resource.RetirementTicket,
                            resource.State,
                            "recorded dependency was destroyed before submission");
                        return false;
                    }
                }
                }

                // Pin resources only after the whole submission validates. The
                // command/batch gateway pins above freeze the exact dependency
                // set throughout publication and validation without leaving
                // resource pins behind for a rejected submit.
                for (int commandIndex = 0; commandIndex < submitInfo.CommandBufferCount; commandIndex++)
                {
                    ulong commandBufferHandle = unchecked((ulong)submitInfo.PCommandBuffers[commandIndex].Handle);
                    if (commandBufferHandle == 0)
                        continue;

                    VulkanResourceLifetimeRecord commandResource = GetOrRegisterVulkanResource_NoLock(
                        ResourceKey(ObjectType.CommandBuffer, commandBufferHandle),
                        "CommandBuffer.SubmitQueuePin");
                    AddVulkanQueuedGenerationPin_NoLock(commandResource);

                    VulkanCommandBufferLifetimeRecord commandLifetime =
                        ResourceRuntime.Lifetime.Tracker.CommandBufferLifetimes[commandBufferHandle];

                    foreach ((VulkanResourceLifetimeKey key, _) in commandLifetime.TouchedDependencies)
                    {
                        AddVulkanQueuedGenerationPin_NoLock(ResourceRuntime.Lifetime.Tracker.ResourceLifetimes[key]);
                    }
                }
            }

            retainGatewayPins = true;
            failureReason = string.Empty;
            return true;
        }
        finally
        {
            if (!retainGatewayPins)
                ReleaseVulkanSubmissionGatewayPins(ref submitInfo);
        }
    }

    private static string DescribeVulkanRetirementTicket(in VulkanRetirementTicket ticket)
        => $"gfx:{ticket.GraphicsSequence}/transfer:{ticket.TransferSequence}/other:{ticket.OtherSequence}/generation:{ticket.ResourceGeneration}/external:{ticket.ExternalOwnershipPending}/pins:{ticket.PinSet?.Count ?? 0}";

    internal static string DescribeVulkanLifetimeRejection(
        VulkanResourceLifetimeKey resource,
        string owner,
        ulong oldGeneration,
        ulong newGeneration,
        string? output,
        ulong commandBufferHandle,
        in VulkanRetirementTicket retirementTicket,
        EVulkanResourceLifetimeState state,
        string reason)
        => new VulkanLifetimeRejectionDiagnostic(
            resource,
            owner,
            oldGeneration,
            newGeneration,
            output ?? "<unknown>",
            commandBufferHandle,
            retirementTicket,
            state,
            reason).ToString();

    internal static void AddVulkanQueuedGenerationPin_NoLock(VulkanResourceLifetimeRecord resource)
    {
        resource.Pins.AddQueuedReference();
        resource.State |= EVulkanResourceLifetimeState.Queued;
    }

    internal static void ReleaseVulkanQueuedGenerationPin_NoLock(VulkanResourceLifetimeRecord resource)
    {
        resource.Pins.ReleaseQueuedReference();
        if (!resource.Pins.HasQueuedReferences)
            resource.State &= ~EVulkanResourceLifetimeState.Queued;
    }

    private void ReleaseVulkanSubmissionResourceLifetimePins(ref SubmitInfo submitInfo)
    {
        lock (ResourceRuntime.Lifetime.Tracker.SyncRoot)
        {
            for (int commandIndex = 0; commandIndex < submitInfo.CommandBufferCount; commandIndex++)
            {
                ulong commandBufferHandle = unchecked((ulong)submitInfo.PCommandBuffers[commandIndex].Handle);
                if (commandBufferHandle == 0)
                    continue;

                if (ResourceRuntime.Lifetime.Tracker.ResourceLifetimes.TryGetValue(
                        ResourceKey(ObjectType.CommandBuffer, commandBufferHandle),
                        out VulkanResourceLifetimeRecord? commandResource))
                {
                    ReleaseVulkanQueuedGenerationPin_NoLock(commandResource);
                }

                if (!ResourceRuntime.Lifetime.Tracker.CommandBufferLifetimes.TryGetValue(
                        commandBufferHandle,
                        out VulkanCommandBufferLifetimeRecord? commandLifetime))
                {
                    continue;
                }

                foreach ((VulkanResourceLifetimeKey key, _) in commandLifetime.TouchedDependencies)
                {
                    if (ResourceRuntime.Lifetime.Tracker.ResourceLifetimes.TryGetValue(key, out VulkanResourceLifetimeRecord? resource))
                        ReleaseVulkanQueuedGenerationPin_NoLock(resource);
                }
            }

            ReleaseVulkanSubmissionGatewayPins_NoLock(ref submitInfo);
        }
    }

    private void ReleaseVulkanSubmissionGatewayPins(ref SubmitInfo submitInfo)
    {
        lock (ResourceRuntime.Lifetime.Tracker.SyncRoot)
            ReleaseVulkanSubmissionGatewayPins_NoLock(ref submitInfo);
    }

    private void ReleaseVulkanSubmissionGatewayPins_NoLock(ref SubmitInfo submitInfo)
    {
        for (int commandIndex = 0; commandIndex < submitInfo.CommandBufferCount; commandIndex++)
        {
            ulong commandBufferHandle = unchecked((ulong)submitInfo.PCommandBuffers[commandIndex].Handle);
            if (commandBufferHandle == 0)
                continue;

            if (!ResourceRuntime.Lifetime.Tracker.CommandBufferLifetimes.TryGetValue(
                    commandBufferHandle,
                    out VulkanCommandBufferLifetimeRecord? commandLifetime) ||
                commandLifetime.QueuedSubmissionCount <= 0)
            {
                throw new InvalidOperationException(
                    $"Command buffer 0x{commandBufferHandle:X} submission-gateway pin underflow.");
            }

            commandLifetime.QueuedSubmissionCount--;
            // The gateway pin is released for both accepted and rejected submissions. At
            // this point recording has ended, so a rejected submit retains only the cached
            // variant owner; an accepted submit has already added its exact queue ticket.
            commandLifetime.FrameDataLease.CompleteRecording(cacheVariant: true);
            if (_commandBufferTrackingBatches.TryGetValue(
                    commandBufferHandle,
                    out VulkanCommandBufferTrackingBatch? trackingBatch))
            {
                lock (trackingBatch)
                {
                    if (trackingBatch.QueuedSubmissionCount <= 0)
                    {
                        throw new InvalidOperationException(
                            $"Command buffer 0x{commandBufferHandle:X} tracking-batch gateway pin underflow.");
                    }

                    trackingBatch.QueuedSubmissionCount--;
                }
            }
        }
    }

    private bool RefreshSubmittedDescriptorDependencies_NoLock(
        VulkanCommandBufferLifetimeRecord commandLifetime,
        out VulkanResourceLifetimeKey failureKey,
        out string failureReason)
    {
        // Descriptor contents are mutable per completed frame slot. They are not
        // structural command-buffer dependencies: only the descriptor-set handle
        // is baked into vkCmdBindDescriptorSets. Rebuild the concrete resource
        // snapshot for each submission so old image generations retain the exact
        // submissions that observed them without dirtying the reusable command
        // buffer when another slot publishes compatible content.
        commandLifetime.RefreshTouchedDependencies();
        List<KeyValuePair<VulkanResourceLifetimeKey, ulong>> touched = commandLifetime.TouchedDependencies;
        int descriptorScanCount = touched.Count;
        Dictionary<VulkanResourceLifetimeKey, ulong> touchedGenerations =
            ResourceRuntime.Lifetime.Tracker.SubmissionDependencyGenerationsScratch;
        touchedGenerations.Clear();
        for (int i = 0; i < descriptorScanCount; i++)
            touchedGenerations[touched[i].Key] = touched[i].Value;
        for (int i = 0; i < descriptorScanCount; i++)
        {
            VulkanResourceLifetimeKey key = touched[i].Key;
            if (key.Type != ObjectType.DescriptorSet ||
                !ResourceRuntime.Lifetime.Tracker.PublishedDescriptorSets.TryGetValue(key.Handle, out VulkanPublishedDescriptorSetSnapshot? snapshot))
            {
                continue;
            }

            for (int referenceIndex = 0; referenceIndex < snapshot.References.Length; referenceIndex++)
            {
                VulkanResourceLifetimeKey referenceKey = snapshot.References[referenceIndex];
                if (!TryAppendSubmittedDescriptorDependency_NoLock(
                        touched,
                        touchedGenerations,
                        referenceKey,
                        out failureReason))
                {
                    VulkanResourceLifetimeKey descriptorSetKey = ResourceKey(ObjectType.DescriptorSet, key.Handle);
                    string descriptorSetOwner = ResourceRuntime.Lifetime.Tracker.ResourceLifetimes.TryGetValue(
                        descriptorSetKey,
                        out VulkanResourceLifetimeRecord? descriptorSetResource)
                        ? descriptorSetResource.Owner
                        : "unknown";
                    failureReason = $"{failureReason}; referenced by {descriptorSetKey} owner={descriptorSetOwner} snapshotGeneration={snapshot.Generation}";
                    failureKey = referenceKey;
                    return false;
                }
            }
        }

        failureKey = default;
        failureReason = string.Empty;
        return true;
    }

    private bool TryAppendSubmittedDescriptorDependency_NoLock(
        List<KeyValuePair<VulkanResourceLifetimeKey, ulong>> touched,
        Dictionary<VulkanResourceLifetimeKey, ulong> touchedGenerations,
        VulkanResourceLifetimeKey key,
        out string failureReason)
    {
        if (!ResourceRuntime.Lifetime.Tracker.ResourceLifetimes.TryGetValue(key, out VulkanResourceLifetimeRecord? resource))
        {
            failureReason = $"descriptor submission dependency {key} is no longer tracked";
            return false;
        }

        if ((resource.State & EVulkanResourceLifetimeState.Destroyed) != 0)
        {
            failureReason = $"descriptor submission dependency {key} was destroyed before submission";
            return false;
        }

        if (touchedGenerations.TryGetValue(key, out ulong trackedGeneration))
        {
            if (trackedGeneration != resource.Generation)
            {
                failureReason = $"descriptor submission dependency {key} changed generation while the submission was prepared";
                return false;
            }

            // A retirement request invalidates future recordings, but the exact
            // generation already pinned by this recorded command buffer remains alive
            // until that recording is released. It is therefore valid to submit once
            // against that retained generation while its replacement is published.
        }
        else
        {
            if ((resource.State & EVulkanResourceLifetimeState.PendingRetirement) != 0 &&
                !resource.Pins.HasDescriptorReferences)
            {
                failureReason = $"descriptor submission dependency {key} began retirement before this command buffer captured it";
                return false;
            }

            touched.Add(new KeyValuePair<VulkanResourceLifetimeKey, ulong>(key, resource.Generation));
            touchedGenerations.Add(key, resource.Generation);
        }

        if (key.Type == ObjectType.ImageView &&
            ResourceRuntime.Lifetime.Tracker.ImageViewBackingImages.TryGetValue(key.Handle, out ulong backingImageHandle) &&
            backingImageHandle != 0 &&
            !TryAppendSubmittedDescriptorDependency_NoLock(
                touched,
                touchedGenerations,
                ResourceKey(ObjectType.Image, backingImageHandle),
                out failureReason))
        {
            return false;
        }

        if (key.Type == ObjectType.BufferView &&
            ResourceRuntime.Lifetime.Tracker.BufferViewBackingBuffers.TryGetValue(key.Handle, out ulong backingBufferHandle) &&
            backingBufferHandle != 0 &&
            !TryAppendSubmittedDescriptorDependency_NoLock(
                touched,
                touchedGenerations,
                ResourceKey(ObjectType.Buffer, backingBufferHandle),
                out failureReason))
        {
            return false;
        }

        failureReason = string.Empty;
        return true;
    }

    private VulkanLifetimeSubmission RecordSuccessfulVulkanSubmissionLifetime(
        Queue queue,
        ref SubmitInfo submitInfo,
        Fence fence,
        in VulkanSubmissionDiagnosticContext diagnosticContext)
    {
        for (int commandIndex = 0; commandIndex < submitInfo.CommandBufferCount; commandIndex++)
            FlushCommandBufferTrackingBatch(submitInfo.PCommandBuffers[commandIndex]);

        ulong queueHandle = unchecked((ulong)queue.Handle);
        EVulkanLifetimeQueueDomain domain = ResolveVulkanLifetimeQueueDomain(queue);
        ResolveSubmissionTimelineSignal(ref submitInfo, out ulong timelineSemaphoreHandle, out ulong timelineValue);

        VulkanLifetimeSubmission submission;
        lock (ResourceRuntime.Lifetime.Tracker.SyncRoot)
        {
            ulong queueSequence = domain switch
            {
                EVulkanLifetimeQueueDomain.Graphics => ++ResourceRuntime.Lifetime.Tracker.LastGraphicsSequence,
                EVulkanLifetimeQueueDomain.Transfer => ++ResourceRuntime.Lifetime.Tracker.LastTransferSequence,
                _ => ++ResourceRuntime.Lifetime.Tracker.LastOtherSequence,
            };

            submission = new VulkanLifetimeSubmission(
                queueHandle,
                domain,
                queueSequence,
                timelineSemaphoreHandle,
                timelineValue,
                unchecked((ulong)fence.Handle));
            ResourceRuntime.Lifetime.Tracker.LifetimeSubmissions.Add(submission);

            for (int commandIndex = 0; commandIndex < submitInfo.CommandBufferCount; commandIndex++)
            {
                ulong commandBufferHandle = unchecked((ulong)submitInfo.PCommandBuffers[commandIndex].Handle);
                if (commandBufferHandle == 0)
                    continue;

                MarkVulkanResourceSubmitted_NoLock(
                    GetOrRegisterVulkanResource_NoLock(
                        ResourceKey(ObjectType.CommandBuffer, commandBufferHandle),
                        "CommandBuffer.Submit"),
                    domain,
                    queueSequence,
                    diagnosticContext.SubmissionSerial,
                    diagnosticContext.FrameOpContextId,
                    diagnosticContext.FrameOpKind);

                if (!ResourceRuntime.Lifetime.Tracker.CommandBufferLifetimes.TryGetValue(commandBufferHandle, out VulkanCommandBufferLifetimeRecord? commandLifetime))
                    continue;

                commandLifetime.FrameDataLease.TryTransferToSubmission(domain, queueSequence);

                foreach ((VulkanResourceLifetimeKey key, _) in commandLifetime.TouchedDependencies)
                {
                    if (ResourceRuntime.Lifetime.Tracker.ResourceLifetimes.TryGetValue(key, out VulkanResourceLifetimeRecord? resource))
                    {
                        MarkVulkanResourceSubmitted_NoLock(
                            resource,
                            domain,
                            queueSequence,
                            diagnosticContext.SubmissionSerial,
                            diagnosticContext.FrameOpContextId,
                            diagnosticContext.FrameOpKind);
                    }

                    if (key.Type == ObjectType.QueryPool &&
                        ResourceRuntime.Lifetime.Tracker.RenderQueriesByPool.TryGetValue(key.Handle, out List<VkRenderQuery>? queries))
                    {
                        for (int queryIndex = 0; queryIndex < queries.Count; queryIndex++)
                            queries[queryIndex].MarkResultEpochSubmitted(commandBufferHandle, in submission);
                    }
                }
            }
        }

        LogVulkanResourceLifetimeDiagnostics(diagnosticContext.SubmissionKind ?? "submit");
        return submission;
    }

    internal void RegisterVulkanRenderQuery(QueryPool queryPool, VkRenderQuery query)
    {
        if (queryPool.Handle == 0)
            return;

        lock (ResourceRuntime.Lifetime.Tracker.SyncRoot)
        {
            if (!ResourceRuntime.Lifetime.Tracker.RenderQueriesByPool.TryGetValue(queryPool.Handle, out List<VkRenderQuery>? queries))
            {
                queries = new List<VkRenderQuery>(32);
                ResourceRuntime.Lifetime.Tracker.RenderQueriesByPool.Add(queryPool.Handle, queries);
            }

            if (!queries.Contains(query))
                queries.Add(query);
        }
    }

    internal void UnregisterVulkanRenderQuery(QueryPool queryPool, VkRenderQuery query)
    {
        if (queryPool.Handle == 0)
            return;

        lock (ResourceRuntime.Lifetime.Tracker.SyncRoot)
        {
            if (ResourceRuntime.Lifetime.Tracker.RenderQueriesByPool.TryGetValue(queryPool.Handle, out List<VkRenderQuery>? queries))
            {
                queries.Remove(query);
                if (queries.Count == 0)
                    ResourceRuntime.Lifetime.Tracker.RenderQueriesByPool.Remove(queryPool.Handle);
            }
        }
    }

    private EVulkanLifetimeQueueDomain ResolveVulkanLifetimeQueueDomain(Queue queue)
    {
        if (queue.Handle == _deviceContext.GraphicsQueue.Handle || queue.Handle == _deviceContext.SecondaryGraphicsQueue.Handle)
            return EVulkanLifetimeQueueDomain.Graphics;
        if (queue.Handle == _deviceContext.TransferQueue.Handle)
            return _deviceContext.TransferQueue.Handle == _deviceContext.GraphicsQueue.Handle
                ? EVulkanLifetimeQueueDomain.Graphics
                : EVulkanLifetimeQueueDomain.Transfer;
        return EVulkanLifetimeQueueDomain.Other;
    }

    internal static void MarkVulkanResourceSubmitted_NoLock(
        VulkanResourceLifetimeRecord resource,
        EVulkanLifetimeQueueDomain domain,
        ulong queueSequence,
        ulong submissionSerial,
        ulong frameOpContextId,
        string? frameOpKind)
    {
        resource.State &= ~EVulkanResourceLifetimeState.Completed;
        resource.State |= EVulkanResourceLifetimeState.Submitted;
        resource.LastSubmissionSerial = submissionSerial;
        resource.LastFrameOpContextId = frameOpContextId;
        resource.LastFrameOpKind = frameOpKind;
        resource.Pins.MarkSubmitted(domain, queueSequence);
    }

    private void ResolveSubmissionTimelineSignal(
        ref SubmitInfo submitInfo,
        out ulong semaphoreHandle,
        out ulong timelineValue)
    {
        semaphoreHandle = 0;
        timelineValue = 0;
        TimelineSemaphoreSubmitInfo* timelineInfo = FindTimelineSemaphoreSubmitInfo(submitInfo.PNext);
        if (timelineInfo is null ||
            timelineInfo->SignalSemaphoreValueCount == 0 ||
            timelineInfo->PSignalSemaphoreValues is null ||
            submitInfo.PSignalSemaphores is null)
        {
            return;
        }

        uint count = Math.Min(timelineInfo->SignalSemaphoreValueCount, submitInfo.SignalSemaphoreCount);
        for (uint i = 0; i < count; i++)
        {
            ulong value = timelineInfo->PSignalSemaphoreValues[i];
            Semaphore semaphore = submitInfo.PSignalSemaphores[i];
            if (value == 0 || semaphore.Handle == 0)
                continue;

            if (semaphore.Handle == _commandRuntime.Synchronization._graphicsTimelineSemaphore.Handle ||
                semaphore.Handle == _commandRuntime.Synchronization._transferTimelineSemaphore.Handle ||
                semaphore.Handle == _commandRuntime.Synchronization._presentTimelineSemaphore.Handle)
            {
                semaphoreHandle = semaphore.Handle;
                timelineValue = value;
                return;
            }
        }
    }

    /// <summary>
    /// Publishes the completion proof supplied by a successful native fence wait.
    /// </summary>
    internal void NotifyVulkanFenceCompleted(Fence fence)
    {
        if (fence.Handle == 0)
            return;

        ulong handle = unchecked((ulong)fence.Handle);
        lock (ResourceRuntime.Lifetime.Tracker.SyncRoot)
        {
            for (int i = ResourceRuntime.Lifetime.Tracker.LifetimeSubmissions.Count - 1; i >= 0; i--)
            {
                VulkanLifetimeSubmission submission = ResourceRuntime.Lifetime.Tracker.LifetimeSubmissions[i];
                if (submission.FenceHandle != handle)
                    continue;

                MarkVulkanQueueSequenceCompleted_NoLock(submission.QueueDomain, submission.QueueSequence);
                ResourceRuntime.Lifetime.Tracker.LifetimeSubmissions.RemoveAt(i);
            }
        }
        AdvanceCompletedImageLayouts();
    }

    /// <summary>
    /// Waits for one tracked submission without draining its queue or the device.
    /// Explicit blocking query reads use this to distinguish the current queued
    /// reset/query epoch from stale host-visible availability.
    /// </summary>
    internal bool WaitForVulkanSubmissionCompletion(
        in VulkanLifetimeSubmission submission,
        string reason)
    {
        if (_deviceLost)
            return false;

        if (submission.TimelineSemaphoreHandle != 0 && submission.TimelineValue != 0)
        {
            Semaphore semaphore = new(submission.TimelineSemaphoreHandle);
            WaitForTimelineValue(semaphore, submission.TimelineValue);
            return !_deviceLost;
        }

        if (submission.FenceHandle == 0)
        {
            Debug.VulkanWarning(
                "[Vulkan] Cannot wait for tracked submission {0}/{1} ({2}): no timeline or fence completion primitive was published.",
                submission.QueueDomain,
                submission.QueueSequence,
                reason);
            return false;
        }

        Fence fence = new(submission.FenceHandle);
        long waitStart = Stopwatch.GetTimestamp();
        while (true)
        {
            Result waitResult;
            using (VulkanCpuStageScope fenceWaitStage =
                new(_frameTelemetry, EVulkanCpuStage.AuxiliaryFenceWait))
            {
                waitResult = Api!.WaitForFences(
                    _deviceContext.Device,
                    1,
                    &fence,
                    true,
                    TimelineWaitPollTimeoutNanoseconds);
            }
            if (waitResult == Result.Success)
            {
                NotifyVulkanFenceCompleted(fence);
                return true;
            }

            if (waitResult == Result.Timeout)
            {
                Debug.VulkanWarningEvery(
                    $"Vulkan.SubmissionFenceWait.{GetHashCode()}.{submission.FenceHandle:X}",
                    TimeSpan.FromSeconds(1),
                    "[Vulkan] Still waiting for tracked submission {0}/{1} ({2}). WaitedMs={3:F1}",
                    submission.QueueDomain,
                    submission.QueueSequence,
                    reason,
                    Stopwatch.GetElapsedTime(waitStart).TotalMilliseconds);
                continue;
            }

            RecordFirstFailingVulkanApi($"vkWaitForFences:{reason}:{waitResult}");
            if (waitResult == Result.ErrorDeviceLost)
                MarkDeviceLost(
                    $"WaitForFences for tracked submission ({reason}) returned ErrorDeviceLost",
                    "vkWaitForFences.TrackedSubmission",
                    waitResult);
            else
                Debug.VulkanWarning(
                    "[Vulkan] Waiting for tracked submission {0}/{1} ({2}) failed: {3}.",
                    submission.QueueDomain,
                    submission.QueueSequence,
                    reason,
                    waitResult);
            return false;
        }
    }

    internal bool IsVulkanSubmissionCompleted(in VulkanLifetimeSubmission submission)
    {
        if (submission.QueueSequence == 0ul)
            return false;

        lock (ResourceRuntime.Lifetime.Tracker.SyncRoot)
        {
            return submission.QueueDomain switch
            {
                EVulkanLifetimeQueueDomain.Graphics => submission.QueueSequence <= ResourceRuntime.Lifetime.Tracker.CompletedGraphicsSequence,
                EVulkanLifetimeQueueDomain.Transfer => submission.QueueSequence <= ResourceRuntime.Lifetime.Tracker.CompletedTransferSequence,
                _ => submission.QueueSequence <= ResourceRuntime.Lifetime.Tracker.CompletedOtherSequence,
            };
        }
    }

    private void NotifyVulkanTimelineCompleted(Semaphore semaphore, ulong value)
    {
        if (semaphore.Handle == 0 || value == 0)
            return;

        ulong handle = semaphore.Handle;
        lock (ResourceRuntime.Lifetime.Tracker.SyncRoot)
        {
            for (int i = ResourceRuntime.Lifetime.Tracker.LifetimeSubmissions.Count - 1; i >= 0; i--)
            {
                VulkanLifetimeSubmission submission = ResourceRuntime.Lifetime.Tracker.LifetimeSubmissions[i];
                if (submission.TimelineSemaphoreHandle != handle ||
                    submission.TimelineValue == 0 ||
                    submission.TimelineValue > value)
                {
                    continue;
                }

                MarkVulkanQueueSequenceCompleted_NoLock(submission.QueueDomain, submission.QueueSequence);
                ResourceRuntime.Lifetime.Tracker.LifetimeSubmissions.RemoveAt(i);
            }
        }
        AdvanceCompletedImageLayouts();
    }

    private void NotifyVulkanQueueIdle(Queue queue)
    {
        ulong queueHandle = unchecked((ulong)queue.Handle);
        lock (ResourceRuntime.Lifetime.Tracker.SyncRoot)
        {
            for (int i = ResourceRuntime.Lifetime.Tracker.LifetimeSubmissions.Count - 1; i >= 0; i--)
            {
                VulkanLifetimeSubmission submission = ResourceRuntime.Lifetime.Tracker.LifetimeSubmissions[i];
                if (submission.QueueHandle != queueHandle)
                    continue;

                MarkVulkanQueueSequenceCompleted_NoLock(submission.QueueDomain, submission.QueueSequence);
                ResourceRuntime.Lifetime.Tracker.LifetimeSubmissions.RemoveAt(i);
            }
        }
        AdvanceCompletedImageLayouts();
    }

    private void NotifyVulkanDeviceIdle()
    {
        lock (ResourceRuntime.Lifetime.Tracker.SyncRoot)
        {
            ResourceRuntime.Lifetime.Tracker.CompletedGraphicsSequence = ResourceRuntime.Lifetime.Tracker.LastGraphicsSequence;
            ResourceRuntime.Lifetime.Tracker.CompletedTransferSequence = ResourceRuntime.Lifetime.Tracker.LastTransferSequence;
            ResourceRuntime.Lifetime.Tracker.CompletedOtherSequence = ResourceRuntime.Lifetime.Tracker.LastOtherSequence;
            ResourceRuntime.Lifetime.Tracker.LifetimeSubmissions.Clear();
        }
        AdvanceCompletedImageLayouts();
    }

    private void NotifyVulkanResourceLifetimeDeviceLost()
    {
        lock (ResourceRuntime.Lifetime.Tracker.SyncRoot)
            ResourceRuntime.Lifetime.Tracker.DeviceLost = true;
    }

    private void MarkVulkanQueueSequenceCompleted_NoLock(
        EVulkanLifetimeQueueDomain domain,
        ulong queueSequence)
        => ResourceRuntime.Lifetime.Tracker.MarkQueueSequenceCompletedNoLock(domain, queueSequence);

    private VulkanRetirementTicket CaptureVulkanRetirementTicket(
        ObjectType type,
        ulong handle,
        string owner)
    {
        if (handle == 0)
            return VulkanRetirementTicket.None;

        VulkanResourceLifetimeKey key = ResourceKey(type, handle);
        ResourceRuntime.Lifetime.Tracker.FenceResourceRecordingAdmission(key, owner);
        PublishCommandBufferTrackingDependenciesBeforeResourceRetirement(key);

        if (type == ObjectType.Image)
            RetireImageViewsForBackingImage(handle);

        VulkanRetirementTicket ticket;
        ulong generation;
        string resourceOwner;
        ulong[] dependentCommandBuffers = [];
        int invalidatedDescriptorSetCount = 0;
        lock (ResourceRuntime.Lifetime.Tracker.SyncRoot)
        {
            VulkanResourceLifetimeRecord resource = GetOrRegisterVulkanResource_NoLock(key, owner);
            generation = resource.Generation;
            resourceOwner = resource.Owner;
            if ((resource.State & EVulkanResourceLifetimeState.Destroyed) != 0)
            {
                ticket = resource.RetirementTicket;
            }
            else if ((resource.State & EVulkanResourceLifetimeState.PendingRetirement) != 0)
            {
                ticket = resource.RetirementTicket;
            }
            else
            {
                UpdateVulkanResourceCompletionState_NoLock(resource);
                ticket = new VulkanRetirementTicket(
                    resource.Pins.LastGraphicsSequence,
                    resource.Pins.LastTransferSequence,
                    resource.Pins.LastOtherSequence,
                    Stopwatch.GetTimestamp(),
                    resource.Generation,
                    (resource.State & EVulkanResourceLifetimeState.External) != 0,
                    VulkanRetirementPinSet.Single(key, resource.Generation));
                resource.RetirementSerial = unchecked((ulong)Interlocked.Increment(ref ResourceRuntime.Lifetime.Tracker.RetirementSerial));
                resource.State |= EVulkanResourceLifetimeState.PendingRetirement;
                ResourceRuntime.Lifetime.Tracker.PublishedResourceGenerations[key] = 0;
                resource.RetirementTicket = ticket;
                invalidatedDescriptorSetCount = InvalidateVulkanDescriptorSetsReferencingResource_NoLock(key);
                if (ResourceRuntime.Lifetime.Tracker.ResourceCommandBufferDependencies.TryGetValue(key, out HashSet<ulong>? dependents) &&
                    dependents.Count > 0)
                {
                    dependentCommandBuffers = new ulong[dependents.Count];
                    int dependentIndex = 0;
                    foreach (ulong commandBufferHandle in dependents)
                    {
                        if (ResourceRuntime.Lifetime.Tracker.CommandBufferLifetimes.TryGetValue(commandBufferHandle, out VulkanCommandBufferLifetimeRecord? lifetime) &&
                            lifetime.Dependencies.TryGetValue(key, out ulong recordedGeneration) &&
                            recordedGeneration == generation)
                        {
                            dependentCommandBuffers[dependentIndex++] = commandBufferHandle;
                        }
                    }

                    if (dependentIndex != dependentCommandBuffers.Length)
                        Array.Resize(ref dependentCommandBuffers, dependentIndex);
                }
            }
        }

        if (dependentCommandBuffers.Length > 0)
            InvalidateCachedCommandBuffersForRetiringResource(key, generation, resourceOwner, dependentCommandBuffers);
        if (invalidatedDescriptorSetCount > 0)
        {
            Debug.VulkanEvery(
                $"Vulkan.ResourceLifetime.TargetedDescriptorInvalidation.{key.Type}",
                TimeSpan.FromSeconds(1),
                "[Vulkan.ResourceLifetime] Targeted descriptor invalidation resource={0} generation={1} descriptorSets={2}.",
                key,
                generation,
                invalidatedDescriptorSetCount);
        }
        return ticket;
    }

    private int InvalidateVulkanDescriptorSetsReferencingResource_NoLock(VulkanResourceLifetimeKey key)
    {
        if (!ResourceRuntime.Lifetime.Tracker.DescriptorSetsByReferencedResource.TryGetValue(key, out HashSet<ulong>? descriptorSets) ||
            descriptorSets.Count == 0)
        {
            return 0;
        }

        int invalidated = 0;
        foreach (ulong descriptorSetHandle in descriptorSets)
        {
            if (!ResourceRuntime.Lifetime.Tracker.DescriptorSetLifetimes.TryGetValue(
                    descriptorSetHandle,
                    out VulkanDescriptorSetLifetimeRecord? state))
            {
                continue;
            }

            state.Generation++;
            PublishVulkanDescriptorSetSnapshot_NoLock(descriptorSetHandle, state);
            invalidated++;
        }

        return invalidated;
    }

    private void InvalidateCachedCommandBuffersForRetiringResource(
        VulkanResourceLifetimeKey key,
        ulong generation,
        string resourceOwner,
        ReadOnlySpan<ulong> dependentCommandBuffers)
    {
        VulkanExactInvalidationResult result = InvalidateCachedCommandBuffersByHandle(
            dependentCommandBuffers,
            $"retiring {key} generation {generation}");
        RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanExactResourceInvalidation(
            result.ExactVariantsDirtied,
            result.ExactCommandChainsDirtied,
            result.UnrelatedVariantsPreserved,
            result.GlobalFallbackInvalidations);
        Debug.VulkanEvery(
            $"Vulkan.ResourceLifetime.RetirementInvalidation.{key.Type}",
            TimeSpan.FromSeconds(1),
            "[Vulkan.ResourceLifetime] Exact retirement invalidation resource={0} generation={1} owner={2} dependentCommandBuffers={3} variantsDirtied={4} chainsDirtied={5} unrelatedVariantsPreserved={6} globalFallbacks={7}.",
            key,
            generation,
            resourceOwner,
            dependentCommandBuffers.Length,
            result.ExactVariantsDirtied,
            result.ExactCommandChainsDirtied,
            result.UnrelatedVariantsPreserved,
            result.GlobalFallbackInvalidations);
    }

    private VulkanRetirementTicket CaptureVulkanRetirementWatermark()
        => ResourceRuntime.Lifetime.Tracker.CaptureRetirementWatermark();

    private VulkanRetirementTicket CaptureVulkanDescriptorPoolRetirementTicket(
        DescriptorPool pool,
        string owner)
    {
        VulkanRetirementTicket ticket = CaptureVulkanRetirementTicket(
            ObjectType.DescriptorPool,
            pool.Handle,
            owner);
        if (pool.Handle == 0)
            return ticket;

        ulong poolGeneration = ticket.ResourceGeneration;
        ulong[] ownedSetHandles;
        lock (ResourceRuntime.Lifetime.Tracker.SyncRoot)
        {
            ownedSetHandles = ResourceRuntime.Lifetime.Tracker.DescriptorSetsByPool.TryGetValue(
                pool.Handle,
                out HashSet<ulong>? trackedSets)
                    ? [.. trackedSets]
                    : [];
        }

        // A pool destroy implicitly destroys every set allocated from it. Close the
        // command-local publication window for each owned set before any of them is
        // marked pending retirement, just as the single-resource path does.
        for (int i = 0; i < ownedSetHandles.Length; i++)
        {
            PublishCommandBufferTrackingDependenciesBeforeResourceRetirement(
                ResourceKey(ObjectType.DescriptorSet, ownedSetHandles[i]));
        }

        HashSet<ulong>? dependentCommandBuffers = null;
        lock (ResourceRuntime.Lifetime.Tracker.SyncRoot)
        {
            if (!ResourceRuntime.Lifetime.Tracker.DescriptorSetsByPool.TryGetValue(pool.Handle, out HashSet<ulong>? ownedSets) ||
                ownedSets.Count == 0)
            {
                return ticket;
            }

            long enqueuedTimestamp = Stopwatch.GetTimestamp();
            foreach (ulong setHandle in ownedSets)
            {
                VulkanResourceLifetimeKey setKey = ResourceKey(ObjectType.DescriptorSet, setHandle);
                VulkanResourceLifetimeRecord setResource = GetOrRegisterVulkanResource_NoLock(
                    setKey,
                    $"{owner}.DescriptorSet");
                VulkanRetirementTicket setTicket;
                if ((setResource.State & (EVulkanResourceLifetimeState.PendingRetirement | EVulkanResourceLifetimeState.Destroyed)) != 0)
                {
                    setTicket = setResource.RetirementTicket;
                }
                else
                {
                    UpdateVulkanResourceCompletionState_NoLock(setResource);
                    setTicket = new VulkanRetirementTicket(
                        setResource.Pins.LastGraphicsSequence,
                        setResource.Pins.LastTransferSequence,
                        setResource.Pins.LastOtherSequence,
                        enqueuedTimestamp,
                        setResource.Generation,
                        false,
                        VulkanRetirementPinSet.Single(setResource.Key, setResource.Generation));
                    setResource.RetirementSerial = unchecked((ulong)Interlocked.Increment(ref ResourceRuntime.Lifetime.Tracker.RetirementSerial));
                    setResource.State |= EVulkanResourceLifetimeState.PendingRetirement;
                    setResource.RetirementTicket = setTicket;

                    if (ResourceRuntime.Lifetime.Tracker.ResourceCommandBufferDependencies.TryGetValue(setKey, out HashSet<ulong>? dependents))
                    {
                        foreach (ulong commandBufferHandle in dependents)
                        {
                            if (ResourceRuntime.Lifetime.Tracker.CommandBufferLifetimes.TryGetValue(commandBufferHandle, out VulkanCommandBufferLifetimeRecord? lifetime) &&
                                lifetime.Dependencies.TryGetValue(setKey, out ulong recordedGeneration) &&
                                recordedGeneration == setResource.Generation)
                            {
                                (dependentCommandBuffers ??= []).Add(commandBufferHandle);
                            }
                        }
                    }
                }

                ticket = ticket.Merge(setTicket);
            }
        }

        // One aggregate exact invalidation avoids both global cache teardown and an
        // invalidation/logging pass for every descriptor set in a large pool.
        if (dependentCommandBuffers is { Count: > 0 })
        {
            ulong[] handles = [.. dependentCommandBuffers];
            InvalidateCachedCommandBuffersForRetiringResource(
                ResourceKey(ObjectType.DescriptorPool, pool.Handle),
                poolGeneration,
                owner,
                handles);
        }

        return ticket;
    }

    private bool IsVulkanRetirementReady(in VulkanRetirementTicket ticket)
        => ResourceRuntime.Lifetime.Tracker.IsRetirementReady(ticket);

    /// <summary>
    /// Command buffers have CPU-side recording and queue-gateway owners that are
    /// not represented by GPU completion pins. Pending retirement prevents a new
    /// recording from starting, so once these owners are clear the native free
    /// cannot race another recording acquisition.
    /// </summary>
    private bool IsVulkanCommandBufferRetirementReady(
        CommandBuffer commandBuffer,
        in VulkanRetirementTicket ticket)
    {
        ulong handle = unchecked((ulong)commandBuffer.Handle);
        if (handle == 0)
            return false;

        lock (ResourceRuntime.Lifetime.Tracker.SyncRoot)
        {
            if (ResourceRuntime.Lifetime.Tracker.ForcedRetirementDrainDepth > 0)
                return true;

            if (_commandBufferTrackingBatches.TryGetValue(
                    handle,
                    out VulkanCommandBufferTrackingBatch? batch))
            {
                lock (batch)
                {
                    if (batch.IsRecording || batch.QueuedSubmissionCount != 0)
                        return false;
                }
            }

            if (ResourceRuntime.Lifetime.Tracker.CommandBufferLifetimes.TryGetValue(
                    handle,
                    out VulkanCommandBufferLifetimeRecord? lifetime) &&
                lifetime.QueuedSubmissionCount != 0)
            {
                return false;
            }

            return IsVulkanRetirementReady_NoLock(ticket);
        }
    }

    /// <summary>
    /// Converts every currently allocated child into a retirement generation before
    /// its pool can be destroyed. Vulkan destroys these children implicitly with
    /// the pool, but their own submitted work and CPU recording owners remain
    /// independently relevant until that point.
    /// </summary>
    private VulkanRetirementTicket CaptureCommandPoolChildRetirementTicket(
        CommandPool commandPool,
        VulkanRetirementTicket ticket)
    {
        if (commandPool.Handle == 0)
            return ticket;

        VulkanResourceLifetimeKey poolKey = ResourceKey(ObjectType.CommandPool, commandPool.Handle);
        ulong poolGeneration;
        CommandBuffer[] children;
        lock (ResourceRuntime.Lifetime.Tracker.SyncRoot)
        {
            if (!ResourceRuntime.Lifetime.Tracker.ResourceLifetimes.TryGetValue(
                    poolKey,
                    out VulkanResourceLifetimeRecord? pool) ||
                pool.Generation == 0 ||
                !ResourceRuntime.Lifetime.Tracker.CommandBuffersByPool.TryGetValue(poolKey, out HashSet<ulong>? ownedChildren))
            {
                return ticket;
            }

            poolGeneration = pool.Generation;
            children = new CommandBuffer[ownedChildren.Count];
            int index = 0;
            foreach (ulong childHandle in ownedChildren)
            {
                if (ResourceRuntime.Lifetime.Tracker.CommandBufferLifetimes.TryGetValue(
                        childHandle,
                        out VulkanCommandBufferLifetimeRecord? child) &&
                    child.AllocatingCommandPool == poolKey &&
                    child.AllocatingCommandPoolGeneration == poolGeneration)
                {
                    children[index++] = new CommandBuffer { Handle = unchecked((nint)childHandle) };
                }
            }

            if (index != children.Length)
                Array.Resize(ref children, index);
        }

        for (int i = 0; i < children.Length; i++)
        {
            CommandBuffer child = children[i];
            VulkanRetirementTicket childTicket = CaptureVulkanRetirementTicket(
                ObjectType.CommandBuffer,
                unchecked((ulong)child.Handle),
                $"CommandPool.Child.0x{commandPool.Handle:X}");
            ticket = ticket.Merge(childTicket);
        }

        return ticket;
    }

    /// <summary>
    /// The merged pool ticket covers every child GPU completion point. This check
    /// covers the separate CPU-side recording and submission-gateway owners.
    /// </summary>
    private bool AreCommandPoolChildrenRetirementReady(CommandPool commandPool)
    {
        if (commandPool.Handle == 0)
            return true;

        VulkanResourceLifetimeKey poolKey = ResourceKey(ObjectType.CommandPool, commandPool.Handle);
        CommandBuffer[] children;
        lock (ResourceRuntime.Lifetime.Tracker.SyncRoot)
        {
            if (!ResourceRuntime.Lifetime.Tracker.CommandBuffersByPool.TryGetValue(poolKey, out HashSet<ulong>? ownedChildren) ||
                ownedChildren.Count == 0)
            {
                return true;
            }

            children = new CommandBuffer[ownedChildren.Count];
            int index = 0;
            foreach (ulong childHandle in ownedChildren)
            {
                if (ResourceRuntime.Lifetime.Tracker.CommandBufferLifetimes.ContainsKey(childHandle))
                    children[index++] = new CommandBuffer { Handle = unchecked((nint)childHandle) };
            }
            if (index != children.Length)
                Array.Resize(ref children, index);
        }

        for (int i = 0; i < children.Length; i++)
            // A separately retired child must drain through vkFreeCommandBuffers
            // first. Otherwise its queued retirement entry could later free a
            // handle that vkDestroyCommandPool has already invalidated.
            if (IsCommandBufferPendingRetirement(children[i]) ||
                !IsVulkanCommandBufferRetirementReady(children[i], VulkanRetirementTicket.None))
                return false;

        return true;
    }

    /// <summary>
    /// Called immediately after native pool destruction. The Vulkan API has now
    /// freed the children implicitly, so retire their cached bind state and exact
    /// ownership relations before publishing the pool as destroyed.
    /// </summary>
    private void CompleteCommandPoolChildDestructions(CommandPool commandPool)
    {
        if (commandPool.Handle == 0)
            return;

        VulkanResourceLifetimeKey poolKey = ResourceKey(ObjectType.CommandPool, commandPool.Handle);
        CommandBuffer[] children;
        lock (ResourceRuntime.Lifetime.Tracker.SyncRoot)
        {
            if (!ResourceRuntime.Lifetime.Tracker.CommandBuffersByPool.TryGetValue(poolKey, out HashSet<ulong>? ownedChildren) ||
                ownedChildren.Count == 0)
            {
                return;
            }

            children = new CommandBuffer[ownedChildren.Count];
            int index = 0;
            foreach (ulong childHandle in ownedChildren)
                children[index++] = new CommandBuffer { Handle = unchecked((nint)childHandle) };
        }

        for (int i = 0; i < children.Length; i++)
        {
            CommandBuffer child = children[i];
            RemoveCommandBufferBindState(child);
            CompleteVulkanResourceDestruction(
                ObjectType.CommandBuffer,
                unchecked((ulong)child.Handle));
        }
    }

    private bool TryBeginDestroyVulkanResourceGeneration(
        ObjectType type,
        ulong handle,
        ulong expectedGeneration,
        string owner)
    {
        if (handle == 0 || expectedGeneration == 0)
            return false;

        VulkanResourceLifetimeKey key = ResourceKey(type, handle);
        lock (ResourceRuntime.Lifetime.Tracker.SyncRoot)
        {
            bool forced = ResourceRuntime.Lifetime.Tracker.ForcedRetirementDrainDepth > 0;
            if (!ResourceRuntime.Lifetime.Tracker.ResourceLifetimes.TryGetValue(key, out VulkanResourceLifetimeRecord? resource) ||
                resource.Generation != expectedGeneration ||
                (resource.State & EVulkanResourceLifetimeState.Destroyed) != 0 ||
                (!forced &&
                 (!IsVulkanRetirementReady_NoLock(resource.RetirementTicket) ||
                  !resource.Pins.IsRetirementReady(
                      ResourceRuntime.Lifetime.Tracker.CompletedGraphicsSequence,
                      ResourceRuntime.Lifetime.Tracker.CompletedTransferSequence,
                      ResourceRuntime.Lifetime.Tracker.CompletedOtherSequence))))
            {
                Debug.VulkanEvery(
                    $"Vulkan.ResourceLifetime.SkipStaleDestroy.{type}.{handle}.{expectedGeneration}.{owner}",
                    TimeSpan.FromSeconds(5),
                    "[Vulkan.ResourceLifetime] Skipping stale or premature destroy: resource={0} expectedGeneration={1} currentGeneration={2} state={3} owner={4}.",
                    key,
                    expectedGeneration,
                    resource?.Generation ?? 0UL,
                    resource?.State ?? EVulkanResourceLifetimeState.None,
                    owner);
                return false;
            }

            return true;
        }
    }

    private bool HasUndestroyedVulkanBufferViewReference(Silk.NET.Vulkan.Buffer buffer)
    {
        if (buffer.Handle == 0)
            return false;

        lock (ResourceRuntime.Lifetime.Tracker.SyncRoot)
        {
            foreach ((ulong viewHandle, ulong backingBufferHandle) in ResourceRuntime.Lifetime.Tracker.BufferViewBackingBuffers)
            {
                if (backingBufferHandle != buffer.Handle)
                    continue;

                if (!ResourceRuntime.Lifetime.Tracker.ResourceLifetimes.TryGetValue(
                        ResourceKey(ObjectType.BufferView, viewHandle),
                        out VulkanResourceLifetimeRecord? view) ||
                    (view.State & EVulkanResourceLifetimeState.Destroyed) == 0)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private bool HasUndestroyedVulkanImageDependency(in RetiredImageResources resources)
    {
        if (resources.Image.Handle == 0 &&
            resources.PrimaryView.Handle == 0 &&
            (resources.AttachmentViews is null || resources.AttachmentViews.Length == 0))
        {
            return false;
        }

        lock (ResourceRuntime.Lifetime.Tracker.SyncRoot)
        {
            if (resources.Image.Handle != 0)
            {
                foreach ((ulong viewHandle, ulong backingImageHandle) in ResourceRuntime.Lifetime.Tracker.ImageViewBackingImages)
                {
                    if (backingImageHandle != resources.Image.Handle ||
                        ContainsRetiredImageView(resources, viewHandle))
                    {
                        continue;
                    }

                    if (!ResourceRuntime.Lifetime.Tracker.ResourceLifetimes.TryGetValue(
                            ResourceKey(ObjectType.ImageView, viewHandle),
                            out VulkanResourceLifetimeRecord? view) ||
                        (view.State & EVulkanResourceLifetimeState.Destroyed) == 0)
                    {
                        return true;
                    }
                }
            }

            foreach ((ulong framebufferHandle, VulkanResourceLifetimeKey[] attachments) in ResourceRuntime.Lifetime.Tracker.FramebufferAttachments)
            {
                if (ResourceRuntime.Lifetime.Tracker.ResourceLifetimes.TryGetValue(
                        ResourceKey(ObjectType.Framebuffer, framebufferHandle),
                        out VulkanResourceLifetimeRecord? framebuffer) &&
                    (framebuffer.State & EVulkanResourceLifetimeState.Destroyed) != 0)
                {
                    continue;
                }

                for (int i = 0; i < attachments.Length; i++)
                {
                    VulkanResourceLifetimeKey attachment = attachments[i];
                    if (ContainsRetiredImageView(resources, attachment.Handle) ||
                        (resources.Image.Handle != 0 &&
                         ResourceRuntime.Lifetime.Tracker.ImageViewBackingImages.TryGetValue(attachment.Handle, out ulong backingImageHandle) &&
                         backingImageHandle == resources.Image.Handle))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    private static bool ContainsRetiredImageView(in RetiredImageResources resources, ulong viewHandle)
    {
        if (viewHandle == 0)
            return false;
        if (resources.PrimaryView.Handle == viewHandle)
            return true;

        ImageView[]? attachmentViews = resources.AttachmentViews;
        if (attachmentViews is null)
            return false;
        for (int i = 0; i < attachmentViews.Length; i++)
        {
            if (attachmentViews[i].Handle == viewHandle)
                return true;
        }

        return false;
    }

    private bool UpdateVulkanResourceCompletionState_NoLock(VulkanResourceLifetimeRecord resource)
    {
        bool completed = resource.Pins.LastGraphicsSequence <= ResourceRuntime.Lifetime.Tracker.CompletedGraphicsSequence &&
            resource.Pins.LastTransferSequence <= ResourceRuntime.Lifetime.Tracker.CompletedTransferSequence &&
            resource.Pins.LastOtherSequence <= ResourceRuntime.Lifetime.Tracker.CompletedOtherSequence;
        if (!completed)
            return false;

        if ((resource.State & EVulkanResourceLifetimeState.Submitted) != 0)
        {
            resource.State &= ~EVulkanResourceLifetimeState.Submitted;
            resource.State |= EVulkanResourceLifetimeState.Completed;
        }

        return true;
    }

    /// <summary>
    /// Returns whether every submitted use of a tracked Vulkan resource has completed.
    /// Query pools use this as their epoch boundary: host-visible availability from a
    /// previous use is not proof that the queued reset and current query have executed.
    /// </summary>
    private bool IsVulkanResourceUseCompleted(ObjectType type, ulong handle)
    {
        if (handle == 0)
            return false;

        lock (ResourceRuntime.Lifetime.Tracker.SyncRoot)
        {
            if (!ResourceRuntime.Lifetime.Tracker.ResourceLifetimes.TryGetValue(
                    ResourceKey(type, handle),
                    out VulkanResourceLifetimeRecord? resource))
            {
                return false;
            }

            return UpdateVulkanResourceCompletionState_NoLock(resource);
        }
    }

    private void EnsureVulkanResourceMutationAllowed(
        ObjectType type,
        ulong handle,
        string operation,
        bool allowWhileInFlight = false)
    {
        if (handle == 0)
            return;

        VulkanResourceLifetimeKey key = ResourceKey(type, handle);
        lock (ResourceRuntime.Lifetime.Tracker.SyncRoot)
        {
            VulkanResourceLifetimeRecord resource = GetOrRegisterVulkanResource_NoLock(key, operation);
            if ((resource.State & (EVulkanResourceLifetimeState.PendingRetirement | EVulkanResourceLifetimeState.Destroyed)) != 0)
                throw new InvalidOperationException($"Cannot perform {operation} on retired Vulkan resource {key}.");

            if (!allowWhileInFlight && !UpdateVulkanResourceCompletionState_NoLock(resource))
            {
                throw new InvalidOperationException(
                    $"Cannot perform {operation} on in-flight Vulkan resource {key}; graphics={resource.Pins.LastGraphicsSequence}/{ResourceRuntime.Lifetime.Tracker.CompletedGraphicsSequence} transfer={resource.Pins.LastTransferSequence}/{ResourceRuntime.Lifetime.Tracker.CompletedTransferSequence} other={resource.Pins.LastOtherSequence}/{ResourceRuntime.Lifetime.Tracker.CompletedOtherSequence}.");
            }
        }
    }

    private void NotifyVulkanResourceUseCompleted(ObjectType type, ulong handle)
    {
        if (handle == 0)
            return;

        lock (ResourceRuntime.Lifetime.Tracker.SyncRoot)
        {
            if (!ResourceRuntime.Lifetime.Tracker.ResourceLifetimes.TryGetValue(
                    ResourceKey(type, handle),
                    out VulkanResourceLifetimeRecord? resource))
            {
                return;
            }

            resource.Pins.ResetCompletion();
            resource.State &= ~EVulkanResourceLifetimeState.Submitted;
            resource.State |= EVulkanResourceLifetimeState.Completed;
        }
    }

    private bool CanMutateVulkanDescriptorPool(DescriptorPool pool, out string reason)
    {
        if (pool.Handle == 0)
        {
            reason = "descriptor pool handle is null";
            return false;
        }

        lock (ResourceRuntime.Lifetime.Tracker.SyncRoot)
        {
            VulkanResourceLifetimeRecord poolResource = GetOrRegisterVulkanResource_NoLock(
                ResourceKey(ObjectType.DescriptorPool, pool.Handle),
                "DescriptorPool.Mutation");
            if ((poolResource.State & (EVulkanResourceLifetimeState.PendingRetirement | EVulkanResourceLifetimeState.Destroyed)) != 0)
            {
                reason = $"descriptor pool 0x{pool.Handle:X} is retired";
                return false;
            }

            if (!ResourceRuntime.Lifetime.Tracker.DescriptorSetsByPool.TryGetValue(pool.Handle, out HashSet<ulong>? ownedSets))
                ownedSets = [];

            foreach (ulong setHandle in ownedSets)
            {
                VulkanResourceLifetimeRecord setResource = GetOrRegisterVulkanResource_NoLock(
                    ResourceKey(ObjectType.DescriptorSet, setHandle),
                    "DescriptorPool.Mutation.Set");
                if (!UpdateVulkanResourceCompletionState_NoLock(setResource))
                {
                    reason =
                        $"descriptor set 0x{setHandle:X} is in flight at graphics={setResource.Pins.LastGraphicsSequence}/{ResourceRuntime.Lifetime.Tracker.CompletedGraphicsSequence} transfer={setResource.Pins.LastTransferSequence}/{ResourceRuntime.Lifetime.Tracker.CompletedTransferSequence} other={setResource.Pins.LastOtherSequence}/{ResourceRuntime.Lifetime.Tracker.CompletedOtherSequence}";
                    return false;
                }
            }
        }

        reason = string.Empty;
        return true;
    }

    private Result ResetVulkanDescriptorPoolTracked(DescriptorPool pool)
    {
        if (!CanMutateVulkanDescriptorPool(pool, out string reason))
        {
            Debug.VulkanEvery(
                $"Vulkan.DescriptorPool.ResetDeferred.{GetHashCode()}.{pool.Handle}",
                TimeSpan.FromSeconds(1),
                "[Vulkan.ResourceLifetime] Descriptor-pool reset deferred: pool=0x{0:X} reason={1}.",
                pool.Handle,
                reason);
            return Result.NotReady;
        }

        Result result = Api!.ResetDescriptorPool(_deviceContext.Device, pool, 0);
        if (result != Result.Success)
            return result;

        lock (ResourceRuntime.Lifetime.Tracker.SyncRoot)
            RemoveDescriptorSetsOwnedByPool_NoLock(pool.Handle, forced: false);
        return result;
    }

    private void CompleteVulkanResourceDestruction(
        ObjectType type,
        ulong handle,
        bool forced = false)
    {
        if (handle == 0)
            return;

        VulkanResourceLifetimeKey key = ResourceKey(type, handle);
        lock (ResourceRuntime.Lifetime.Tracker.SyncRoot)
        {
            forced |= ResourceRuntime.Lifetime.Tracker.ForcedRetirementDrainDepth > 0;
            if (!ResourceRuntime.Lifetime.Tracker.ResourceLifetimes.TryGetValue(key, out VulkanResourceLifetimeRecord? resource))
                return;

            if (!forced &&
                (!IsVulkanRetirementReady_NoLock(resource.RetirementTicket) ||
                 !resource.Pins.IsRetirementReady(
                     ResourceRuntime.Lifetime.Tracker.CompletedGraphicsSequence,
                     ResourceRuntime.Lifetime.Tracker.CompletedTransferSequence,
                     ResourceRuntime.Lifetime.Tracker.CompletedOtherSequence)))
            {
                throw new InvalidOperationException(
                    $"Attempted to destroy {key} generation {resource.Generation} before its GPU completion point was reached.");
            }

            if (forced)
                Interlocked.Increment(ref ResourceRuntime.Lifetime.Tracker.ForcedResourceDestructionCount);

            resource.State = EVulkanResourceLifetimeState.Destroyed;
            ResourceRuntime.Lifetime.Tracker.ResourceCommandBufferDependencies.Remove(key);
            if (type == ObjectType.ImageView)
                ResourceRuntime.Lifetime.Tracker.ImageViewBackingImages.Remove(handle);
            if (type == ObjectType.BufferView)
                ResourceRuntime.Lifetime.Tracker.BufferViewBackingBuffers.Remove(handle);
            if (type == ObjectType.DescriptorSet)
            {
                RemoveDescriptorSetLifetime_NoLock(handle, forced);
                ResourceRuntime.Lifetime.Tracker.PublishedDescriptorSets.TryRemove(handle, out _);
            }
            if (type == ObjectType.CommandBuffer)
            {
                if (ResourceRuntime.Lifetime.Tracker.CommandBufferLifetimes.Remove(handle, out VulkanCommandBufferLifetimeRecord? lifetime))
                {
                    ReleaseVulkanCommandBufferDependencies_NoLock(handle, lifetime);
                    RemoveVulkanCommandBufferPoolOwnership_NoLock(handle, lifetime);
                }
            }
            if (type == ObjectType.Framebuffer)
                ResourceRuntime.Lifetime.Tracker.FramebufferAttachments.Remove(handle);
            if (type == ObjectType.DescriptorPool)
                RemoveDescriptorSetsOwnedByPool_NoLock(handle, forced);
            if (type == ObjectType.CommandPool)
                ResourceRuntime.Lifetime.Tracker.CommandBuffersByPool.Remove(key);
        }
    }

    private bool IsVulkanRetirementReady_NoLock(in VulkanRetirementTicket ticket)
        => ResourceRuntime.Lifetime.Tracker.IsRetirementReadyNoLock(ticket);

    private void BeginForcedVulkanRetirementDrain()
    {
        lock (ResourceRuntime.Lifetime.Tracker.SyncRoot)
            ResourceRuntime.Lifetime.Tracker.ForcedRetirementDrainDepth++;
    }

    private void EndForcedVulkanRetirementDrain()
    {
        lock (ResourceRuntime.Lifetime.Tracker.SyncRoot)
            ResourceRuntime.Lifetime.Tracker.ForcedRetirementDrainDepth = Math.Max(0, ResourceRuntime.Lifetime.Tracker.ForcedRetirementDrainDepth - 1);
    }

    private void RemoveDescriptorSetsOwnedByPool_NoLock(ulong poolHandle, bool forced)
    {
        if (!ResourceRuntime.Lifetime.Tracker.DescriptorSetsByPool.TryGetValue(poolHandle, out HashSet<ulong>? ownedSets) ||
            ownedSets.Count == 0)
        {
            ResourceRuntime.Lifetime.Tracker.DescriptorSetsByPool.Remove(poolHandle);
            return;
        }

        ulong[] removedSets = [.. ownedSets];
        for (int i = 0; i < removedSets.Length; i++)
            RemoveDescriptorSetLifetime_NoLock(removedSets[i], forced);

        ResourceRuntime.Lifetime.Tracker.DescriptorSetsByPool.Remove(poolHandle);
    }

    private void RemoveDescriptorSetLifetime_NoLock(ulong setHandle, bool forced)
    {
        if (ResourceRuntime.Lifetime.Tracker.DescriptorSetLifetimes.Remove(setHandle, out VulkanDescriptorSetLifetimeRecord? state))
        {
            ReleaseVulkanDescriptorSetGenerationPins_NoLock(state);
            UpdateVulkanDescriptorSetPoolIndex_NoLock(setHandle, state.Pool.Handle, 0);
            foreach (VulkanResourceLifetimeKey reference in state.IndexedReferences)
            {
                if (!ResourceRuntime.Lifetime.Tracker.DescriptorSetsByReferencedResource.TryGetValue(reference, out HashSet<ulong>? sets))
                    continue;

                sets.Remove(setHandle);
                if (sets.Count == 0)
                    ResourceRuntime.Lifetime.Tracker.DescriptorSetsByReferencedResource.Remove(reference);
            }
        }

        ResourceRuntime.Lifetime.Tracker.PublishedDescriptorSets.TryRemove(setHandle, out _);
        if (!ResourceRuntime.Lifetime.Tracker.ResourceLifetimes.TryGetValue(
                ResourceKey(ObjectType.DescriptorSet, setHandle),
                out VulkanResourceLifetimeRecord? setResource))
        {
            return;
        }

        setResource.State = EVulkanResourceLifetimeState.Destroyed;
        if (forced)
            Interlocked.Increment(ref ResourceRuntime.Lifetime.Tracker.ForcedResourceDestructionCount);
    }

    private void ReleaseExternalVulkanResourceOwnership(ObjectType type, ulong handle)
    {
        if (handle == 0)
            return;

        lock (ResourceRuntime.Lifetime.Tracker.SyncRoot)
        {
            if (!ResourceRuntime.Lifetime.Tracker.ResourceLifetimes.TryGetValue(
                    ResourceKey(type, handle),
                    out VulkanResourceLifetimeRecord? resource))
            {
                return;
            }

            resource.State &= ~EVulkanResourceLifetimeState.External;
            resource.RetirementTicket = resource.RetirementTicket with { ExternalOwnershipPending = false };
        }
    }

    /// <summary>
    /// Detaches an externally owned resource generation before an API operation may recycle
    /// its opaque handle for a different native object. Swapchain replacement is allowed to
    /// invalidate old image handles as soon as the new swapchain is created, before the old
    /// swapchain object itself is destroyed.
    /// </summary>
    private ulong DetachExternalVulkanResourceLifetimeForHandleReuse(
        ObjectType type,
        ulong handle,
        string owner)
    {
        if (handle == 0)
            return 0;

        VulkanResourceLifetimeKey key = ResourceKey(type, handle);
        PublishCommandBufferTrackingDependenciesBeforeResourceRetirement(key);

        ulong generation;
        string resourceOwner;
        ulong[] dependentCommandBuffers = [];
        int invalidatedDescriptorSetCount;
        lock (ResourceRuntime.Lifetime.Tracker.SyncRoot)
        {
            if (!ResourceRuntime.Lifetime.Tracker.ResourceLifetimes.TryGetValue(key, out VulkanResourceLifetimeRecord? resource))
                return 0;
            if ((resource.State & EVulkanResourceLifetimeState.External) == 0)
            {
                throw new InvalidOperationException(
                    $"Cannot detach non-external {key} generation {resource.Generation} for handle reuse in {owner}.");
            }

            generation = resource.Generation;
            resourceOwner = resource.Owner;
            invalidatedDescriptorSetCount =
                InvalidateVulkanDescriptorSetsReferencingResource_NoLock(key);
            if (ResourceRuntime.Lifetime.Tracker.ResourceCommandBufferDependencies.TryGetValue(
                    key,
                    out HashSet<ulong>? dependents) &&
                dependents.Count > 0)
            {
                dependentCommandBuffers = new ulong[dependents.Count];
                int dependentIndex = 0;
                foreach (ulong commandBufferHandle in dependents)
                {
                    if (ResourceRuntime.Lifetime.Tracker.CommandBufferLifetimes.TryGetValue(
                            commandBufferHandle,
                            out VulkanCommandBufferLifetimeRecord? lifetime) &&
                        lifetime.Dependencies.TryGetValue(key, out ulong recordedGeneration) &&
                        recordedGeneration == generation)
                    {
                        dependentCommandBuffers[dependentIndex++] = commandBufferHandle;
                    }
                }

                if (dependentIndex != dependentCommandBuffers.Length)
                    Array.Resize(ref dependentCommandBuffers, dependentIndex);
            }

            resource.State = EVulkanResourceLifetimeState.Destroyed;
            ResourceRuntime.Lifetime.Tracker.ResourceLifetimes.Remove(key);
            ResourceRuntime.Lifetime.Tracker.PublishedResourceGenerations.TryRemove(key, out _);
            ResourceRuntime.Lifetime.Tracker.ResourceCommandBufferDependencies.Remove(key);
        }

        if (dependentCommandBuffers.Length > 0)
        {
            InvalidateCachedCommandBuffersForRetiringResource(
                key,
                generation,
                resourceOwner,
                dependentCommandBuffers);
        }

        Debug.Vulkan(
            "[Vulkan.ResourceLifetime] Detached external resource generation for handle reuse. Resource={0} Generation={1} Owner={2} InvalidatedDescriptors={3}.",
            key,
            generation,
            owner,
            invalidatedDescriptorSetCount);
        return generation;
    }

    private ulong[] DetachSwapchainImageLifetimesForHandleReuse(Image[] images)
    {
        ulong[] generations = new ulong[images.Length];
        for (int i = 0; i < images.Length; i++)
        {
            Image image = images[i];
            generations[i] = DetachExternalVulkanResourceLifetimeForHandleReuse(
                ObjectType.Image,
                image.Handle,
                $"Swapchain.ColorImage[{i}]");
            ClearTrackedImageLayouts(image);
        }

        return generations;
    }

    /// <summary>
    /// Completes physical destruction only when the handle still identifies the detached
    /// generation. A replacement image may already own the same numeric Vulkan handle.
    /// </summary>
    private void CompleteDetachedExternalVulkanResourceDestruction(
        ObjectType type,
        ulong handle,
        ulong expectedGeneration,
        bool forced)
    {
        if (handle == 0 || expectedGeneration == 0)
            return;

        VulkanResourceLifetimeKey key = ResourceKey(type, handle);
        lock (ResourceRuntime.Lifetime.Tracker.SyncRoot)
        {
            if (!ResourceRuntime.Lifetime.Tracker.ResourceLifetimes.TryGetValue(key, out VulkanResourceLifetimeRecord? resource) ||
                resource.Generation != expectedGeneration)
            {
                return;
            }

            if (forced)
                Interlocked.Increment(ref ResourceRuntime.Lifetime.Tracker.ForcedResourceDestructionCount);
            resource.State = EVulkanResourceLifetimeState.Destroyed;
            ResourceRuntime.Lifetime.Tracker.ResourceCommandBufferDependencies.Remove(key);
        }
    }

    private void ReactivateVulkanResourceAfterRetirement(
        ObjectType type,
        ulong handle,
        string owner)
    {
        if (handle == 0)
            return;

        lock (ResourceRuntime.Lifetime.Tracker.SyncRoot)
        {
            VulkanResourceLifetimeRecord resource = GetOrRegisterVulkanResource_NoLock(
                ResourceKey(type, handle),
                owner);
            if (!IsVulkanRetirementReady_NoLock(resource.RetirementTicket))
            {
                throw new InvalidOperationException(
                    $"Cannot recycle {resource.Key} before its retirement completion point is reached.");
            }

            resource.Owner = owner;
            resource.State = EVulkanResourceLifetimeState.CpuOwned;
            resource.Pins.ResetCompletion();
            resource.RetirementSerial = 0;
            resource.RetirementTicket = default;
        }
    }

    internal VulkanResourceLifetimeSnapshot GetVulkanResourceLifetimeSnapshot(
        bool includeExactLiveResourceGenerations = false)
    {
        lock (ResourceRuntime.Lifetime.Tracker.SyncRoot)
        {
            int live = 0;
            int recorded = 0;
            int submitted = 0;
            int completed = 0;
            int external = 0;
            int pending = 0;
            int destroyed = 0;
            long oldestTimestamp = 0;
            ulong oldestRetirementSerial = 0;
            VulkanResourceLifetimeRecord? oldestPendingResource = null;
            List<VulkanPinnedResourceGeneration>? exactLiveResourceGenerations =
                includeExactLiveResourceGenerations ? [] : null;

            foreach (VulkanResourceLifetimeRecord resource in ResourceRuntime.Lifetime.Tracker.ResourceLifetimes.Values)
            {
                UpdateVulkanResourceCompletionState_NoLock(resource);
                EVulkanResourceLifetimeState state = resource.State;
                if ((state & EVulkanResourceLifetimeState.Destroyed) != 0)
                    destroyed++;
                else
                {
                    live++;
                    exactLiveResourceGenerations?.Add(new VulkanPinnedResourceGeneration(
                        resource.Key,
                        resource.Generation));
                }
                if ((state & EVulkanResourceLifetimeState.Recorded) != 0)
                    recorded++;
                if ((state & EVulkanResourceLifetimeState.Submitted) != 0)
                    submitted++;
                if ((state & EVulkanResourceLifetimeState.Completed) != 0)
                    completed++;
                if ((state & EVulkanResourceLifetimeState.External) != 0)
                    external++;
                if ((state & EVulkanResourceLifetimeState.PendingRetirement) != 0)
                {
                    pending++;
                    long timestamp = resource.RetirementTicket.EnqueuedTimestamp;
                    if (timestamp != 0 && (oldestTimestamp == 0 || timestamp < oldestTimestamp))
                    {
                        oldestTimestamp = timestamp;
                        oldestPendingResource = resource;
                    }
                    if (resource.RetirementSerial != 0 &&
                        (oldestRetirementSerial == 0 || resource.RetirementSerial < oldestRetirementSerial))
                    {
                        oldestRetirementSerial = resource.RetirementSerial;
                    }
                }
            }

            long oldestAgeMilliseconds = oldestTimestamp == 0
                ? 0
                : (long)Math.Max(0, Stopwatch.GetElapsedTime(oldestTimestamp).TotalMilliseconds);
            ulong latestRetirementSerial = unchecked((ulong)Math.Max(0, Volatile.Read(ref ResourceRuntime.Lifetime.Tracker.RetirementSerial)));
            ulong oldestGenerationAge = oldestRetirementSerial == 0
                ? 0
                : latestRetirementSerial - oldestRetirementSerial + 1;
            if (oldestAgeMilliseconds >= 5_000 && oldestPendingResource is not null)
            {
                VulkanRetirementTicket ticket = oldestPendingResource.RetirementTicket;
                Debug.VulkanEvery(
                    "Vulkan.ResourceLifetime.OldestPendingRetirement",
                    TimeSpan.FromSeconds(5),
                    "[Vulkan.ResourceLifetime] Oldest pending retirement key={0} owner='{1}' ageMs={2} generation={3} " +
                    "ticketGraphics={4}/{5} ticketTransfer={6}/{7} ticketOther={8}/{9} external={10}.",
                    oldestPendingResource.Key,
                    oldestPendingResource.Owner,
                    oldestAgeMilliseconds,
                    oldestPendingResource.Generation,
                    ticket.GraphicsSequence,
                    ResourceRuntime.Lifetime.Tracker.CompletedGraphicsSequence,
                    ticket.TransferSequence,
                    ResourceRuntime.Lifetime.Tracker.CompletedTransferSequence,
                    ticket.OtherSequence,
                    ResourceRuntime.Lifetime.Tracker.CompletedOtherSequence,
                    ticket.ExternalOwnershipPending);
            }
            int frameDataRecordingLeases = 0;
            int frameDataCachedLeases = 0;
            int frameDataSubmittedLeases = 0;
            int frameDataLeaseRetainedGenerationCount = 0;
            Span<ulong> leaseRetainedGenerations = stackalloc ulong[8];
            VulkanMappedFrameArena? mappedFrameArena = MappedFrameArena;
            ulong activeFrameDataGeneration = mappedFrameArena?.Generation ?? 0UL;
            foreach (VulkanCommandBufferLifetimeRecord commandLifetime in ResourceRuntime.Lifetime.Tracker.CommandBufferLifetimes.Values)
            {
                commandLifetime.FrameDataLease.ObserveQueueCompletion(
                    ResourceRuntime.Lifetime.Tracker.CompletedGraphicsSequence,
                    ResourceRuntime.Lifetime.Tracker.CompletedTransferSequence,
                    ResourceRuntime.Lifetime.Tracker.CompletedOtherSequence);
                if (commandLifetime.FrameDataLease.HasRecordingOwner)
                    frameDataRecordingLeases++;
                if (commandLifetime.FrameDataLease.HasCachedVariantOwner)
                    frameDataCachedLeases++;
                if (commandLifetime.FrameDataLease.HasSubmittedOwner)
                    frameDataSubmittedLeases++;
                ulong leaseGeneration = commandLifetime.FrameDataLease.Generation;
                if (leaseGeneration != 0 && leaseGeneration != activeFrameDataGeneration)
                {
                    bool alreadyCounted = false;
                    for (int index = 0; index < frameDataLeaseRetainedGenerationCount; index++)
                    {
                        if (leaseRetainedGenerations[index] == leaseGeneration)
                        {
                            alreadyCounted = true;
                            break;
                        }
                    }

                    if (!alreadyCounted && frameDataLeaseRetainedGenerationCount < leaseRetainedGenerations.Length)
                        leaseRetainedGenerations[frameDataLeaseRetainedGenerationCount++] = leaseGeneration;
                }
            }
            RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanResourceLifetimeGauges(
                live,
                ResourceRuntime.Lifetime.Tracker.DescriptorSetLifetimes.Count,
                pending,
                oldestAgeMilliseconds);
            RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanMeshFrameDataGauges(
                mappedFrameArena?.FrameSlotCount ?? 0,
                checked((long)((ulong)(mappedFrameArena?.FrameSlotCount ?? 0) * (mappedFrameArena?.Capacity ?? 0UL))),
                checked((long)Math.Min(mappedFrameArena?.ReservedBytes ?? 0UL, (ulong)long.MaxValue)),
                mappedFrameArena?.ReservationCount ?? 0,
                mappedFrameArena?.Generation ?? 0UL,
                frameDataRecordingLeases,
                frameDataCachedLeases,
                frameDataSubmittedLeases,
                activeFrameDataGeneration == 0 ? 0 : 1,
                frameDataLeaseRetainedGenerationCount);
            return new VulkanResourceLifetimeSnapshot(
                live,
                recorded,
                submitted,
                completed,
                external,
                pending,
                destroyed,
                ResourceRuntime.Lifetime.Tracker.CommandBufferLifetimes.Count,
                ResourceRuntime.Lifetime.Tracker.DescriptorSetLifetimes.Count,
                ResourceRuntime.Lifetime.Tracker.LifetimeSubmissions.Count,
                ResourceRuntime.Lifetime.Tracker.LastGraphicsSequence,
                ResourceRuntime.Lifetime.Tracker.CompletedGraphicsSequence,
                ResourceRuntime.Lifetime.Tracker.LastTransferSequence,
                ResourceRuntime.Lifetime.Tracker.CompletedTransferSequence,
                ResourceRuntime.Lifetime.Tracker.LastOtherSequence,
                ResourceRuntime.Lifetime.Tracker.CompletedOtherSequence,
                oldestAgeMilliseconds,
                oldestGenerationAge,
                Volatile.Read(ref ResourceRuntime.Lifetime.Tracker.ForcedResourceDestructionCount),
                ResourceRuntime.Lifetime.Tracker.DeviceLost,
                exactLiveResourceGenerations?.ToArray() ?? []);
        }
    }

    private void LogVulkanResourceLifetimeDiagnostics(string reason)
    {
        if (!VulkanFrameDiagnosticsTraceEnabled)
            return;

        VulkanResourceLifetimeSnapshot snapshot = GetVulkanResourceLifetimeSnapshot();
        if (snapshot.PendingRetirementCount == 0 && snapshot.InFlightSubmissionCount == 0)
            return;

        Debug.VulkanEvery(
            $"Vulkan.ResourceLifetime.{GetHashCode()}",
            TimeSpan.FromSeconds(1),
            "[Vulkan.ResourceLifetime] reason={0} live={1} descriptorSets={2} commandBuffers={3} recorded={4} submitted={5} completed={6} external={7} retirementQueueDepth={8} inFlightSubmissions={9} oldestRetirementMs={10} oldestRetirementGenerationAge={11} graphics={12}/{13} transfer={14}/{15} other={16}/{17} forced={18} deviceLost={19}.",
            reason,
            snapshot.LiveResourceCount,
            snapshot.TrackedDescriptorSetCount,
            snapshot.TrackedCommandBufferCount,
            snapshot.RecordedResourceCount,
            snapshot.SubmittedResourceCount,
            snapshot.CompletedResourceCount,
            snapshot.ExternalResourceCount,
            snapshot.PendingRetirementCount,
            snapshot.InFlightSubmissionCount,
            snapshot.OldestPendingRetirementAgeMilliseconds,
            snapshot.OldestPendingRetirementGenerationAge,
            snapshot.CompletedGraphicsSequence,
            snapshot.LastGraphicsSequence,
            snapshot.CompletedTransferSequence,
            snapshot.LastTransferSequence,
            snapshot.CompletedOtherSequence,
            snapshot.LastOtherSequence,
            snapshot.ForcedDestructionCount,
            snapshot.DeviceLost);
    }
}
