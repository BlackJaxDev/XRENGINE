using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Owns the mutable registries, scratch storage, and queue-completion watermarks
/// used by Vulkan resource lifetime tracking.
/// </summary>
/// <remarks>
/// The renderer currently retains the lifetime algorithms as a migration
/// facade. Centralizing their state here prevents unrelated renderer partials
/// from adding parallel registries and gives later extraction a single owner.
/// All scratch collections are retained for the tracker lifetime so steady-state
/// recording and submission do not allocate.
/// </remarks>
internal sealed class VulkanResourceLifetimeTracker
{
    private const int InitialResourceSlotCapacity = 4096;

    private VulkanResourceLifetimeRecord?[] _resourceSlots =
        new VulkanResourceLifetimeRecord?[InitialResourceSlotCapacity];
    private uint[] _resourceSlotFreeLinks = new uint[InitialResourceSlotCapacity];
    // Submission validation is serialized by SyncRoot. Keep its generation
    // membership scratch parallel to the resource directory so the hot submit
    // path never rebuilds a keyed hash table just to detect duplicates.
    private ulong[] _submissionDependencyGenerationScratch =
        new ulong[InitialResourceSlotCapacity];
    private ulong[] _submissionDependencyEpochScratch =
        new ulong[InitialResourceSlotCapacity];
    private ulong _submissionDependencyScratchEpoch;
    private uint _resourceSlotCount = 1u;
    private uint _freeResourceSlotHead;

    internal object SyncRoot { get; } = new();

    internal ConcurrentDictionary<VulkanResourceLifetimeKey, ulong> PublishedResourceGenerations { get; } = new();
    internal Dictionary<VulkanResourceLifetimeKey, VulkanResourceLifetimeRecord> ResourceLifetimes { get; } = new();
    /// <summary>
    /// Cold-path compatibility index for externally owned native identities that
    /// were detached so Vulkan may reuse their handles while the old generation
    /// finishes retiring. The flat slot remains the submission-time authority.
    /// </summary>
    internal Dictionary<VulkanPinnedResourceGeneration, VulkanResourceSlotHandle> DetachedResourceSlots { get; } = new();
    internal Dictionary<ulong, VulkanCommandBufferLifetimeRecord> CommandBufferLifetimes { get; } = new();
    /// <summary>
    /// Persistent allocation ownership, separate from a command buffer's transient
    /// recording dependencies. Destroying a command pool implicitly frees every
    /// child, so the pool must retain this relation until that native destruction
    /// has settled every child generation.
    /// </summary>
    internal Dictionary<VulkanResourceLifetimeKey, HashSet<ulong>> CommandBuffersByPool { get; } = new();
    internal Dictionary<VulkanResourceLifetimeKey, HashSet<ulong>> ResourceCommandBufferDependencies { get; } = new();
    // Accessed only while SyncRoot is held. Empty reverse-index sets are
    // scratch, never visible through the live map, and retain their buckets
    // across completed command-buffer resets.
    internal Stack<HashSet<ulong>> ReusableResourceCommandBufferDependencySets { get; } = new();
    internal Dictionary<ulong, VulkanDescriptorSetLifetimeRecord> DescriptorSetLifetimes { get; } = new();
    internal Dictionary<ulong, List<VkRenderQuery>> RenderQueriesByPool { get; } = new();
    internal Dictionary<ulong, HashSet<ulong>> DescriptorSetsByPool { get; } = new();
    internal Dictionary<VulkanResourceLifetimeKey, HashSet<ulong>> DescriptorSetsByReferencedResource { get; } = new();
    internal ConcurrentDictionary<ulong, VulkanPublishedDescriptorSetSnapshot> PublishedDescriptorSets { get; } = new();
    internal Dictionary<ulong, ulong> ImageViewBackingImages { get; } = new();
    internal Dictionary<ulong, ulong> BufferViewBackingBuffers { get; } = new();


