using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

internal sealed partial class VulkanResourceRuntime
{
    private const int MaxResidentTemplateDependencies = 64;

    /// <summary>
    /// Validates and pins the exact native generations used by one resident
    /// template. Validation completes for the entire request before any pin is
    /// incremented, so callers never receive a partially acquired lease.
    /// </summary>
    internal bool TryAcquireResidentTemplateDependencies(
        ReadOnlySpan<VulkanResidentTemplateDependencyRequest> requests,
        out VulkanResidentTemplateDependencyLease? lease,
        out string? reason)
    {
        lease = null;
        reason = null;
        if (requests.IsEmpty || requests.Length > MaxResidentTemplateDependencies)
        {
            reason = $"A resident template must declare between 1 and {MaxResidentTemplateDependencies} dependencies.";
            return false;
        }

        VulkanResourceLifetimeTracker tracker = Lifetime.Tracker;
        lock (tracker.SyncRoot)
        {
            if (tracker.DeviceLost)
            {
                reason = "The Vulkan device is lost; resident-template dependencies cannot be acquired.";
                return false;
            }

            for (int index = 0; index < requests.Length; ++index)
            {
                VulkanResidentTemplateDependencyRequest request = requests[index];
                if (!request.TryGetKey(out VulkanResourceLifetimeKey key) ||
                    key.Type == Silk.NET.Vulkan.ObjectType.Unknown)
                {
                    reason = $"Resident template dependency {index} has an invalid typed native key.";
                    return false;
                }

                for (int priorIndex = 0; priorIndex < index; ++priorIndex)
                {
                    VulkanResidentTemplateDependencyRequest prior = requests[priorIndex];
                    if (!prior.TryGetKey(out VulkanResourceLifetimeKey priorKey))
                        continue;
                    if (priorKey == key)
                    {
                        reason = $"Resident template dependency {index} duplicates {key}.";
                        return false;
                    }

                    if (prior.Handle == request.Handle && prior.Kind != request.Kind)
                    {
                        reason = $"Resident template dependency {index} reuses native handle 0x{request.Handle:X} with mismatched types.";
                        return false;
                    }
                }

                if (!tracker.ResourceLifetimes.TryGetValue(key, out VulkanResourceLifetimeRecord? resource) ||
                    resource.Generation != request.Generation ||
                    resource.PublishedGeneration != request.Generation ||
                    !resource.Slot.IsValid ||
                    (resource.State & (EVulkanResourceLifetimeState.PendingRetirement |
                                       EVulkanResourceLifetimeState.Destroyed)) != 0)
                {
                    reason = $"Resident template dependency {key} generation {request.Generation} is not current and live.";
                    return false;
                }
            }

            VulkanResourceSlotHandle[] dependencies =
                new VulkanResourceSlotHandle[requests.Length];
            for (int index = 0; index < requests.Length; ++index)
            {
                VulkanResidentTemplateDependencyRequest request = requests[index];
                _ = request.TryGetKey(out VulkanResourceLifetimeKey key);
                dependencies[index] = tracker.ResourceLifetimes[key].Slot;
            }

            // Allocate the lease object before mutating pin counts. An
            // allocation failure must leave the transaction entirely
            // uncommitted rather than leaking structural ownership.
            VulkanResidentTemplateDependencyLease acquiredLease =
                new(this, dependencies);

            for (int index = 0; index < dependencies.Length; ++index)
            {
                _ = tracker.TryResolveResourceSlotNoLock(
                    dependencies[index],
                    out VulkanResourceLifetimeRecord resource);
                resource.Pins.AddTemplateReference();
            }

            lease = acquiredLease;
            return true;
        }
    }

