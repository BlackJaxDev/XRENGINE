using System.Threading;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Silk.NET.Vulkan;
using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Vulkan.Commands.Readback;

namespace XREngine.Rendering.Vulkan;

/// <summary>Owns native command artifacts, bind caches, diagnostics, and reusable recording scratch.</summary>
internal sealed class VulkanCommandBufferState
{
    private const int InvalidatedCommandSetCapacity = 16384;
    private readonly object _invalidatedCommandSetGate = new();
    private readonly ulong[] _invalidatedCommandHandles =
        new ulong[InvalidatedCommandSetCapacity];
    private readonly byte[] _invalidatedCommandHandleStates =
        new byte[InvalidatedCommandSetCapacity];
    /// <summary>
    /// Native swapchain handle that owns the current desktop command-artifact
    /// generation in the reverse-dependency graph. This is intentionally kept
    /// beside the image-indexed arrays so output recreation retires the exact
    /// publisher before a replacement generation can reuse a native handle.
    /// </summary>
    internal ulong DesktopOutputNativeDependencyIdentity;
    internal CommandBuffer[]? Buffers;
    internal CommandBuffer[]? ActiveBuffers;
    internal VulkanPrimaryCommandPlan[]? PrimaryPlans;
    internal PrimaryCommandArtifactOwner[]? PrimaryOwners;
    internal object OpenXrPrimaryOwnersGate { get; } = new();
    internal Dictionary<ulong, PrimaryCommandArtifactOwner> OpenXrPrimaryOwners { get; } = [];
    internal CommandBuffer[]? DynamicUiSecondaries;
    internal CommandBuffer[]? DynamicUiOverlays;
    internal int[]? DynamicUiOpCounts;
    internal ulong[]? DynamicUiSignatures;
    internal ulong[]? FrameOpSignatures;
    internal ulong[]? PlannerRevisions;
    internal ComputeTransientResources[]? ComputeTransientResources;
    internal List<DeferredSecondaryCommandBuffer>[]? DeferredSecondaries;
    internal object OneTimePoolsGate { get; } = new();
    internal Dictionary<nint, OneTimeCommandOwner> OneTimePools { get; } = new();
    internal object OneTimeSubmitGate { get; } = new();
    internal object SubmissionStateGate { get; } = new();
    internal ReaderWriterLockSlim DeviceQueueAdmissionGate { get; set; } =
        new(LockRecursionPolicy.NoRecursion);
    internal object BindStateGate { get; } = new();
    internal Dictionary<ulong, CommandBufferBindState> BindStates { get; } = new();
    internal Dictionary<ulong, int> ImageIndices { get; } = new();
    internal long RecordingGeneration;
    internal object OwnedSecondaryPoolsGate { get; } = new();
    internal Dictionary<ulong, OwnedCommandChainSecondaryPool> OwnedSecondaryPools { get; } = new();
    internal bool EnableSecondary = true;
    internal bool EnableComputeSecondary = true;
    internal bool EnableTransferSecondary = true;
    internal bool EnableQuerySecondary = true;
    internal FrameOpSignatureDebugPart[][]? SignatureDebugParts;
    internal int SignatureDiffLogCount;
    internal string? DiagnosticBaseWindowTitle;
    internal string? DiagnosticLastTitle;
    internal int LastFrameDroppedDrawOps;
    internal int LastFrameDroppedOps;
    internal ThreadLocal<CommandBufferRecordingScratch> RecordingScratch { get; } =
        new(static () => new CommandBufferRecordingScratch());
    internal VulkanFrameWideMeshFrameDataReservationManifest FrameWideMeshDataManifest { get; } = new();
    internal long ObservedMeshFrameDataManifestGeneration;
    internal bool LastEnsureRecordedPrimary;
    internal string? LastReusableFrameDataRefreshFailureReason;
    internal Dictionary<ulong, CameraPoseReuseState> CameraPoseReuseStates { get; } = new(8);
    internal VkDataBuffer? BoundIndirectBuffer;
    internal VkDataBuffer? BoundParameterBuffer;
    internal VkMeshRenderer? BoundMeshRendererForIndirect;
    internal IndexType BoundIndexType = IndexType.Uint32;
    internal uint BoundIndexCount;
    internal VulkanIndirectDrawState? PendingIndirectDrawState;
    internal VulkanReadbackTaskTracker ReadbackTasks { get; } = new();
    internal ulong MaterialUniformFrameTimeFrameId = ulong.MaxValue;
    internal float MaterialUniformUpdateDeltaLive;
    internal float MaterialUniformSecondsLive;
    internal float MaterialUniformDeltaSecondsLive;
    internal XRMaterial? ShadowBindingSourceMaterial;
    internal XRRenderProgram? ShadowBindingProgram;
    internal ulong ShadowBindingSourceLayoutVersion = ulong.MaxValue;
    internal MaterialShadowBindingPlan? ShadowBindingPlan;
    internal object ForwardLightingGate { get; } = new();
    internal Dictionary<ForwardLightingBindingSnapshotCacheKey, ComputeDispatchSnapshot> ForwardLightingSnapshots { get; } = [];
    internal ulong ForwardLightingSnapshotFrame;
    internal ForwardLightingBindingSnapshotCacheKey ForwardLightingLastSnapshotKey;
    internal ComputeDispatchSnapshot? ForwardLightingLastSnapshot;
    internal bool HasForwardLightingLastSnapshot;
    internal ConcurrentDictionary<ulong, byte> InvalidatedBuffersPendingReset { get; } = new();
    internal ConcurrentDictionary<ulong, VulkanCommandBufferTrackingBatch> TrackingBatches { get; } = new();
    internal VulkanStableCommandDirectory StableCommandDirectory { get; } = new();
    internal long DirtyGeneration;
    internal long LastDirtyTimestamp;
    internal bool[]? DirtyFlags;
    internal object DirtyReasonGate { get; } = new();
    internal Dictionary<string, int> DirtyReasons { get; } = new(StringComparer.Ordinal);
    internal long LastDirtyReasonLogTimestamp;
    internal XRFrameBuffer? BoundDrawFrameBuffer;
    internal XRFrameBuffer? BoundReadFrameBuffer;
    internal EReadBufferMode ReadBufferMode = EReadBufferMode.ColorAttachment0;