    internal VulkanResourceLifetimeSnapshot CaptureSnapshot(
        bool includeExactLiveResourceGenerations)
    {
        using (VulkanFrameLockScope.Enter(
                   SyncRoot,
                   EVulkanFrameWaitReason.ResourceLifetimeLock))
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
            List<VulkanPinnedResourceGeneration>? exactLiveResourceGenerations =
                includeExactLiveResourceGenerations ? [] : null;

            foreach (VulkanResourceLifetimeRecord resource in ResourceLifetimes.Values)
                AccumulateSnapshotResource(
                    resource,
                    exactLiveResourceGenerations,
                    ref live,
                    ref recorded,
                    ref submitted,
                    ref completed,
                    ref external,
                    ref pending,
                    ref destroyed,
                    ref oldestTimestamp,
                    ref oldestRetirementSerial);

            foreach (VulkanResourceSlotHandle slot in DetachedResourceSlots.Values)
            {
                if (!TryResolveResourceSlotNoLock(
                        slot,
                        out VulkanResourceLifetimeRecord resource))
                    continue;

                AccumulateSnapshotResource(
                    resource,
                    exactLiveResourceGenerations,
                    ref live,
                    ref recorded,
                    ref submitted,
                    ref completed,
                    ref external,
                    ref pending,
                    ref destroyed,
                    ref oldestTimestamp,
                    ref oldestRetirementSerial);
            }

            long oldestAgeMilliseconds = oldestTimestamp == 0
                ? 0
                : (long)Math.Max(
                    0,
                    Stopwatch.GetElapsedTime(oldestTimestamp).TotalMilliseconds);
            ulong latestRetirementSerial = unchecked(
                (ulong)Math.Max(0, Volatile.Read(ref RetirementSerial)));
            ulong oldestGenerationAge = oldestRetirementSerial == 0
                ? 0
                : latestRetirementSerial - oldestRetirementSerial + 1;

            return new VulkanResourceLifetimeSnapshot(
                live,
                recorded,
                submitted,
                completed,
                external,
                pending,
                destroyed,
                CommandBufferLifetimes.Count,
                DescriptorSetLifetimes.Count,
                LifetimeSubmissions.Count,
                LastGraphicsSequence,
                CompletedGraphicsSequence,
                LastTransferSequence,
                CompletedTransferSequence,
                LastOtherSequence,
                CompletedOtherSequence,
                oldestAgeMilliseconds,
                oldestGenerationAge,
                Volatile.Read(ref ForcedResourceDestructionCount),
                DeviceLost,
                exactLiveResourceGenerations is null
                    ? []
                    : [.. exactLiveResourceGenerations]);
        }
    }

    private static void AccumulateSnapshotResource(
        VulkanResourceLifetimeRecord resource,
        List<VulkanPinnedResourceGeneration>? exactLiveResourceGenerations,
        ref int live,
        ref int recorded,
        ref int submitted,
        ref int completed,
        ref int external,
        ref int pending,
        ref int destroyed,
        ref long oldestTimestamp,
        ref ulong oldestRetirementSerial)
    {
        EVulkanResourceLifetimeState state = resource.State;
        if ((state & EVulkanResourceLifetimeState.Destroyed) != 0)
            destroyed++;
        else
        {
            live++;
            exactLiveResourceGenerations?.Add(
                new VulkanPinnedResourceGeneration(
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
        if ((state & EVulkanResourceLifetimeState.PendingRetirement) == 0)
            return;

        pending++;
        long timestamp = resource.RetirementTicket.EnqueuedTimestamp;
        if (timestamp != 0 &&
            (oldestTimestamp == 0 || timestamp < oldestTimestamp))
        {
            oldestTimestamp = timestamp;
        }
        if (resource.RetirementSerial != 0 &&
            (oldestRetirementSerial == 0 ||
             resource.RetirementSerial < oldestRetirementSerial))
        {
            oldestRetirementSerial = resource.RetirementSerial;
        }
    }

    internal Dictionary<ulong, VulkanResourceLifetimeKey[]> FramebufferAttachments { get; } = new();

    internal List<VulkanLifetimeSubmission> LifetimeSubmissions { get; } = new(16);
    internal ThreadLocal<HashSet<ulong>> ChangedDescriptorSetsScratch { get; } = new(static () => []);
    internal ThreadLocal<HashSet<ulong>> DescriptorClosureChangedSetsScratch { get; } = new(static () => []);
    internal ThreadLocal<HashSet<VulkanResourceLifetimeKey>> DescriptorReferencesScratch { get; } = new(static () => []);
    internal ThreadLocal<HashSet<VulkanResourceLifetimeKey>> DescriptorPinnedReferencesScratch { get; } = new(static () => []);

    internal long ResourceGeneration;
    // Native buffer handles can be replaced while the logical planner revision is
    // unchanged. This separate epoch invalidates only frozen buffer bindings.
    internal long NativeBufferBindingRevision;

    internal ulong PublishedNativeBufferBindingRevision
        => unchecked((ulong)Volatile.Read(ref NativeBufferBindingRevision));
    internal long RetirementSerial;
    internal ulong LastGraphicsSequence;
    internal ulong CompletedGraphicsSequence;
    internal ulong ObservedGraphicsSequence;
    internal ulong LastTransferSequence;
    internal ulong CompletedTransferSequence;
    internal ulong ObservedTransferSequence;
    internal ulong LastOtherSequence;
    internal ulong CompletedOtherSequence;
    internal ulong ObservedOtherSequence;
    internal long ForcedResourceDestructionCount;
    internal bool DeviceLost;
    internal int ForcedRetirementDrainDepth;

    internal ulong GetPublishedGeneration(VulkanResourceLifetimeKey key)
        => key.IsValid && PublishedResourceGenerations.TryGetValue(key, out ulong generation)
            ? generation
            : 0;

    /// <summary>
    /// Resolves a typed native identity to its flat slot during cold-path
    /// construction. Stable consumers retain the returned slot and never repeat
    /// this dictionary lookup.
    /// </summary>
    internal bool TryGetResourceSlotNoLock(
        VulkanResourceLifetimeKey key,
        out VulkanResourceSlotHandle slot)
    {
        if (ResourceLifetimes.TryGetValue(
                key,
                out VulkanResourceLifetimeRecord? resource))
        {
            slot = resource.Slot;
            return slot.IsValid;
        }

        slot = VulkanResourceSlotHandle.Invalid;
        return false;
    }

    /// <summary>
    /// Performs an allocation-free ABA-safe lookup in the flat lifetime
    /// directory. Callers serialize access with <see cref="SyncRoot"/>.
    /// </summary>
    internal bool TryResolveResourceSlotNoLock(
        VulkanResourceSlotHandle slot,
        out VulkanResourceLifetimeRecord resource)
    {
        if (!slot.IsValid || slot.Index >= _resourceSlotCount)
        {
            resource = null!;
            return false;
        }

        VulkanResourceLifetimeRecord? candidate =
            _resourceSlots[checked((int)slot.Index)];
        if (candidate is null ||
            candidate.Slot != slot ||
            candidate.Generation != slot.Generation)
        {
            resource = null!;
            return false;
        }

        resource = candidate;
        return true;
    }

    /// <summary>
    /// Starts an allocation-free generation-membership pass for one serialized
    /// submission. The epoch distinguishes entries retained from earlier
    /// submissions while the stored generation preserves ABA validation.
    /// </summary>
    internal ulong BeginSubmissionDependencyGenerationScratchNoLock()
    {
        ulong epoch = unchecked(++_submissionDependencyScratchEpoch);
        if (epoch != 0UL)
            return epoch;

        Array.Clear(_submissionDependencyEpochScratch);
        _submissionDependencyScratchEpoch = 1UL;
        return 1UL;
    }

    internal void RecordSubmissionDependencyGenerationNoLock(
        VulkanResourceSlotHandle slot,
        ulong generation,
        ulong epoch)
    {
        if (!slot.IsValid ||
            generation == 0UL ||
            epoch == 0UL ||
            slot.Index >= (uint)_submissionDependencyGenerationScratch.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(slot));
        }

        int index = checked((int)slot.Index);
        _submissionDependencyGenerationScratch[index] = generation;
        _submissionDependencyEpochScratch[index] = epoch;
    }

    internal bool TryGetSubmissionDependencyGenerationNoLock(
        VulkanResourceSlotHandle slot,
        ulong epoch,
        out ulong generation)
    {
        if (slot.IsValid &&
            epoch != 0UL &&
            slot.Index < (uint)_submissionDependencyGenerationScratch.Length)
        {
            int index = checked((int)slot.Index);
            if (_submissionDependencyEpochScratch[index] == epoch)
            {
                generation = _submissionDependencyGenerationScratch[index];
                return true;
            }
        }

        generation = 0UL;
        return false;
    }

    /// <summary>
    /// Resolves either the current keyed generation or an exact detached
    /// generation. This is a cold-path compatibility bridge for retirement code
    /// that still owns a typed native key plus generation instead of a slot.
    /// </summary>
    internal bool TryResolveResourceGenerationNoLock(
        VulkanResourceLifetimeKey key,
        ulong generation,
        out VulkanResourceLifetimeRecord resource)
    {
        if (generation != 0UL &&
            ResourceLifetimes.TryGetValue(
                key,
                out VulkanResourceLifetimeRecord? current) &&
            current.Generation == generation)
        {
            resource = current;
            return true;
        }

        VulkanPinnedResourceGeneration pinned = new(key, generation);
        if (generation != 0UL &&
            DetachedResourceSlots.TryGetValue(
                pinned,
                out VulkanResourceSlotHandle slot) &&
            TryResolveResourceSlotNoLock(slot, out resource) &&
            resource.Key == key &&
            resource.Generation == generation)
        {
            return true;
        }

        resource = null!;
        return false;
    }

    /// <summary>
    /// Returns whether a detached generation can be destroyed without violating
    /// any recorded, descriptor, template, queue, or completion-sequence pin.
    /// An unresolved slot is already gone and is therefore idempotently ready.
    /// </summary>
    internal bool IsDetachedResourceSlotRetirementReadyNoLock(
        VulkanResourceSlotHandle slot)
    {
        if (!slot.IsValid ||
            !TryResolveResourceSlotNoLock(
                slot,
                out VulkanResourceLifetimeRecord resource))
        {
            return true;
        }

        VulkanPinnedResourceGeneration pinned = new(
            resource.Key,
            resource.Generation);
        if (!DetachedResourceSlots.TryGetValue(
                pinned,
                out VulkanResourceSlotHandle indexedSlot) ||
            indexedSlot != slot)
        {
            return false;
        }

        return resource.Pins.IsRetirementReady(
            CompletedGraphicsSequence,
            CompletedTransferSequence,
            CompletedOtherSequence);
    }

    internal bool TryResolvePublishedResourceSlotNoLock(
        VulkanResourceSlotHandle slot,
        out VulkanResourceLifetimeRecord resource)
    {
        if (!TryResolveResourceSlotNoLock(slot, out resource) ||
            resource.PublishedGeneration != slot.Generation ||
            (resource.State &
             (EVulkanResourceLifetimeState.PendingRetirement |
              EVulkanResourceLifetimeState.Destroyed)) != 0)
        {
            resource = null!;
            return false;
        }

        return true;
    }

    internal bool TryGetPublishedDescriptorSnapshotNoLock(
        VulkanResourceSlotHandle descriptorSetSlot,
        out VulkanPublishedDescriptorSetSnapshot snapshot)
    {
        if (TryResolvePublishedResourceSlotNoLock(
                descriptorSetSlot,
                out VulkanResourceLifetimeRecord resource) &&
            resource.Key.Type == ObjectType.DescriptorSet &&
            resource.PublishedDescriptorSnapshot is { } published &&
            published.DescriptorSetSlot == descriptorSetSlot)
        {
            snapshot = published;
            return true;
        }

        snapshot = null!;
        return false;
    }

    internal void PublishDescriptorSnapshotNoLock(
        ulong descriptorSetHandle,
        VulkanPublishedDescriptorSetSnapshot snapshot)
    {
        VulkanResourceLifetimeKey key = new(
            ObjectType.DescriptorSet,
            descriptorSetHandle);
        if (!ResourceLifetimes.TryGetValue(
                key,
                out VulkanResourceLifetimeRecord? resource) ||
            resource.Slot != snapshot.DescriptorSetSlot)
        {
            throw new InvalidOperationException(
                $"Descriptor set {key} has no matching resource slot for publication.");
        }

        resource.PublishedDescriptorSnapshot = snapshot;
        PublishedDescriptorSets[descriptorSetHandle] = snapshot;
    }

    internal void RemovePublishedDescriptorSnapshotNoLock(
        ulong descriptorSetHandle)
    {
        VulkanResourceLifetimeKey key = new(
            ObjectType.DescriptorSet,
            descriptorSetHandle);
        if (ResourceLifetimes.TryGetValue(
                key,
                out VulkanResourceLifetimeRecord? resource))
        {
            resource.PublishedDescriptorSnapshot = null;
        }

        PublishedDescriptorSets.TryRemove(descriptorSetHandle, out _);
    }

    internal void SetPublishedGenerationNoLock(
        VulkanResourceLifetimeKey key,
        ulong generation)
    {
        bool nativeBufferPublicationChanged = false;
        if (ResourceLifetimes.TryGetValue(
                key,
                out VulkanResourceLifetimeRecord? resource))
        {
            nativeBufferPublicationChanged =
                key.Type == ObjectType.Buffer && resource.PublishedGeneration != generation;
            resource.PublishedGeneration = generation;
        }

        PublishedResourceGenerations[key] = generation;
        // Publish the exact generation before releasing the buffer epoch. A
        // freeze that observes this newer epoch must never resolve the old map value.
        if (nativeBufferPublicationChanged)
            IncrementNativeBufferBindingRevisionNoLock();
    }

    internal void RecycleResourceGenerationNoLock(
        VulkanResourceLifetimeRecord resource,
        ulong generation)
    {
        if (generation == 0UL)
            throw new ArgumentOutOfRangeException(nameof(generation));
        if (!TryResolveResourceSlotNoLock(
                resource.Slot,
                out VulkanResourceLifetimeRecord current) ||
            !ReferenceEquals(current, resource))
        {
            throw new InvalidOperationException(
                $"Cannot recycle unindexed Vulkan resource {resource.Key}.");
        }

        resource.Generation = generation;
        if (resource.Key.Type == ObjectType.Buffer)
            IncrementNativeBufferBindingRevisionNoLock();
        resource.Slot = new VulkanResourceSlotHandle(
            resource.Slot.Index,
            generation);
        resource.PublishedDescriptorSnapshot = null;
    }

    internal bool RemoveResourceNoLock(VulkanResourceLifetimeKey key)
    {
        if (!ResourceLifetimes.TryGetValue(
                key,
                out VulkanResourceLifetimeRecord? resource))
        {
            PublishedResourceGenerations.TryRemove(key, out _);
            return false;
        }
        if (!DeviceLost &&
            ForcedRetirementDrainDepth == 0 &&
            !resource.Pins.IsRetirementReady(
                CompletedGraphicsSequence,
                CompletedTransferSequence,
                CompletedOtherSequence))
        {
            throw new InvalidOperationException(
                $"Cannot release Vulkan resource slot {resource.Slot} for {key} while its generation is still pinned or in flight.");
        }

        _ = ResourceLifetimes.Remove(key);
        PublishedResourceGenerations.TryRemove(key, out _);
        if (key.Type == ObjectType.Buffer)
            IncrementNativeBufferBindingRevisionNoLock();
        if (resource.Key.Type == ObjectType.DescriptorSet)
            PublishedDescriptorSets.TryRemove(resource.Key.Handle, out _);
        ReleaseResourceSlotNoLock(resource);
        return true;
    }

    /// <summary>
    /// Removes a reusable native identity from the keyed publication tables while
    /// retaining its exact slot until the detached native generation is destroyed.
    /// Existing queue receipts can therefore release the old generation without
    /// colliding with a new resource that reuses the same Vulkan handle.
    /// </summary>
    internal bool TryDetachExternalResourceIdentityNoLock(
        VulkanResourceLifetimeKey key,
        out VulkanResourceSlotHandle slot)
    {
        if (!ResourceLifetimes.TryGetValue(
                key,
                out VulkanResourceLifetimeRecord? resource))
        {
            PublishedResourceGenerations.TryRemove(key, out _);
            slot = VulkanResourceSlotHandle.Invalid;
            return false;
        }
        if ((resource.State & EVulkanResourceLifetimeState.External) == 0)
        {
            throw new InvalidOperationException(
                $"Only externally owned Vulkan resources may detach their native identity ({key}).");
        }
        if (!resource.Slot.IsValid ||
            !TryResolveResourceSlotNoLock(
                resource.Slot,
                out VulkanResourceLifetimeRecord indexed) ||
            !ReferenceEquals(indexed, resource))
        {
            throw new InvalidOperationException(
                $"Cannot detach unindexed external Vulkan resource {key} generation {resource.Generation}.");
        }

        VulkanPinnedResourceGeneration pinned = new(key, resource.Generation);
        if (DetachedResourceSlots.ContainsKey(pinned))
        {
            throw new InvalidOperationException(
                $"External Vulkan resource {key} generation {resource.Generation} is already detached.");
        }

        PublishedResourceGenerations.TryRemove(key, out _);
        if (key.Type == ObjectType.Buffer)
            IncrementNativeBufferBindingRevisionNoLock();
        resource.PublishedGeneration = 0UL;
        resource.PublishedDescriptorSnapshot = null;
        resource.State |= EVulkanResourceLifetimeState.PendingRetirement;
        slot = resource.Slot;
        DetachedResourceSlots.Add(pinned, slot);
        if (!ResourceLifetimes.Remove(key))
        {
            _ = DetachedResourceSlots.Remove(pinned);
            throw new InvalidOperationException(
                $"External Vulkan resource {key} changed while detaching generation {resource.Generation}.");
        }
        ResourceCommandBufferDependencies.Remove(key);
        return true;
    }

    /// <summary>
    /// Releases a previously detached exact slot after the owner proves that the
    /// corresponding native generation is no longer referenced by the GPU.
    /// </summary>
    internal bool ReleaseDetachedResourceSlotNoLock(
        VulkanResourceLifetimeKey expectedKey,
        VulkanResourceSlotHandle slot,
        bool forced)
    {
        VulkanPinnedResourceGeneration pinned = new(
            expectedKey,
            slot.Generation);
        if (!slot.IsValid ||
            !DetachedResourceSlots.TryGetValue(
                pinned,
                out VulkanResourceSlotHandle indexedSlot) ||
            indexedSlot != slot ||
            !TryResolveResourceSlotNoLock(
                slot,
                out VulkanResourceLifetimeRecord resource) ||
            resource.Key != expectedKey ||
            resource.Generation != slot.Generation)
        {
            return false;
        }
        if (ResourceLifetimes.TryGetValue(
                expectedKey,
                out VulkanResourceLifetimeRecord? published) &&
            ReferenceEquals(published, resource))
        {
            throw new InvalidOperationException(
                $"Cannot release attached Vulkan resource slot {slot} for {expectedKey} through the detached-generation path.");
        }
        if (!forced &&
            !DeviceLost &&
            ForcedRetirementDrainDepth == 0 &&
            !resource.Pins.IsRetirementReady(
                CompletedGraphicsSequence,
                CompletedTransferSequence,
                CompletedOtherSequence))
        {
            throw new InvalidOperationException(
                $"Cannot release detached Vulkan resource slot {slot} for {expectedKey} while its generation is still pinned or in flight.");
        }

        _ = DetachedResourceSlots.Remove(pinned);
        resource.State = EVulkanResourceLifetimeState.Destroyed;
        ReleaseResourceSlotNoLock(resource);
        return true;
    }

    internal void RegisterResource(
        VulkanResourceLifetimeKey key,
        string owner,
        bool externallyOwned)
    {
        if (!key.IsValid)
            return;

        using (VulkanFrameLockScope.Enter(
                   SyncRoot,
                   EVulkanFrameWaitReason.ResourceLifetimeLock))
        {
            if (ResourceLifetimes.TryGetValue(key, out VulkanResourceLifetimeRecord? existing))
            {
                if ((existing.State & EVulkanResourceLifetimeState.PendingRetirement) != 0)
                {
                    throw new InvalidOperationException(
                        $"Vulkan handle {key} was recycled by {owner} while generation {existing.Generation} is still pending retirement.");
                }

                if ((existing.State & EVulkanResourceLifetimeState.Destroyed) == 0)
                {
                    existing.Owner = owner;
                    // TryAdd preserves a zero-valued retirement admission fence. Re-registering
                    // an already-live handle must not reopen recording while retirement is
                    // publishing command-buffer dependencies for that generation.
                    PublishedResourceGenerations.TryAdd(key, existing.Generation);
                    existing.PublishedGeneration =
                        PublishedResourceGenerations.TryGetValue(
                            key,
                            out ulong publishedGeneration)
                                ? publishedGeneration
                                : 0UL;
                    if (externallyOwned)
                        existing.State |= EVulkanResourceLifetimeState.External;
                    return;
                }

                _ = RemoveResourceNoLock(key);
            }

            ulong generation = VulkanGeneration.IncrementNonZero(ref ResourceGeneration);
            VulkanResourceLifetimeRecord resource = new()
            {
                Key = key,
                Generation = generation,
                Owner = string.IsNullOrWhiteSpace(owner) ? "<unknown>" : owner,
                PublishedGeneration = generation,
                State = EVulkanResourceLifetimeState.CpuOwned |
                    (externallyOwned
                        ? EVulkanResourceLifetimeState.External
                        : EVulkanResourceLifetimeState.None),
            };
            resource.Slot = AllocateResourceSlotNoLock(resource);
            ResourceLifetimes[key] = resource;
            PublishedResourceGenerations[key] = generation;
            if (key.Type == ObjectType.Buffer)
                IncrementNativeBufferBindingRevisionNoLock();
        }
    }

    internal VulkanResourceLifetimeRecord GetOrRegisterResourceNoLock(
        VulkanResourceLifetimeKey key,
        string owner)
    {
        if (ResourceLifetimes.TryGetValue(key, out VulkanResourceLifetimeRecord? record))
            return record;

        ulong generation = VulkanGeneration.IncrementNonZero(ref ResourceGeneration);
        record = new VulkanResourceLifetimeRecord
        {
            Key = key,
            Generation = generation,
            Owner = owner,
            PublishedGeneration = generation,
            State = EVulkanResourceLifetimeState.CpuOwned,
        };
        record.Slot = AllocateResourceSlotNoLock(record);
        ResourceLifetimes[key] = record;
        PublishedResourceGenerations[key] = generation;
        if (key.Type == ObjectType.Buffer)
            IncrementNativeBufferBindingRevisionNoLock();
        return record;
    }

    /// <summary>
    /// Closes lock-free command-recording admission for one resource generation before
    /// retirement scans local dependency batches. A zero published generation directs
    /// new recorders through the synchronized lifetime path, where they either publish
    /// a dependency before retirement commits or observe the pending-retirement state.
    /// </summary>
    internal void FenceResourceRecordingAdmission(
        VulkanResourceLifetimeKey key,
        string owner)
    {
        using (VulkanFrameLockScope.Enter(
                   SyncRoot,
                   EVulkanFrameWaitReason.ResourceLifetimeLock))
        {
            _ = GetOrRegisterResourceNoLock(key, owner);
            SetPublishedGenerationNoLock(key, 0UL);
        }
    }

    private void IncrementNativeBufferBindingRevisionNoLock()
    {
        long revision = unchecked(NativeBufferBindingRevision + 1L);
        Volatile.Write(ref NativeBufferBindingRevision, revision == 0L ? 1L : revision);
    }

    private VulkanResourceSlotHandle AllocateResourceSlotNoLock(
        VulkanResourceLifetimeRecord resource)
    {
        uint index;
        if (_freeResourceSlotHead != 0u)
        {
            index = _freeResourceSlotHead;
            _freeResourceSlotHead =
                _resourceSlotFreeLinks[checked((int)index)];
            _resourceSlotFreeLinks[checked((int)index)] = 0u;
        }
        else
        {
            index = _resourceSlotCount++;
            EnsureResourceSlotCapacity(index);
        }

        VulkanResourceSlotHandle slot = new(index, resource.Generation);
        _resourceSlots[checked((int)index)] = resource;
        return slot;
    }

    private void ReleaseResourceSlotNoLock(
        VulkanResourceLifetimeRecord resource)
    {
        VulkanResourceSlotHandle slot = resource.Slot;
        if (!slot.IsValid || slot.Index >= _resourceSlotCount)
            return;

        int index = checked((int)slot.Index);
        if (!ReferenceEquals(_resourceSlots[index], resource))
            return;

        _resourceSlots[index] = null;
        _resourceSlotFreeLinks[index] = _freeResourceSlotHead;
        _freeResourceSlotHead = slot.Index;
        resource.Slot = VulkanResourceSlotHandle.Invalid;
        resource.PublishedGeneration = 0UL;
        resource.PublishedDescriptorSnapshot = null;
    }

    private void EnsureResourceSlotCapacity(uint requiredIndex)
    {
        if (requiredIndex < (uint)_resourceSlots.Length)
            return;

        int capacity = _resourceSlots.Length;
        do
            capacity = checked(capacity * 2);
        while (requiredIndex >= (uint)capacity);

        Array.Resize(ref _resourceSlots, capacity);
        Array.Resize(ref _resourceSlotFreeLinks, capacity);
        Array.Resize(ref _submissionDependencyGenerationScratch, capacity);
        Array.Resize(ref _submissionDependencyEpochScratch, capacity);
    }

    internal VulkanRetirementTicket CaptureRetirementWatermark()
    {
        using (VulkanFrameLockScope.Enter(
                   SyncRoot,
                   EVulkanFrameWaitReason.ResourceLifetimeLock))
        {
            return new VulkanRetirementTicket(
                LastGraphicsSequence,
                LastTransferSequence,
                LastOtherSequence,
                Stopwatch.GetTimestamp(),
                0,
                false);
        }
    }

    internal void MarkQueueSequenceCompletedNoLock(
        EVulkanLifetimeQueueDomain domain,
        ulong queueSequence)
    {
        if (queueSequence == 0)
            return;

        ulong queueHandle = 0;
        for (int index = 0; index < LifetimeSubmissions.Count; ++index)
        {
            VulkanLifetimeSubmission submission = LifetimeSubmissions[index];
            if (submission.QueueDomain != domain ||
                submission.QueueSequence != queueSequence)
            {
                continue;
            }

            queueHandle = submission.QueueHandle;
            break;
        }

        // A completion event must name a currently published submission. If a
        // previous event already reaped it, it has already contributed to the
        // frontier and there is no new native proof to record.
        if (queueHandle == 0)
            return;

        // SubmitToQueueTrackedWithDisposition keeps its submission-state gate
        // through both vkQueueSubmit and lifetime publication. Consequently a
        // lower per-domain sequence on this queue was submitted to Vulkan no
        // later than the completed submission. Do not extend this proof to a
        // different queue in the same domain: graphics and secondary graphics
        // queues execute independently.
        for (int index = 0; index < LifetimeSubmissions.Count; ++index)
        {
            VulkanLifetimeSubmission submission = LifetimeSubmissions[index];
            if (submission.QueueDomain != domain ||
                submission.QueueHandle != queueHandle ||
                submission.QueueSequence > queueSequence ||
                submission.CompletionObserved)
            {
                continue;
            }

            LifetimeSubmissions[index] = submission with
            {
                CompletionObserved = true,
            };
        }

        SetObservedSequenceNoLock(
            domain,
            Math.Max(GetObservedSequenceNoLock(domain), queueSequence));
        AdvanceCompletionFrontierNoLock(domain);
    }

    /// <summary>
    /// Compacts lifetime entries whose native completion has already been
    /// observed. Call while <see cref="SyncRoot"/> is held after completion
    /// polling so completed bookkeeping cannot grow indefinitely.
    /// </summary>
    internal void ReapObservedSubmissionsNoLock()
    {
        int writeIndex = 0;
        for (int readIndex = 0; readIndex < LifetimeSubmissions.Count; ++readIndex)
        {
            VulkanLifetimeSubmission submission = LifetimeSubmissions[readIndex];
            if (submission.CompletionObserved)
                continue;

            if (writeIndex != readIndex)
                LifetimeSubmissions[writeIndex] = submission;
            writeIndex++;
        }

        int removedCount = LifetimeSubmissions.Count - writeIndex;
        if (removedCount != 0)
            LifetimeSubmissions.RemoveRange(writeIndex, removedCount);

        AdvanceCompletionFrontierNoLock(EVulkanLifetimeQueueDomain.Graphics);
        AdvanceCompletionFrontierNoLock(EVulkanLifetimeQueueDomain.Transfer);
        AdvanceCompletionFrontierNoLock(EVulkanLifetimeQueueDomain.Other);
    }

    private void AdvanceCompletionFrontierNoLock(EVulkanLifetimeQueueDomain domain)
    {
        ulong firstPendingSequence = 0;
        for (int index = 0; index < LifetimeSubmissions.Count; ++index)
        {
            VulkanLifetimeSubmission submission = LifetimeSubmissions[index];
            if (submission.QueueDomain != domain || submission.CompletionObserved)
                continue;

            if (firstPendingSequence == 0 ||
                submission.QueueSequence < firstPendingSequence)
            {
                firstPendingSequence = submission.QueueSequence;
            }
        }

        ulong frontier = firstPendingSequence == 0
            ? GetObservedSequenceNoLock(domain)
            : firstPendingSequence - 1;
        SetCompletedSequenceNoLock(
            domain,
            Math.Max(GetCompletedSequenceNoLock(domain), frontier));
    }

    private ulong GetCompletedSequenceNoLock(EVulkanLifetimeQueueDomain domain)
        => domain switch
        {
            EVulkanLifetimeQueueDomain.Graphics => CompletedGraphicsSequence,
            EVulkanLifetimeQueueDomain.Transfer => CompletedTransferSequence,
            _ => CompletedOtherSequence,
        };

    private void SetCompletedSequenceNoLock(
        EVulkanLifetimeQueueDomain domain,
        ulong sequence)
    {
        switch (domain)
        {
            case EVulkanLifetimeQueueDomain.Graphics:
                CompletedGraphicsSequence = sequence;
                break;
            case EVulkanLifetimeQueueDomain.Transfer:
                CompletedTransferSequence = sequence;
                break;
            default:
                CompletedOtherSequence = sequence;
                break;
        }
    }

    private ulong GetObservedSequenceNoLock(EVulkanLifetimeQueueDomain domain)
        => domain switch
        {
            EVulkanLifetimeQueueDomain.Graphics => ObservedGraphicsSequence,
            EVulkanLifetimeQueueDomain.Transfer => ObservedTransferSequence,
            _ => ObservedOtherSequence,
        };

    private void SetObservedSequenceNoLock(
        EVulkanLifetimeQueueDomain domain,
        ulong sequence)
    {
        switch (domain)
        {
            case EVulkanLifetimeQueueDomain.Graphics:
                ObservedGraphicsSequence = sequence;
                break;
            case EVulkanLifetimeQueueDomain.Transfer:
                ObservedTransferSequence = sequence;
                break;
            default:
                ObservedOtherSequence = sequence;
                break;
        }
    }

    /// <summary>
    /// Publishes the completion boundary established by a successful
    /// <c>vkDeviceWaitIdle</c>. Native idleness covers every queue submission,
    /// so retaining older software completion counters would reject resources
    /// whose pins are already safe to destroy during teardown.
    /// </summary>
    internal void MarkDeviceIdleCompleted()
    {
        using (VulkanFrameLockScope.Enter(
                   SyncRoot,
                   EVulkanFrameWaitReason.ResourceLifetimeLock))
        {
            CompletedGraphicsSequence = Math.Max(
                CompletedGraphicsSequence,
                LastGraphicsSequence);
            ObservedGraphicsSequence = Math.Max(
                ObservedGraphicsSequence,
                LastGraphicsSequence);
            CompletedTransferSequence = Math.Max(
                CompletedTransferSequence,
                LastTransferSequence);
            ObservedTransferSequence = Math.Max(
                ObservedTransferSequence,
                LastTransferSequence);
            CompletedOtherSequence = Math.Max(
                CompletedOtherSequence,
                LastOtherSequence);
            ObservedOtherSequence = Math.Max(
                ObservedOtherSequence,
                LastOtherSequence);
            // Device idleness proves every entry in the software ledger, so
            // retaining any of them as unresolved would artificially cap the
            // frontier for later submissions. Clearing is safe here because
            // the device-wide native wait provides stronger proof than a
            // per-queue fence or timeline marker.
            LifetimeSubmissions.Clear();
        }
    }

    internal bool IsRetirementReady(in VulkanRetirementTicket ticket)
    {
        using (VulkanFrameLockScope.Enter(
                   SyncRoot,
                   EVulkanFrameWaitReason.ResourceLifetimeLock))
            return IsRetirementReadyNoLock(ticket);
    }

    internal bool IsRetirementReadyNoLock(in VulkanRetirementTicket ticket)
    {
        if (ForcedRetirementDrainDepth > 0)
            return true;
        if (DeviceLost ||
            ticket.GraphicsSequence > CompletedGraphicsSequence ||
            ticket.TransferSequence > CompletedTransferSequence ||
            ticket.OtherSequence > CompletedOtherSequence)
        {
            return false;
        }

        VulkanRetirementPinSet? pinSet = ticket.PinSet;
        if (pinSet is null)
            return !ticket.ExternalOwnershipPending;

        bool externalOwnershipStillPending = false;
        ReadOnlySpan<VulkanPinnedResourceGeneration> pinnedResources = pinSet.Resources;
        for (int i = 0; i < pinnedResources.Length; i++)
        {
            VulkanPinnedResourceGeneration pinned = pinnedResources[i];
            if (!TryResolveResourceGenerationNoLock(
                    pinned.Key,
                    pinned.Generation,
                    out VulkanResourceLifetimeRecord resource) ||
                (resource.State & EVulkanResourceLifetimeState.Destroyed) != 0)
            {
                continue;
            }

            externalOwnershipStillPending |=
                (resource.State & EVulkanResourceLifetimeState.External) != 0;
            if (!resource.Pins.IsRetirementReady(
                    CompletedGraphicsSequence,
                    CompletedTransferSequence,
                    CompletedOtherSequence))
            {
                return false;
            }
        }

        return !externalOwnershipStillPending;
    }
}