    /// <summary>
    /// Returns true only while every dependency generation captured by the
    /// lease is still published, live, and resident-template pinned.
    /// </summary>
    internal bool IsResidentTemplateDependencyLeaseCurrent(
        VulkanResidentTemplateDependencyLease? lease)
    {
        if (lease is null || !lease.IsActive)
            return false;

        VulkanResourceLifetimeTracker tracker = Lifetime.Tracker;
        lock (tracker.SyncRoot)
        {
            if (tracker.DeviceLost)
                return false;

            ReadOnlySpan<VulkanResourceSlotHandle> dependencies = lease.Dependencies;
            for (int index = 0; index < dependencies.Length; ++index)
            {
                VulkanResourceSlotHandle dependency = dependencies[index];
                if (!tracker.TryResolvePublishedResourceSlotNoLock(
                        dependency,
                        out VulkanResourceLifetimeRecord resource) ||
                    resource.Pins.TemplateReferenceCount <= 0 ||
                    (resource.State & (EVulkanResourceLifetimeState.PendingRetirement |
                                       EVulkanResourceLifetimeState.Destroyed)) != 0)
                {
                    return false;
                }
            }

            return true;
        }
    }

    /// <summary>
    /// Validates the exact image, image-view, and sampler generations retained
    /// for a long-lived presentation replay. Unlike ordinary publication
    /// validation, a retained source may no longer be the current publication;
    /// its structural lease keeps the exact native generations alive.
    /// </summary>
    internal bool TryValidateRetainedPresentationSourceForReplay(
        in VulkanPresentationSourceTuple source,
        VulkanResidentTemplateDependencyLease? lease,
        out string failureReason)
    {
        failureReason = string.Empty;
        if (!source.HasLogicalSource || !source.IsComplete || lease is null || !lease.IsActive)
        {
            failureReason = "The retained presentation source or its lifetime lease is incomplete.";
            return false;
        }


        if (TryGetImageAllocationExtent(
                source.Image.Handle,
                out Extent3D allocationExtent) &&
            (source.Width != allocationExtent.Width ||
             source.Height != allocationExtent.Height))
        {
            failureReason =
                $"The retained presentation source extent {source.Width}x{source.Height} " +
                $"does not match native image extent {allocationExtent.Width}x{allocationExtent.Height}.";
            return false;
        }

        ReadOnlySpan<VulkanResourceSlotHandle> dependencies = lease.Dependencies;
        if (dependencies.Length != 3)
        {
            failureReason = "The retained presentation source does not own its exact image, view, and sampler generations.";
            return false;
        }

        VulkanResourceLifetimeTracker tracker = Lifetime.Tracker;
        lock (tracker.SyncRoot)
        {
            return TryValidateRetainedPresentationDependency(
                       tracker,
                       dependencies[0],
                       ObjectType.Image,
                       source.Image.Handle,
                       source.ImageAllocationGeneration,
                       out failureReason) &&
                   TryValidateRetainedPresentationDependency(
                       tracker,
                       dependencies[1],
                       ObjectType.ImageView,
                       source.ImageView.Handle,
                       source.ImageViewGeneration,
                       out failureReason) &&
                   TryValidateRetainedPresentationDependency(
                       tracker,
                       dependencies[2],
                       ObjectType.Sampler,
                       source.Sampler.Handle,
                       source.SamplerGeneration,
                       out failureReason);
        }
    }

