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
