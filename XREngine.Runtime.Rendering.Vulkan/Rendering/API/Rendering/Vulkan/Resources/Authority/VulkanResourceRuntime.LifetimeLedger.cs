using System.Diagnostics;
using Silk.NET.Vulkan;
using XREngine.Rendering.Models;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.Vulkan;

internal sealed partial class VulkanResourceRuntime
{
    internal VulkanRetirementTicket CaptureRetirementWatermark()
        => Lifetime.Tracker.CaptureRetirementWatermark();

    internal bool IsRetirementReady(in VulkanRetirementTicket ticket)
        => Lifetime.Tracker.IsRetirementReady(ticket);

    internal VulkanResourceLifetimeSnapshot CaptureLifetimeSnapshot(
        bool includeExactLiveResourceGenerations)
        => Lifetime.Tracker.CaptureSnapshot(includeExactLiveResourceGenerations);

    internal bool TryGetBufferViewBackingBuffer(
        BufferView bufferView,
        out Silk.NET.Vulkan.Buffer buffer)
    {
        lock (Lifetime.Tracker.SyncRoot)
        {
            if (bufferView.Handle != 0 &&
                Lifetime.Tracker.BufferViewBackingBuffers.TryGetValue(
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

    internal void RegisterResource(
        ObjectType type,
        ulong handle,
        string owner,
        bool externallyOwned = false)
        => Lifetime.Tracker.RegisterResource(
            new VulkanResourceLifetimeKey(type, handle),
            owner,
            externallyOwned);

    internal void RegisterImageViewResource(
        ImageView imageView,
        Image backingImage,
        string owner,
        bool backingImageExternallyOwned)
    {
        RegisterResource(ObjectType.ImageView, imageView.Handle, owner);
        if (backingImage.Handle == 0)
            return;

        RegisterResource(
            ObjectType.Image,
            backingImage.Handle,
            $"{owner}.BackingImage",
            backingImageExternallyOwned);
        lock (Lifetime.Tracker.SyncRoot)
            Lifetime.Tracker.ImageViewBackingImages[imageView.Handle] = backingImage.Handle;
    }

    internal void ReportRetiredResourceBacklog(
        string resourceKind,
        int frameSlot,
        int remaining)
    {
        if (remaining <= 0)
            return;

        Debug.VulkanEvery(
            $"Vulkan.RetiredResourceBacklog.{GetHashCode()}.{resourceKind}.{frameSlot}",
            TimeSpan.FromSeconds(1),
            "[Vulkan] Retired {0} backlog remains for frame slot {1}: {2}",
            resourceKind,
            frameSlot,
            remaining);
    }

    internal void EnsureImageViewAvailableForCommandRecording(
        CommandBuffer commandBuffer,
        ImageView imageView,
        string owner,
        ulong expectedGeneration = 0)
    {
        if (imageView.Handle == 0)
            return;

        VulkanResourceLifetimeKey key = new(ObjectType.ImageView, imageView.Handle);
        lock (Lifetime.Tracker.SyncRoot)
        {
            VulkanResourceLifetimeRecord resource =
                Lifetime.Tracker.GetOrRegisterResourceNoLock(key, owner);
            bool generationChanged = expectedGeneration != 0 &&
                resource.Generation != expectedGeneration;
            bool retired = (resource.State &
                (EVulkanResourceLifetimeState.PendingRetirement |
                 EVulkanResourceLifetimeState.Destroyed)) != 0;
            if (generationChanged || retired)
            {
                throw new InvalidOperationException(
                    $"Command buffer 0x{unchecked((ulong)commandBuffer.Handle):X} attempted to record retired {key} generation {expectedGeneration}; current generation={resource.Generation} state={resource.State} owner={resource.Owner}; requested by {owner}.");
            }

            if (!Lifetime.Tracker.ImageViewBackingImages.TryGetValue(
                    imageView.Handle,
                    out ulong backingImageHandle) ||
                backingImageHandle == 0)
                return;

            VulkanResourceLifetimeKey imageKey = new(ObjectType.Image, backingImageHandle);
            VulkanResourceLifetimeRecord image =
                Lifetime.Tracker.GetOrRegisterResourceNoLock(imageKey, owner);
            if ((image.State &
                 (EVulkanResourceLifetimeState.PendingRetirement |
                  EVulkanResourceLifetimeState.Destroyed)) != 0)
            {
                throw new InvalidOperationException(
                    $"Command buffer 0x{unchecked((ulong)commandBuffer.Handle):X} attempted to record {key} backed by retired {imageKey} generation {image.Generation} state={image.State}; requested by {owner}.");
            }
        }
    }

    internal void RegisterAllocatedCommandBuffer(
        CommandBuffer commandBuffer,
        CommandPool commandPool,
        CommandBufferLevel level,
        string owner)
    {
        ulong handle = unchecked((ulong)commandBuffer.Handle);
        if (handle == 0)
            return;

        VulkanResourceLifetimeKey commandKey = new(ObjectType.CommandBuffer, handle);
        VulkanResourceLifetimeKey poolKey = new(ObjectType.CommandPool, commandPool.Handle);
        Lifetime.Tracker.RegisterResource(commandKey, owner, externallyOwned: false);
        lock (Lifetime.Tracker.SyncRoot)
        {
            VulkanResourceLifetimeRecord pool =
                Lifetime.Tracker.GetOrRegisterResourceNoLock(poolKey, $"{owner}.Pool");
            if ((pool.State &
                 (EVulkanResourceLifetimeState.PendingRetirement |
                  EVulkanResourceLifetimeState.Destroyed)) != 0)
                throw new InvalidOperationException($"Cannot register {commandKey} against retiring {poolKey}.");

            if (!Lifetime.Tracker.CommandBufferLifetimes.TryGetValue(
                    handle,
                    out VulkanCommandBufferLifetimeRecord? lifetime))
            {
                lifetime = new VulkanCommandBufferLifetimeRecord();
                Lifetime.Tracker.CommandBufferLifetimes[handle] = lifetime;
            }
            lifetime.Level = level;
            lifetime.AllocatingCommandPool = poolKey;
            lifetime.AllocatingCommandPoolGeneration = pool.Generation;
            if (!Lifetime.Tracker.CommandBuffersByPool.TryGetValue(
                    poolKey,
                    out HashSet<ulong>? children))
            {
                children = [];
                Lifetime.Tracker.CommandBuffersByPool[poolKey] = children;
            }
            children.Add(handle);
        }
    }

    internal unsafe Result CreateImageTracked(
        Vk api,
        Device device,
        ref ImageCreateInfo createInfo,
        Image* image,
        string owner)
    {
        Result result = api.CreateImage(device, ref createInfo, null, image);
        if (result == Result.Success && image is not null)
            RegisterResource(ObjectType.Image, image->Handle, owner);
        return result;
    }

    internal unsafe void DestroyImageImmediateTracked(
        Vk api,
        Device device,
        Image image,
        string owner)
    {
        if (image.Handle == 0)
            return;

        VulkanResourceLifetimeKey key = new(ObjectType.Image, image.Handle);
        VulkanRetirementTicket ticket = CaptureRetirementTicket(
            key,
            owner);
        if (!Lifetime.Tracker.IsRetirementReady(ticket))
        {
            throw new InvalidOperationException(
                $"Cannot immediately destroy image 0x{image.Handle:X} in {owner} before its GPU completion point.");
        }

        api.DestroyImage(device, image, null);
        CompleteResourceDestruction(ObjectType.Image, image.Handle);
    }

    internal VulkanRetirementTicket CaptureRetirementTicket(
        VulkanResourceLifetimeKey key,
        string owner)
    {
        if (!key.IsValid)
            return VulkanRetirementTicket.None;

        Lifetime.Tracker.FenceResourceRecordingAdmission(key, owner);
        Lifetime.PublishTrackingDependenciesBeforeRetirement(key);
        if (key.Type == ObjectType.Image)
            Images.RetireViewsForBackingImage(key.Handle, owner);

        VulkanRetirementTicket ticket = CaptureRetirementTicketCore(
            key,
            owner,
            out ulong generation,
            out _,
            out int invalidatedDescriptorSetCount);
        if (invalidatedDescriptorSetCount != 0)
            Debug.VulkanEvery(
                $"Vulkan.ResourceLifetime.TargetedDescriptorInvalidation.{key.Type}",
                TimeSpan.FromSeconds(1),
                "[Vulkan.ResourceLifetime] Targeted descriptor invalidation resource={0} generation={1} descriptorSets={2}.",
                key,
                generation,
                invalidatedDescriptorSetCount);
        return ticket;
    }

    private VulkanRetirementTicket CaptureRetirementTicketCore(
        VulkanResourceLifetimeKey key,
        string owner,
        out ulong generation,
        out ulong[] dependentCommandBuffers,
        out int invalidatedDescriptorSetCount)
    {
        dependentCommandBuffers = [];
        invalidatedDescriptorSetCount = 0;
        lock (Lifetime.Tracker.SyncRoot)
        {
            VulkanResourceLifetimeRecord resource =
                Lifetime.Tracker.GetOrRegisterResourceNoLock(key, owner);
            generation = resource.Generation;
            if ((resource.State &
                 (EVulkanResourceLifetimeState.Destroyed |
                  EVulkanResourceLifetimeState.PendingRetirement)) != 0)
            {
                return resource.RetirementTicket;
            }

            UpdateResourceCompletionStateNoLock(resource);
            VulkanRetirementTicket ticket = new(
                resource.Pins.LastGraphicsSequence,
                resource.Pins.LastTransferSequence,
                resource.Pins.LastOtherSequence,
                Stopwatch.GetTimestamp(),
                generation,
                (resource.State & EVulkanResourceLifetimeState.External) != 0,
                VulkanRetirementPinSet.Single(key, generation));
            resource.RetirementSerial = unchecked(
                (ulong)Interlocked.Increment(ref Lifetime.Tracker.RetirementSerial));
            resource.State |= EVulkanResourceLifetimeState.PendingRetirement;
            resource.RetirementTicket = ticket;
            Lifetime.Tracker.SetPublishedGenerationNoLock(key, 0UL);

            invalidatedDescriptorSetCount =
                Descriptors.InvalidateResourceReferencesNoLock(Lifetime, key);
            if (Lifetime.Tracker.ResourceCommandBufferDependencies.TryGetValue(
                    key,
                    out HashSet<ulong>? dependents) &&
                dependents.Count != 0)
            {
                dependentCommandBuffers = new ulong[dependents.Count];
                int count = 0;
                foreach (ulong commandBufferHandle in dependents)
                {
                    if (Lifetime.Tracker.CommandBufferLifetimes.TryGetValue(
                            commandBufferHandle,
                            out VulkanCommandBufferLifetimeRecord? lifetime) &&
                        lifetime.Dependencies.TryGetValue(key, out ulong recordedGeneration) &&
                        recordedGeneration == generation)
                    {
                        dependentCommandBuffers[count++] = commandBufferHandle;
                    }
                }

                if (count != dependentCommandBuffers.Length)
                    Array.Resize(ref dependentCommandBuffers, count);
            }

            return ticket;
        }
    }

    internal bool TryBeginDestroyResourceGeneration(
        ObjectType type,
        ulong handle,
        ulong expectedGeneration,
        string owner)
    {
        if (handle == 0 || expectedGeneration == 0)
            return false;

        VulkanResourceLifetimeKey key = new(type, handle);
        lock (Lifetime.Tracker.SyncRoot)
        {
            bool forced = Lifetime.Tracker.ForcedRetirementDrainDepth > 0;
            if (!Lifetime.Tracker.ResourceLifetimes.TryGetValue(
                    key,
                    out VulkanResourceLifetimeRecord? resource) ||
                resource.Generation != expectedGeneration ||
                (resource.State & EVulkanResourceLifetimeState.Destroyed) != 0 ||
                (!forced &&
                 (!Lifetime.Tracker.IsRetirementReadyNoLock(resource.RetirementTicket) ||
                  !resource.Pins.IsRetirementReady(
                      Lifetime.Tracker.CompletedGraphicsSequence,
                      Lifetime.Tracker.CompletedTransferSequence,
                      Lifetime.Tracker.CompletedOtherSequence))))
            {
                return false;
            }

            return true;
        }
    }

    internal void CompleteResourceDestruction(
        ObjectType type,
        ulong handle,
        bool forced = false)
    {
        if (handle == 0)
            return;

        VulkanResourceLifetimeKey key = new(type, handle);
        lock (Lifetime.Tracker.SyncRoot)
        {
            forced |= Lifetime.Tracker.ForcedRetirementDrainDepth > 0;
            if (!Lifetime.Tracker.ResourceLifetimes.TryGetValue(
                    key,
                    out VulkanResourceLifetimeRecord? resource))
                return;
            if (!forced &&
                (!Lifetime.Tracker.IsRetirementReadyNoLock(resource.RetirementTicket) ||
                 !resource.Pins.IsRetirementReady(
                     Lifetime.Tracker.CompletedGraphicsSequence,
                     Lifetime.Tracker.CompletedTransferSequence,
                     Lifetime.Tracker.CompletedOtherSequence)))
            {
                throw new InvalidOperationException(
                    $"Attempted to destroy {key} generation {resource.Generation} before GPU completion.");
            }

            if (forced)
                Interlocked.Increment(ref Lifetime.Tracker.ForcedResourceDestructionCount);
            resource.State = EVulkanResourceLifetimeState.Destroyed;
            Lifetime.Tracker.ResourceCommandBufferDependencies.Remove(key);
            if (type == ObjectType.ImageView)
                Lifetime.Tracker.ImageViewBackingImages.Remove(handle);
            if (type == ObjectType.BufferView)
                Lifetime.Tracker.BufferViewBackingBuffers.Remove(handle);
            if (type == ObjectType.DescriptorSet)
            {
                VulkanDescriptorManager.RemoveDescriptorSetLifetimeNoLock(
                    Lifetime,
                    handle,
                    forced);
            }
            if (type == ObjectType.CommandBuffer &&
                Lifetime.Tracker.CommandBufferLifetimes.Remove(
                    handle,
                    out VulkanCommandBufferLifetimeRecord? lifetime))
            {
                ReleaseCommandBufferDependenciesNoLock(handle, lifetime);
                if (lifetime.AllocatingCommandPool.IsValid &&
                    Lifetime.Tracker.CommandBuffersByPool.TryGetValue(
                        lifetime.AllocatingCommandPool,
                        out HashSet<ulong>? children))
                    children.Remove(handle);
            }
            if (type == ObjectType.Framebuffer)
                Lifetime.Tracker.FramebufferAttachments.Remove(handle);
            if (type == ObjectType.DescriptorPool)
                VulkanDescriptorManager.RemoveDescriptorSetsOwnedByPoolNoLock(
                    Lifetime,
                    handle,
                    forced);
            if (type == ObjectType.CommandPool)
                Lifetime.Tracker.CommandBuffersByPool.Remove(key);
        }
    }

    internal void BeginForcedRetirementDrain()
    {
        lock (Lifetime.Tracker.SyncRoot)
            Lifetime.Tracker.ForcedRetirementDrainDepth++;
    }

    internal void EndForcedRetirementDrain()
    {
        lock (Lifetime.Tracker.SyncRoot)
            Lifetime.Tracker.ForcedRetirementDrainDepth =
                Math.Max(0, Lifetime.Tracker.ForcedRetirementDrainDepth - 1);
    }

    internal void CompleteTimeline(Silk.NET.Vulkan.Semaphore semaphore, ulong value)
    {
        if (semaphore.Handle == 0 || value == 0)
            return;
        lock (Lifetime.Tracker.SyncRoot)
        {
            for (int index = Lifetime.Tracker.LifetimeSubmissions.Count - 1;
                 index >= 0;
                 index--)
            {
                VulkanLifetimeSubmission submission =
                    Lifetime.Tracker.LifetimeSubmissions[index];
                if (submission.TimelineSemaphoreHandle != semaphore.Handle ||
                    submission.TimelineValue == 0 ||
                    submission.TimelineValue > value)
                    continue;

                Lifetime.Tracker.MarkQueueSequenceCompletedNoLock(
                    submission.QueueDomain,
                    submission.QueueSequence);
                Lifetime.Tracker.LifetimeSubmissions.RemoveAt(index);
            }
        }
    }

    internal void CompleteQueue(Queue queue)
    {
        ulong queueHandle = unchecked((ulong)queue.Handle);
        lock (Lifetime.Tracker.SyncRoot)
        {
            for (int index = Lifetime.Tracker.LifetimeSubmissions.Count - 1;
                 index >= 0;
                 index--)
            {
                VulkanLifetimeSubmission submission =
                    Lifetime.Tracker.LifetimeSubmissions[index];
                if (submission.QueueHandle != queueHandle)
                    continue;
                Lifetime.Tracker.MarkQueueSequenceCompletedNoLock(
                    submission.QueueDomain,
                    submission.QueueSequence);
                Lifetime.Tracker.LifetimeSubmissions.RemoveAt(index);
            }
        }
    }

    internal void CompleteDevice()
    {
        lock (Lifetime.Tracker.SyncRoot)
        {
            Lifetime.Tracker.CompletedGraphicsSequence =
                Lifetime.Tracker.LastGraphicsSequence;
            Lifetime.Tracker.CompletedTransferSequence =
                Lifetime.Tracker.LastTransferSequence;
            Lifetime.Tracker.CompletedOtherSequence =
                Lifetime.Tracker.LastOtherSequence;
            Lifetime.Tracker.LifetimeSubmissions.Clear();
        }
    }

    internal void MarkDeviceLost()
    {
        lock (Lifetime.Tracker.SyncRoot)
            Lifetime.Tracker.DeviceLost = true;
    }

    internal void LogLifetimeDiagnostics(string reason)
    {
        VulkanResourceLifetimeSnapshot snapshot = Lifetime.Tracker.CaptureSnapshot(
            includeExactLiveResourceGenerations: false);
        if (snapshot.PendingRetirementCount == 0 && snapshot.InFlightSubmissionCount == 0)
            return;

        Debug.VulkanEvery(
            $"Vulkan.ResourceLifetime.{GetHashCode()}",
            TimeSpan.FromSeconds(1),
            "[Vulkan.ResourceLifetime] reason={0} live={1} descriptorSets={2} commandBuffers={3} pending={4} inFlight={5} forced={6} deviceLost={7}.",
            reason,
            snapshot.LiveResourceCount,
            snapshot.TrackedDescriptorSetCount,
            snapshot.TrackedCommandBufferCount,
            snapshot.PendingRetirementCount,
            snapshot.InFlightSubmissionCount,
            snapshot.ForcedDestructionCount,
            snapshot.DeviceLost);
    }

    internal void ReleaseDescriptorReferencesForPhysicalResourceDestruction(string reason)
    {
        int cachedPoolCount = ReleaseComputeDescriptorCaches();
        int commandReferenceCount = 0;
        int meshRendererCount = 0;
        int materialCount = 0;
        int frameBufferCount = 0;

        VkObject<XRMeshRenderer.BaseVersion>[] meshes =
            BackendObjects.Snapshot<XRMeshRenderer.BaseVersion>();
        for (int index = 0; index < meshes.Length; index++)
        {
            if (meshes[index] is not VkMeshRenderer mesh)
                continue;
            mesh.ReleaseDescriptorReferencesForPhysicalResourceDestruction();
            meshRendererCount++;
        }

        VkObject<XRMaterial>[] materials = BackendObjects.Snapshot<XRMaterial>();
        for (int index = 0; index < materials.Length; index++)
        {
            if (materials[index] is not VkMaterial material)
                continue;
            material.ReleaseDescriptorReferencesForPhysicalResourceDestruction();
            materialCount++;
        }

        VkObject<XRFrameBuffer>[] frameBuffers = BackendObjects.Snapshot<XRFrameBuffer>();
        for (int index = 0; index < frameBuffers.Length; index++)
        {
            if (frameBuffers[index] is VkFrameBuffer frameBuffer &&
                frameBuffer.InvalidateCachedAttachmentState())
                frameBufferCount++;
        }

        Debug.VulkanEvery(
            $"Vulkan.ResourceDestroy.ReleaseDescriptorReferences.{reason}",
            TimeSpan.FromSeconds(1),
            "[Vulkan] Released physical-resource descriptor references: reason={0} meshes={1} materials={2} framebuffers={3} cachedPools={4} commandReferences={5}.",
            reason,
            meshRendererCount,
            materialCount,
            frameBufferCount,
            cachedPoolCount,
            commandReferenceCount);
    }

    private int ReleaseComputeDescriptorCaches()
        => RetireComputeDescriptorCaches(preserveCapacity: true);

    /// <summary>
    /// Retires the generation-owned compute descriptor pools during final
    /// resource-runtime shutdown without recreating empty cache slots.
    /// </summary>
    internal int RetireComputeDescriptorCachesForShutdown()
        => RetireComputeDescriptorCaches(preserveCapacity: false);

    private int RetireComputeDescriptorCaches(bool preserveCapacity)
    {
        lock (Descriptors.Compute.Gate)
        {
            ComputeDescriptorImageCache[]? caches = Descriptors.Compute.Caches;
            if (caches is null)
                return 0;

            HashSet<ulong> retiredPools = [];
            for (int cacheIndex = 0; cacheIndex < caches.Length; cacheIndex++)
            {
                foreach (List<ComputeDescriptorPoolBlock> blocks in
                         caches[cacheIndex].PoolsBySchema.Values)
                for (int blockIndex = 0; blockIndex < blocks.Count; blockIndex++)
                {
                    DescriptorPool pool = blocks[blockIndex].Pool;
                    if (pool.Handle != 0 && retiredPools.Add(pool.Handle))
                        DescriptorLifetime.RetireDescriptorPool(pool);
                }
            }

            if (preserveCapacity)
            {
                ComputeDescriptorImageCache[] replacement =
                    new ComputeDescriptorImageCache[caches.Length];
                for (int index = 0; index < replacement.Length; index++)
                    replacement[index] = new ComputeDescriptorImageCache();
                Descriptors.Compute.Caches = replacement;
            }
            else
            {
                Descriptors.Compute.Caches = null;
            }

            return retiredPools.Count;
        }
    }
}
