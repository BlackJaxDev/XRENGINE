using Silk.NET.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    internal const ulong MappedFrameArenaInitialCapacity = 32 * 1024 * 1024;
    private static bool? DynamicUniformBufferEnabledOverride
        => XREnvironment.GetBooleanOverride(
            XREngineEnvironmentVariables.VulkanDynamicUniformBuffer);

    private bool DynamicUniformBufferEnabled =>
        DynamicUniformBufferEnabledOverride ??
        RuntimeEngine.Rendering.Settings.VulkanRobustnessSettings.DynamicUniformBufferEnabled;

    /// <summary>
    /// The sole owner of mapped mesh frame-data storage. Mesh consumers use its typed slices
    /// directly rather than reaching through renderer forwarding helpers.
    /// </summary>
    internal VulkanMappedFrameArena? MappedFrameArena => ResourceRuntime.MappedFrameArena;

    internal bool IsMappedFrameArenaEnabled =>
        DynamicUniformBufferEnabled &&
        (ResourceRuntime.Descriptors.Heap.ActiveBackend != EVulkanDescriptorBackend.DescriptorHeap ||
         !ResourceRuntime.Descriptors.Heap.StorageReady) &&
        MappedFrameArena?.IsActive == true;

    private void InitializeMappedFrameArena()
    {
        if (!DynamicUniformBufferEnabled)
        {
            if (DynamicUniformBufferEnabledOverride is false)
            {
                Debug.Vulkan(
                    "[Vulkan] Mapped frame arena disabled by {0}=0 for this process.",
                    XREngineEnvironmentVariables.VulkanDynamicUniformBuffer);
            }
            return;
        }

        int desktopFrameSlots = OutputRuntime.Desktop.Images?.Length ?? 0;
        int frameSlots = Math.Max(desktopFrameSlots, ResourceRuntime.Descriptors.FrameSlotCount);
        if (frameSlots == 0)
            return;

        VulkanMappedFrameArenaBackend backend = new(
            Api!,
            _deviceContext.PhysicalDevice,
            _deviceContext.Device,
            _deviceContext,
            ResourceRuntime.Allocations.Buffers,
            _deviceContext.NonCoherentAtomSize);
        VulkanMappedFrameArena arena = new(
            backend,
            MappedFrameArenaInitialCapacity,
            checked((uint)Math.Max(_deviceContext.MinUniformBufferOffsetAlignment, 1UL)));
        try
        {
            arena.Initialize(frameSlots);
            ResourceRuntime.PublishMappedFrameArena(arena);
            Debug.Vulkan(
                "[Vulkan] Mapped frame arena initialized: {0} x {1} KB, dynamic-offset alignment={2}.",
                frameSlots,
                MappedFrameArenaInitialCapacity / 1024,
                arena.DynamicOffsetAlignment);
        }
        catch
        {
            arena.Destroy();
            CompleteMappedFrameArenaDeviceLossObservation();
            throw;
        }
    }

    private void EnsureMappedFrameArenaFrameSlotCapacity(int frameSlotCount)
    {
        if (!DynamicUniformBufferEnabled || MappedFrameArena is not { } arena)
            return;

        try
        {
            arena.EnsureFrameSlotCount(frameSlotCount);
        }
        catch
        {
            CompleteMappedFrameArenaDeviceLossObservation();
            throw;
        }
    }

    private void DestroyMappedFrameArena()
    {
        VulkanMappedFrameArena? arena = ResourceRuntime.DetachMappedFrameArena();
        arena?.Destroy();
        CompleteMappedFrameArenaDeviceLossObservation();
    }

    private void CompleteMappedFrameArenaDeviceLossObservation()
    {
        DeviceBootstrap.VulkanNativeDeviceFault? fault =
            _deviceContext.FirstNativeDeviceFault;
        if (fault is null)
            return;

        MarkDeviceLost(
            $"Mapped frame arena {fault.Operation} returned {fault.Result}",
            fault.Operation,
            fault.Result);
    }

    internal void RemoveMeshFrameDataManifestRenderer(VkMeshRenderer owner)
    {
        _frameWideMeshFrameDataManifest.RemoveRenderer(owner);
        VulkanFrameWideMeshFrameDataReservationManifest manifest = _frameWideMeshFrameDataManifest;
        RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanFrameWideMeshFrameDataManifestGauges(
            manifest.Generation,
            manifest.PublicationCount,
            manifest.LateRegistrationCount,
            manifest.PublishedRendererCount,
            manifest.PublishedFamilyCount,
            manifest.IsSealed);
    }

    /// <summary>
    /// Associates command-buffer lifetime ownership with the active arena generation. The
    /// mapped arena owns bytes; the renderer remains the authority for command submission.
    /// </summary>
    internal bool TryAcquireMappedFrameArenaRecordingLease(
        CommandBuffer commandBuffer,
        VkMeshRenderer owner,
        int drawSlot,
        ulong sealedGeneration,
        out string reason)
    {
        reason = string.Empty;
        ulong commandBufferHandle = unchecked((ulong)commandBuffer.Handle);
        ulong generation = MappedFrameArena?.Generation ?? 0UL;
        if (commandBufferHandle == 0 || generation == 0)
            return true;

        VulkanMeshFrameDataReservationManifest manifest =
            _commandBufferRecordingScratch.Value!.MeshFrameDataManifest;
        bool manifestOwnsDraw = manifest.ContainsSealedDraw(owner, drawSlot, generation);
        bool workerOwnsSealedDraw = sealedGeneration != 0 && sealedGeneration == generation;
        if (!manifestOwnsDraw && !workerOwnsSealedDraw)
        {
            reason = $"late or unsealed frame-data request for generation {generation}, slot {drawSlot}";
            RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanDynamicUniformExhaustion();
            return false;
        }

        VulkanResourceLifetimeTracker lifetimeTracker = ResourceRuntime.Lifetime.Tracker;
        lock (lifetimeTracker.SyncRoot)
        {
            if (!lifetimeTracker.CommandBufferLifetimes.TryGetValue(
                    commandBufferHandle,
                    out VulkanCommandBufferLifetimeRecord? lifetime))
            {
                lifetime = new VulkanCommandBufferLifetimeRecord();
                lifetimeTracker.CommandBufferLifetimes[commandBufferHandle] = lifetime;
            }

            if (lifetime.QueuedSubmissionCount != 0)
            {
                reason = $"command buffer 0x{commandBufferHandle:X} is already queued";
                return false;
            }

            if (lifetime.FrameDataLease.Generation != 0 &&
                lifetime.FrameDataLease.Generation != generation)
            {
                reason = $"command buffer 0x{commandBufferHandle:X} captured frame-data generation {lifetime.FrameDataLease.Generation} before current generation {generation}";
                return false;
            }

            if (lifetime.FrameDataLease.TryAcquireRecording(
                    generation,
                    commandBufferQueued: lifetime.QueuedSubmissionCount != 0))
            {
                return true;
            }

            reason = $"command buffer 0x{commandBufferHandle:X} could not acquire frame-data generation {generation}";
            return false;
        }
    }
}