    internal ulong ResolveRecordingGeneration(CommandBuffer commandBuffer)
    {
        if (commandBuffer.Handle == 0)
            return 0;

        ulong key = unchecked((ulong)commandBuffer.Handle);
        lock (BindStateGate)
            return BindStates.TryGetValue(key, out CommandBufferBindState state)
                ? state.RecordingGeneration
                : 0;
    }

    internal void RegisterImageIndex(
        CommandBuffer commandBuffer,
        uint imageIndex)
    {
        if (commandBuffer.Handle == 0)
            return;

        int resolvedImageIndex = unchecked((int)Math.Min(imageIndex, int.MaxValue));
        ulong key = unchecked((ulong)commandBuffer.Handle);
        lock (BindStateGate)
            ImageIndices[key] = resolvedImageIndex;
    }

    internal void RemoveBindState(CommandBuffer commandBuffer)
    {
        if (commandBuffer.Handle == 0)
            return;

        ulong handle = unchecked((ulong)commandBuffer.Handle);
        lock (BindStateGate)
        {
            BindStates.Remove(handle);
            ImageIndices.Remove(handle);
        }

        TrackingBatches.TryRemove(handle, out _);
        InvalidatedBuffersPendingReset.TryRemove(handle, out _);
        RemoveInvalidatedCommandHandle(handle);
    }

    /// <summary>
    /// Clears state invalidated by a successful native reset without dropping
    /// the command buffer's reusable tracking batch.
    /// </summary>
    internal void ClearBindStateAfterSuccessfulReset(CommandBuffer commandBuffer)
    {
        if (commandBuffer.Handle == 0)
            return;

        ulong handle = unchecked((ulong)commandBuffer.Handle);
        lock (BindStateGate)
        {
            BindStates.Remove(handle);
            ImageIndices.Remove(handle);
        }

        InvalidatedBuffersPendingReset.TryRemove(handle, out _);
        RemoveInvalidatedCommandHandle(handle);
    }

