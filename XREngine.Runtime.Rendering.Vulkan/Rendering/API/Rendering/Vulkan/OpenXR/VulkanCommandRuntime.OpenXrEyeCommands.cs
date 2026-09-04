using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>Lane-owned primary artifacts for OpenXR eye recording.</summary>
internal sealed partial class VulkanCommandRuntime
{
    internal bool IsOpenXrTraceEnabled => OpenXrVulkanTraceEnabled;

    internal bool EnsureOpenXrDescriptorFrameSlotFloor(int frameSlotCount)
        => EnsureDescriptorFrameSlotFrameCountFloor(frameSlotCount);

    internal Dictionary<CommandChainKey, CommandChain> GetOpenXrCommandChainCache(
        uint imageIndex)
        => GetCommandChainCache(imageIndex);

    internal void RegisterOpenXrCommandBufferImageIndex(
        CommandBuffer commandBuffer,
        uint imageIndex)
        => RegisterCommandBufferImageIndex(commandBuffer, imageIndex);

    internal void PublishOpenXrRecordedTextureUploads(
        List<VulkanImportedTexturePendingUpload> uploads,
        string uploadSource)
        => PublishRecordedTextureUploadsAfterCompletedSubmit(
            uploads,
            uploadSource);

    internal void PublishOpenXrRecordedTextureUploads(
        ReadOnlySpan<VulkanImportedTexturePendingUpload> uploads,
        string uploadSource)
    {
        for (int i = 0; i < uploads.Length; i++)
            ResourceRuntime.Uploads.PublishCompletedRecordedTextureUpload(
                ResourceRuntime,
                uploads[i],
                uploadSource);
    }

    internal void CancelOpenXrRecordedTextureUploads(
        List<VulkanImportedTexturePendingUpload> uploads,
        string reason)
        => CancelRecordedTextureUploads(uploads, reason);

    internal void CancelOpenXrRecordedTextureUploads(
        ReadOnlySpan<VulkanImportedTexturePendingUpload> uploads,
        string reason)
    {
        if (uploads.IsEmpty)
            return;

        _ = InvalidateCommandChainSecondaryCommandBuffersForDescriptorReferenceRelease();
        MarkCommandBuffersDirty(reason);
        for (int i = 0; i < uploads.Length; i++)
            CancelRecordedTextureUpload(uploads[i], reason);
    }

    internal PrimaryCommandArtifactOwner GetOrCreateOpenXrLanePrimaryCommandBufferOwner(
        ulong targetSlotKey,
        uint recordImageIndex,
        VulkanLaneCommandFamilyArena laneArena,
        bool priorUseCompletionProven,
        uint openXrViewIndex)
    {
        ArgumentNullException.ThrowIfNull(laneArena);
        using VulkanLaneCommandFamilyArena.RecordingLease arenaLease =
            VulkanLaneCommandFamilyArena.EnterRecording(laneArena);
        return GetOrCreateOpenXrPrimaryCommandBufferOwner(
            targetSlotKey,
            recordImageIndex,
            laneArena.RetainedPool,
            $"OpenXR eye primary command buffer owner eye={openXrViewIndex} lane={laneArena.LaneId} slot={laneArena.FrameSlot}",
            requirePriorUseCompletion: true,
            priorUseCompletionProven);
    }

    internal PrimaryCommandArtifactOwner GetOrCreateOpenXrPrimaryCommandBufferOwner(
        ulong targetSlotKey,
        uint recordImageIndex,
        CommandPool ownerPool,
        string allocationLabel,
        bool requirePriorUseCompletion = false,
        bool priorUseCompletionProven = false)
    {
        lock (CommandBuffers.OpenXrPrimaryOwnersGate)
        {
            if (CommandBuffers.OpenXrPrimaryOwners.TryGetValue(
                    targetSlotKey,
                    out PrimaryCommandArtifactOwner? owner))
            {
                if (owner.PrimaryCommandPool.Handle != ownerPool.Handle)
                {
                    throw new InvalidOperationException(
                        $"OpenXR primary cache key 0x{targetSlotKey:X16} belongs to command pool " +
                        $"0x{owner.PrimaryCommandPool.Handle:X}, not requested pool 0x{ownerPool.Handle:X}.");
                }
                if (requirePriorUseCompletion &&
                    !ResourceRuntime.CanResetCommandBuffer(
                        owner.PrimaryCommandBuffer))
                {
                    throw new InvalidOperationException(
                        $"OpenXR primary command buffer 0x{owner.PrimaryCommandBuffer.Handle:X} cannot be " +
                        $"reused before exact completion of its prior in-flight slot; " +
                        $"slotCompletionProven={priorUseCompletionProven}.");
                }

                RegisterCommandBufferImageIndex(
                    owner.PrimaryCommandBuffer,
                    recordImageIndex);
                return owner;
            }

            CommandBuffer primary = AllocateTrackedCommandBuffer(
                Api,
                DeviceContext,
                ResourceRuntime,
                ownerPool,
                CommandBufferLevel.Primary,
                allocationLabel);
            RegisterCommandBufferImageIndex(primary, recordImageIndex);
            owner = new PrimaryCommandArtifactOwner(
                primary,
                dynamicUiSecondaryCommandBuffer: default,
                ownerPool,
                dynamicUiSecondaryCommandPool: default,
                ownsPrimaryCommandBuffer: true,
                ownsDynamicUiSecondaryCommandBuffer: false);
            CommandBuffers.OpenXrPrimaryOwners.Add(targetSlotKey, owner);
            return owner;
        }
    }

    internal void DestroyOpenXrEyeCommandPools()
    {
        // Eye primaries now live in fixed retained render-lane arenas. The
        // primary-owner teardown frees their buffers; device teardown retires
        // the arenas after every owner has detached.
    }
}