    /// <summary>
    /// Transfers an exact retained image generation into a command recording.
    /// The template lease may be released after this independent recorded pin
    /// is published; ordinary retired resources remain inadmissible.
    /// </summary>
    internal bool TryAdoptRetainedPresentationImageForRecording(
        CommandBuffer commandBuffer,
        in VulkanPresentationSourceTuple source,
        VulkanResidentTemplateDependencyLease? lease,
        out string failureReason)
    {
        if (!TryValidateRetainedPresentationSourceForReplay(source, lease, out failureReason))
            return false;

        ulong commandHandle = unchecked((ulong)commandBuffer.Handle);
        VulkanResourceLifetimeTracker tracker = Lifetime.Tracker;
        lock (tracker.SyncRoot)
        {
            if (tracker.DeviceLost ||
                !TryValidateCommandBufferRecordingAdmissionNoLock(commandHandle, out failureReason) ||
                lease is null || !lease.IsActive ||
                !TryValidateRetainedPresentationDependency(
                    tracker,
                    lease.Dependencies[0],
                    ObjectType.Image,
                    source.Image.Handle,
                    source.ImageAllocationGeneration,
                    out failureReason))
                return false;

            if (!tracker.CommandBufferLifetimes.TryGetValue(commandHandle, out VulkanCommandBufferLifetimeRecord? lifetime))
            {
                lifetime = new VulkanCommandBufferLifetimeRecord();
                tracker.CommandBufferLifetimes.Add(commandHandle, lifetime);
            }

            VulkanResourceSlotHandle slot = lease.Dependencies[0];
            VulkanResourceLifetimeKey key = new(ObjectType.Image, source.Image.Handle);
            if (!tracker.ResourceLifetimes.TryGetValue(key, out VulkanResourceLifetimeRecord? keyedImage) ||
                keyedImage.Slot != slot ||
                keyedImage.Generation != source.ImageAllocationGeneration)
            {
                failureReason = "The retained presentation image is no longer the keyed native generation.";
                return false;
            }
            if ((lifetime.RetainedPresentationImageSlot.IsValid &&
                 lifetime.RetainedPresentationImageSlot != slot) ||
                (lifetime.Dependencies.TryGetValue(key, out ulong recordedGeneration) &&
                 recordedGeneration != source.ImageAllocationGeneration))
            {
                failureReason = "The command buffer already records another generation of the presentation image.";
                return false;
            }

            if (!lifetime.Dependencies.ContainsKey(key))
            {
                lifetime.Dependencies.EnsureCapacity(lifetime.Dependencies.Count + 1);
                lifetime.TouchedDependencies.EnsureCapacity(lifetime.Dependencies.Count + 1);
                _ = tracker.TryResolveResourceSlotNoLock(slot, out VulkanResourceLifetimeRecord image);
                image.Pins.AddRecordedReference();
                image.State |= EVulkanResourceLifetimeState.Recorded;
                lifetime.Dependencies.Add(key, image.Generation);
            }
            else
                lifetime.TouchedDependencies.EnsureCapacity(lifetime.Dependencies.Count);

            lifetime.RetainedPresentationImageSlot = slot;
            lifetime.RefreshTouchedDependencies();
            failureReason = string.Empty;
            return true;
        }
    }

    private static bool TryValidateRetainedPresentationDependency(
        VulkanResourceLifetimeTracker tracker,
        VulkanResourceSlotHandle slot,
        ObjectType type,
        ulong handle,
        ulong generation,
        out string failureReason)
    {
        if (!tracker.TryResolveResourceSlotNoLock(slot, out VulkanResourceLifetimeRecord resource) ||
            resource.Key != new VulkanResourceLifetimeKey(type, handle) ||
            resource.Generation != generation ||
            resource.Pins.TemplateReferenceCount <= 0 ||
            (resource.State & EVulkanResourceLifetimeState.Destroyed) != 0)
        {
            failureReason = "The retained presentation source no longer owns its exact native generation.";
            return false;
        }

        failureReason = string.Empty;
        return true;
    }

    /// <summary>
    /// Releases only resources that still match the lease's exact generation.
    /// A stale entry is deliberately left untouched so a recycled handle can
    /// never be unpinned by an old template lease.
    /// </summary>
    internal void ReleaseResidentTemplateDependencies(
        ReadOnlySpan<VulkanResourceSlotHandle> dependencies)
    {
        VulkanResourceLifetimeTracker tracker = Lifetime.Tracker;
        lock (tracker.SyncRoot)
        {
            for (int index = 0; index < dependencies.Length; ++index)
            {
                VulkanResourceSlotHandle dependency = dependencies[index];
                if (!tracker.TryResolveResourceSlotNoLock(
                        dependency,
                        out VulkanResourceLifetimeRecord resource))
                {
                    if (!tracker.DeviceLost)
                    {
                        throw new InvalidOperationException(
                            $"Resident-template dependency {dependency} disappeared before its lease was released.");
                    }
                    continue;
                }

                resource.Pins.ReleaseTemplateReference();
            }
        }
    }
}