    /// <summary>
    /// Publishes pending-reset membership into a fixed open-addressed set used
    /// by the submit gateway. The concurrent dictionary remains the cold
    /// enumerable reset queue and is not consulted by stable submissions.
    /// </summary>
    internal void AddInvalidatedCommandHandle(ulong handle)
    {
        if (handle == 0)
            return;

        lock (_invalidatedCommandSetGate)
        {
            int mask = InvalidatedCommandSetCapacity - 1;
            int start = (int)(MixCommandHandle(handle) & (uint)mask);
            int firstTombstone = -1;
            for (int probe = 0; probe < InvalidatedCommandSetCapacity; ++probe)
            {
                int index = (start + probe) & mask;
                byte state = _invalidatedCommandHandleStates[index];
                if (state == 1)
                {
                    if (_invalidatedCommandHandles[index] == handle)
                        return;
                    continue;
                }
                if (state == 2)
                {
                    if (firstTombstone < 0)
                        firstTombstone = index;
                    continue;
                }

                int destination = firstTombstone >= 0 ? firstTombstone : index;
                _invalidatedCommandHandles[destination] = handle;
                _invalidatedCommandHandleStates[destination] = 1;
                return;
            }

            if (firstTombstone >= 0)
            {
                _invalidatedCommandHandles[firstTombstone] = handle;
                _invalidatedCommandHandleStates[firstTombstone] = 1;
                return;
            }
        }

        throw new InvalidOperationException(
            "The fixed pending-reset command-buffer directory is exhausted.");
    }

    internal bool ContainsInvalidatedCommandHandle(ulong handle)
    {
        if (handle == 0)
            return false;

        lock (_invalidatedCommandSetGate)
        {
            int mask = InvalidatedCommandSetCapacity - 1;
            int start = (int)(MixCommandHandle(handle) & (uint)mask);
            for (int probe = 0; probe < InvalidatedCommandSetCapacity; ++probe)
            {
                int index = (start + probe) & mask;
                byte state = _invalidatedCommandHandleStates[index];
                if (state == 0)
                    return false;
                if (state == 1 && _invalidatedCommandHandles[index] == handle)
                    return true;
            }
        }

        return false;
    }

    internal void RemoveInvalidatedCommandHandle(ulong handle)
    {
        if (handle == 0)
            return;

        lock (_invalidatedCommandSetGate)
        {
            int mask = InvalidatedCommandSetCapacity - 1;
            int start = (int)(MixCommandHandle(handle) & (uint)mask);
            for (int probe = 0; probe < InvalidatedCommandSetCapacity; ++probe)
            {
                int index = (start + probe) & mask;
                byte state = _invalidatedCommandHandleStates[index];
                if (state == 0)
                    return;
                if (state != 1 || _invalidatedCommandHandles[index] != handle)
                    continue;

                _invalidatedCommandHandles[index] = 0;
                _invalidatedCommandHandleStates[index] = 2;
                return;
            }
        }
    }

    private static ulong MixCommandHandle(ulong value)
    {
        value ^= value >> 33;
        value *= 0xff51afd7ed558ccdUL;
        value ^= value >> 33;
        return value;
    }

    internal bool TryReleaseOwnedSecondaryCommandBuffer(
        CommandPool pool,
        CommandBuffer commandBuffer,
        out CommandPool poolReadyForRetirement)
    {
        poolReadyForRetirement = default;
        if (pool.Handle == 0 || commandBuffer.Handle == 0)
            return false;

        lock (OwnedSecondaryPoolsGate)
        {
            if (!OwnedSecondaryPools.TryGetValue(
                    pool.Handle,
                    out OwnedCommandChainSecondaryPool? ownedPool))
            {
                return false;
            }

            ownedPool.CommandBuffers.Remove(
                unchecked((ulong)commandBuffer.Handle));
            if (!ownedPool.PendingDestroy || ownedPool.CommandBuffers.Count != 0)
                return false;

            OwnedSecondaryPools.Remove(pool.Handle);
            poolReadyForRetirement = pool;
            return true;
        }
    }

