using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Command artifacts for desktop primaries that a background render lane records.
/// A command pool must be externally synchronized while one of its command buffers
/// is recorded or reset, so these artifacts come from the recording lane's retained
/// pool and never from <see cref="VulkanCommandPoolAuthority.PrimaryGraphics"/>, which
/// the render thread uses at the same time.
/// </summary>
internal sealed partial class VulkanCommandRuntime
{
    /// <summary>
    /// First artifact index of desktop worker primaries. Each frame slot adds its
    /// index. The range does not overlap desktop swapchain images or OpenXR
    /// frame-data slots, so caches keyed by artifact index stay separate.
    /// </summary>
    internal const uint DesktopWorkerArtifactIndexBase = 0x4000_0000u;

    /// <summary>
    /// Returns the primary owner for one desktop frame slot on one lane, and creates
    /// it on first use. The owner holds a primary and a dynamic UI secondary, both
    /// allocated from the lane arena's retained pool. It lives in the lane primary
    /// owner table, so dirty marking, owner lookup and teardown include it.
    /// </summary>
    internal PrimaryCommandArtifactOwner GetOrCreateDesktopWorkerPrimaryCommandBufferOwner(
        ulong ownerKey,
        uint artifactIndex,
        VulkanLaneCommandFamilyArena laneArena)
    {
        ArgumentNullException.ThrowIfNull(laneArena);
        using VulkanLaneCommandFamilyArena.RecordingLease arenaLease =
            VulkanLaneCommandFamilyArena.EnterRecording(laneArena);
        CommandPool pool = laneArena.RetainedPool;
        lock (CommandBuffers.OpenXrPrimaryOwnersGate)
        {
            if (CommandBuffers.OpenXrPrimaryOwners.TryGetValue(ownerKey, out PrimaryCommandArtifactOwner? owner))
            {
                if (owner.PrimaryCommandPool.Handle != pool.Handle)
                {
                    throw new InvalidOperationException(
                        $"Desktop worker owner 0x{ownerKey:X16} belongs to command pool 0x{owner.PrimaryCommandPool.Handle:X}, " +
                        $"not lane {laneArena.LaneId} pool 0x{pool.Handle:X}.");
                }

                RegisterCommandBufferImageIndex(owner.PrimaryCommandBuffer, artifactIndex);
                RegisterCommandBufferImageIndex(owner.DynamicUiSecondaryCommandBuffer, artifactIndex);
                return owner;
            }

            string label = $"Desktop worker primary lane={laneArena.LaneId} slot={laneArena.FrameSlot}";
            CommandBuffer primary = AllocateTrackedCommandBuffer(
                Api,
                DeviceContext,
                ResourceRuntime,
                pool,
                CommandBufferLevel.Primary,
                label);
            CommandBuffer dynamicUiSecondary = AllocateTrackedCommandBuffer(
                Api,
                DeviceContext,
                ResourceRuntime,
                pool,
                CommandBufferLevel.Secondary,
                $"{label} dynamic UI");
            RegisterCommandBufferImageIndex(primary, artifactIndex);
            RegisterCommandBufferImageIndex(dynamicUiSecondary, artifactIndex);
            owner = new PrimaryCommandArtifactOwner(
                primary,
                dynamicUiSecondary,
                pool,
                pool,
                ownsPrimaryCommandBuffer: true,
                ownsDynamicUiSecondaryCommandBuffer: true)
            {
                Dirty = true,
                DirtyReason = "desktop worker owner created",
            };
            CommandBuffers.OpenXrPrimaryOwners.Add(ownerKey, owner);
            return owner;
        }
    }
}
