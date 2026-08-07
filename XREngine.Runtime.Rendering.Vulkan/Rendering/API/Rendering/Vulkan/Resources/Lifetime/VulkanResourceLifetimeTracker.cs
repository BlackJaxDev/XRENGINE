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
    internal object SyncRoot { get; } = new();

    internal ConcurrentDictionary<VulkanResourceLifetimeKey, ulong> PublishedResourceGenerations { get; } = new();
    internal Dictionary<VulkanResourceLifetimeKey, VulkanResourceLifetimeRecord> ResourceLifetimes { get; } = new();
    internal Dictionary<ulong, VulkanCommandBufferLifetimeRecord> CommandBufferLifetimes { get; } = new();
    /// <summary>
    /// Persistent allocation ownership, separate from a command buffer's transient
    /// recording dependencies. Destroying a command pool implicitly frees every
    /// child, so the pool must retain this relation until that native destruction
    /// has settled every child generation.
    /// </summary>
    internal Dictionary<VulkanResourceLifetimeKey, HashSet<ulong>> CommandBuffersByPool { get; } = new();
    internal Dictionary<VulkanResourceLifetimeKey, HashSet<ulong>> ResourceCommandBufferDependencies { get; } = new();
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
        lock (SyncRoot)
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
                    continue;

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
    internal Dictionary<ulong, VulkanResourceLifetimeKey[]> FramebufferAttachments { get; } = new();

    /// <summary>
    /// Retained submission lookup shared by serialized queue submissions.
    /// </summary>
    internal Dictionary<VulkanResourceLifetimeKey, ulong> SubmissionDependencyGenerationsScratch { get; } = new(4096);

    internal List<VulkanLifetimeSubmission> LifetimeSubmissions { get; } = new(16);
    internal ThreadLocal<HashSet<ulong>> ChangedDescriptorSetsScratch { get; } = new(static () => []);
    internal ThreadLocal<HashSet<VulkanResourceLifetimeKey>> DescriptorReferencesScratch { get; } = new(static () => []);
    internal ThreadLocal<HashSet<VulkanResourceLifetimeKey>> DescriptorPinnedReferencesScratch { get; } = new(static () => []);

    internal long ResourceGeneration;
    internal long RetirementSerial;
    internal ulong LastGraphicsSequence;
    internal ulong CompletedGraphicsSequence;
    internal ulong LastTransferSequence;
    internal ulong CompletedTransferSequence;
    internal ulong LastOtherSequence;
    internal ulong CompletedOtherSequence;
    internal long ForcedResourceDestructionCount;
    internal bool DeviceLost;
    internal int ForcedRetirementDrainDepth;

    internal ulong GetPublishedGeneration(VulkanResourceLifetimeKey key)
        => key.IsValid && PublishedResourceGenerations.TryGetValue(key, out ulong generation)
            ? generation
            : 0;

    internal void RegisterResource(
        VulkanResourceLifetimeKey key,
        string owner,
        bool externallyOwned)
    {
        if (!key.IsValid)
            return;

        lock (SyncRoot)
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
                    if (externallyOwned)
                        existing.State |= EVulkanResourceLifetimeState.External;
                    return;
                }
            }

            ulong generation = VulkanGeneration.IncrementNonZero(ref ResourceGeneration);
            ResourceLifetimes[key] = new VulkanResourceLifetimeRecord
            {
                Key = key,
                Generation = generation,
                Owner = string.IsNullOrWhiteSpace(owner) ? "<unknown>" : owner,
                State = EVulkanResourceLifetimeState.CpuOwned |
                    (externallyOwned
                        ? EVulkanResourceLifetimeState.External
                        : EVulkanResourceLifetimeState.None),
            };
            PublishedResourceGenerations[key] = generation;
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
            State = EVulkanResourceLifetimeState.CpuOwned,
        };
        ResourceLifetimes[key] = record;
        PublishedResourceGenerations[key] = generation;
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
        lock (SyncRoot)
        {
            _ = GetOrRegisterResourceNoLock(key, owner);
            PublishedResourceGenerations[key] = 0;
        }
    }

    internal VulkanRetirementTicket CaptureRetirementWatermark()
    {
        lock (SyncRoot)
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
        switch (domain)
        {
            case EVulkanLifetimeQueueDomain.Graphics:
                CompletedGraphicsSequence = Math.Max(CompletedGraphicsSequence, queueSequence);
                break;
            case EVulkanLifetimeQueueDomain.Transfer:
                CompletedTransferSequence = Math.Max(CompletedTransferSequence, queueSequence);
                break;
            default:
                CompletedOtherSequence = Math.Max(CompletedOtherSequence, queueSequence);
                break;
        }
    }

    internal bool IsRetirementReady(in VulkanRetirementTicket ticket)
    {
        lock (SyncRoot)
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
            if (!ResourceLifetimes.TryGetValue(
                    pinned.Key,
                    out VulkanResourceLifetimeRecord? resource) ||
                resource.Generation != pinned.Generation ||
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
