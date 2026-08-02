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

    internal ConcurrentDictionary<VulkanRenderer.VulkanResourceLifetimeKey, ulong> PublishedResourceGenerations { get; } = new();
    internal Dictionary<VulkanRenderer.VulkanResourceLifetimeKey, VulkanRenderer.VulkanResourceLifetimeRecord> ResourceLifetimes { get; } = new();
    internal Dictionary<ulong, VulkanRenderer.VulkanCommandBufferLifetimeRecord> CommandBufferLifetimes { get; } = new();
    internal Dictionary<VulkanRenderer.VulkanResourceLifetimeKey, HashSet<ulong>> ResourceCommandBufferDependencies { get; } = new();
    internal Dictionary<ulong, VulkanRenderer.VulkanDescriptorSetLifetimeRecord> DescriptorSetLifetimes { get; } = new();
    internal Dictionary<ulong, List<VkRenderQuery>> RenderQueriesByPool { get; } = new();
    internal Dictionary<ulong, HashSet<ulong>> DescriptorSetsByPool { get; } = new();
    internal Dictionary<VulkanRenderer.VulkanResourceLifetimeKey, HashSet<ulong>> DescriptorSetsByReferencedResource { get; } = new();
    internal ConcurrentDictionary<ulong, VulkanRenderer.VulkanPublishedDescriptorSetSnapshot> PublishedDescriptorSets { get; } = new();
    internal Dictionary<ulong, ulong> ImageViewBackingImages { get; } = new();
    internal Dictionary<ulong, ulong> BufferViewBackingBuffers { get; } = new();
    internal Dictionary<ulong, VulkanRenderer.VulkanResourceLifetimeKey[]> FramebufferAttachments { get; } = new();

    /// <summary>
    /// Retained submission lookup shared by serialized queue submissions.
    /// </summary>
    internal Dictionary<VulkanRenderer.VulkanResourceLifetimeKey, ulong> SubmissionDependencyGenerationsScratch { get; } = new(4096);

    internal List<VulkanLifetimeSubmission> LifetimeSubmissions { get; } = new(16);
    internal ThreadLocal<HashSet<ulong>> ChangedDescriptorSetsScratch { get; } = new(static () => []);
    internal ThreadLocal<HashSet<VulkanRenderer.VulkanResourceLifetimeKey>> DescriptorReferencesScratch { get; } = new(static () => []);
    internal ThreadLocal<HashSet<VulkanRenderer.VulkanResourceLifetimeKey>> DescriptorPinnedReferencesScratch { get; } = new(static () => []);

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

    internal ulong GetPublishedGeneration(VulkanRenderer.VulkanResourceLifetimeKey key)
        => key.IsValid && PublishedResourceGenerations.TryGetValue(key, out ulong generation)
            ? generation
            : 0;

    internal void RegisterResource(
        VulkanRenderer.VulkanResourceLifetimeKey key,
        string owner,
        bool externallyOwned)
    {
        if (!key.IsValid)
            return;

        lock (SyncRoot)
        {
            if (ResourceLifetimes.TryGetValue(key, out VulkanRenderer.VulkanResourceLifetimeRecord? existing))
            {
                if ((existing.State & VulkanRenderer.EVulkanResourceLifetimeState.PendingRetirement) != 0)
                {
                    throw new InvalidOperationException(
                        $"Vulkan handle {key} was recycled by {owner} while generation {existing.Generation} is still pending retirement.");
                }

                if ((existing.State & VulkanRenderer.EVulkanResourceLifetimeState.Destroyed) == 0)
                {
                    existing.Owner = owner;
                    // TryAdd preserves a zero-valued retirement admission fence. Re-registering
                    // an already-live handle must not reopen recording while retirement is
                    // publishing command-buffer dependencies for that generation.
                    PublishedResourceGenerations.TryAdd(key, existing.Generation);
                    if (externallyOwned)
                        existing.State |= VulkanRenderer.EVulkanResourceLifetimeState.External;
                    return;
                }
            }

            ulong generation = VulkanGeneration.IncrementNonZero(ref ResourceGeneration);
            ResourceLifetimes[key] = new VulkanRenderer.VulkanResourceLifetimeRecord
            {
                Key = key,
                Generation = generation,
                Owner = string.IsNullOrWhiteSpace(owner) ? "<unknown>" : owner,
                State = VulkanRenderer.EVulkanResourceLifetimeState.CpuOwned |
                    (externallyOwned
                        ? VulkanRenderer.EVulkanResourceLifetimeState.External
                        : VulkanRenderer.EVulkanResourceLifetimeState.None),
            };
            PublishedResourceGenerations[key] = generation;
        }
    }

    internal VulkanRenderer.VulkanResourceLifetimeRecord GetOrRegisterResourceNoLock(
        VulkanRenderer.VulkanResourceLifetimeKey key,
        string owner)
    {
        if (ResourceLifetimes.TryGetValue(key, out VulkanRenderer.VulkanResourceLifetimeRecord? record))
            return record;

        ulong generation = VulkanGeneration.IncrementNonZero(ref ResourceGeneration);
        record = new VulkanRenderer.VulkanResourceLifetimeRecord
        {
            Key = key,
            Generation = generation,
            Owner = owner,
            State = VulkanRenderer.EVulkanResourceLifetimeState.CpuOwned,
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
        VulkanRenderer.VulkanResourceLifetimeKey key,
        string owner)
    {
        lock (SyncRoot)
        {
            _ = GetOrRegisterResourceNoLock(key, owner);
            PublishedResourceGenerations[key] = 0;
        }
    }

    internal VulkanRenderer.VulkanRetirementTicket CaptureRetirementWatermark()
    {
        lock (SyncRoot)
        {
            return new VulkanRenderer.VulkanRetirementTicket(
                LastGraphicsSequence,
                LastTransferSequence,
                LastOtherSequence,
                Stopwatch.GetTimestamp(),
                0,
                false);
        }
    }

    internal void MarkQueueSequenceCompletedNoLock(
        VulkanRenderer.EVulkanLifetimeQueueDomain domain,
        ulong queueSequence)
    {
        switch (domain)
        {
            case VulkanRenderer.EVulkanLifetimeQueueDomain.Graphics:
                CompletedGraphicsSequence = Math.Max(CompletedGraphicsSequence, queueSequence);
                break;
            case VulkanRenderer.EVulkanLifetimeQueueDomain.Transfer:
                CompletedTransferSequence = Math.Max(CompletedTransferSequence, queueSequence);
                break;
            default:
                CompletedOtherSequence = Math.Max(CompletedOtherSequence, queueSequence);
                break;
        }
    }

    internal bool IsRetirementReady(in VulkanRenderer.VulkanRetirementTicket ticket)
    {
        lock (SyncRoot)
            return IsRetirementReadyNoLock(ticket);
    }

    internal bool IsRetirementReadyNoLock(in VulkanRenderer.VulkanRetirementTicket ticket)
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

        VulkanRenderer.VulkanRetirementPinSet? pinSet = ticket.PinSet;
        if (pinSet is null)
            return !ticket.ExternalOwnershipPending;

        bool externalOwnershipStillPending = false;
        ReadOnlySpan<VulkanRenderer.VulkanPinnedResourceGeneration> pinnedResources = pinSet.Resources;
        for (int i = 0; i < pinnedResources.Length; i++)
        {
            VulkanRenderer.VulkanPinnedResourceGeneration pinned = pinnedResources[i];
            if (!ResourceLifetimes.TryGetValue(
                    pinned.Key,
                    out VulkanRenderer.VulkanResourceLifetimeRecord? resource) ||
                resource.Generation != pinned.Generation ||
                (resource.State & VulkanRenderer.EVulkanResourceLifetimeState.Destroyed) != 0)
            {
                continue;
            }

            externalOwnershipStillPending |=
                (resource.State & VulkanRenderer.EVulkanResourceLifetimeState.External) != 0;
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