    internal bool TryDeferSecondaryCommandBufferFree(
        uint imageIndex,
        CommandPool pool,
        CommandBuffer commandBuffer,
        ulong resourceGeneration)
    {
        if (commandBuffer.Handle == 0 || pool.Handle == 0 ||
            DeferredSecondaries is null ||
            imageIndex >= DeferredSecondaries.Length)
        {
            return false;
        }

        DeferredSecondaries[imageIndex] ??= [];
        DeferredSecondaries[imageIndex]!.Add(
            new DeferredSecondaryCommandBuffer(
                pool,
                commandBuffer,
                resourceGeneration));
        return true;
    }

    internal bool TryGetDiagnosticMetadata(
        uint imageIndex,
        CommandBuffer commandBuffer,
        out ulong plannerRevision,
        out ulong frameOpContextId,
        out ulong resourceGeneration,
        out ulong descriptorGeneration)
    {
        plannerRevision = 0;
        frameOpContextId = 0;
        resourceGeneration = 0;
        descriptorGeneration = 0;
        if (PrimaryOwners is null || imageIndex >= (uint)PrimaryOwners.Length)
            return false;

        PrimaryCommandArtifactOwner owner = PrimaryOwners[imageIndex];
        if (owner.PrimaryCommandBuffer.Handle != commandBuffer.Handle)
            return false;

        plannerRevision = owner.PlannerRevision == ulong.MaxValue
            ? 0
            : owner.PlannerRevision;
        frameOpContextId = owner.RecordedFrameOpContextId;
        resourceGeneration = owner.RecordedResourceGeneration;
        descriptorGeneration = owner.RecordedDescriptorGeneration;
        return true;
    }

    internal long SnapshotDirtyGeneration()
        => Volatile.Read(ref DirtyGeneration);

    internal bool HaveDirtiedSince(long generation)
        => Volatile.Read(ref DirtyGeneration) != generation;

    internal void MarkDirty(string? reason)
    {
        Volatile.Write(ref LastDirtyTimestamp, Stopwatch.GetTimestamp());
        Interlocked.Increment(ref DirtyGeneration);

        if (DirtyFlags is null)
            return;

        for (int i = 0; i < DirtyFlags.Length; i++)
            DirtyFlags[i] = true;

        if (PrimaryOwners is not null)
        {
            string resolvedReason = string.IsNullOrWhiteSpace(reason)
                ? "owner invalidated"
                : reason;
            for (int i = 0; i < PrimaryOwners.Length; i++)
            {
                PrimaryOwners[i].Dirty = true;
                PrimaryOwners[i].DirtyReason = resolvedReason;
            }
        }

        RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanCommandBuffersDirty(reason);
        TrackDirtyReason(reason, DirtyFlags.Length);
    }

    private void TrackDirtyReason(string? reason, int swapchainImageCount)
    {
        string key = string.IsNullOrWhiteSpace(reason) ? "<unknown>" : reason;
        string? summary = null;
        lock (DirtyReasonGate)
        {
            DirtyReasons.TryGetValue(key, out int count);
            DirtyReasons[key] = count + 1;

            long now = Stopwatch.GetTimestamp();
            if (LastDirtyReasonLogTimestamp == 0)
            {
                LastDirtyReasonLogTimestamp = now;
                return;
            }

            if (Stopwatch.GetElapsedTime(LastDirtyReasonLogTimestamp, now) < TimeSpan.FromSeconds(1))
                return;

            StringBuilder builder = new();
            foreach (KeyValuePair<string, int> pair in DirtyReasons.OrderByDescending(static pair => pair.Value))
            {
                if (builder.Length > 0)
                    builder.Append(", ");

                builder.Append(pair.Key).Append('=').Append(pair.Value);
            }

            summary = builder.ToString();
            DirtyReasons.Clear();
            LastDirtyReasonLogTimestamp = now;
        }

        Debug.Vulkan(
            "[Vulkan] Command buffers marked dirty over the last second. SwapchainImages={0} Reasons={1}",
            swapchainImageCount,
            summary);
    }
}
