using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>Command-owned pools and primary artifacts for OpenXR eye recording.</summary>
internal sealed partial class VulkanCommandRuntime
{
    private readonly object _openXrEyeCommandPoolsGate = new();
    private readonly CommandPool[] _openXrEyeCommandPools = new CommandPool[2];

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

    internal void CancelOpenXrRecordedTextureUploads(
        List<VulkanImportedTexturePendingUpload> uploads,
        string reason)
        => CancelRecordedTextureUploads(uploads, reason);

    internal PrimaryCommandArtifactOwner GetOrCreateOpenXrPrimaryCommandBufferOwner(
        ulong targetSlotKey,
        uint recordImageIndex,
        in OpenXrEyeRenderTargetContext targetContext)
    {
        CommandPool pool = GetOrCreateOpenXrEyeCommandPool(
            targetContext.OpenXrViewIndex);
        return GetOrCreateOpenXrPrimaryCommandBufferOwner(
            targetSlotKey,
            recordImageIndex,
            pool,
            $"OpenXR eye primary command buffer owner eye={targetContext.OpenXrViewIndex}");
    }

    internal PrimaryCommandArtifactOwner GetOrCreateOpenXrPrimaryCommandBufferOwner(
        ulong targetSlotKey,
        uint recordImageIndex,
        CommandPool ownerPool,
        string allocationLabel)
    {
        lock (CommandBuffers.OpenXrPrimaryOwnersGate)
        {
            if (CommandBuffers.OpenXrPrimaryOwners.TryGetValue(
                    targetSlotKey,
                    out PrimaryCommandArtifactOwner? owner))
            {
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

    private unsafe CommandPool GetOrCreateOpenXrEyeCommandPool(uint openXrViewIndex)
    {
        int poolIndex = (int)Math.Min(openXrViewIndex, 1U);
        lock (_openXrEyeCommandPoolsGate)
        {
            CommandPool existing = _openXrEyeCommandPools[poolIndex];
            if (existing.Handle != 0)
                return existing;

            uint graphicsFamily = DeviceContext.QueueFamilies.GraphicsFamilyIndex
                ?? throw new InvalidOperationException(
                    "Graphics queue family is not available.");
            CommandPoolCreateInfo createInfo = new()
            {
                SType = StructureType.CommandPoolCreateInfo,
                QueueFamilyIndex = graphicsFamily,
                Flags = CommandPoolCreateFlags.ResetCommandBufferBit |
                    CommandPoolCreateFlags.TransientBit,
            };
            if (Api.CreateCommandPool(
                    DeviceContext.Device,
                    ref createInfo,
                    null,
                    out CommandPool created) != Result.Success)
            {
                throw new InvalidOperationException(
                    $"Failed to create OpenXR eye command pool {poolIndex}.");
            }

            ResourceRuntime.Lifetime.Tracker.RegisterResource(
                new VulkanResourceLifetimeKey(
                    ObjectType.CommandPool,
                    created.Handle),
                $"OpenXR eye primary command pool[{poolIndex}]",
                externallyOwned: false);
            _openXrEyeCommandPools[poolIndex] = created;
            return created;
        }
    }

    internal unsafe void DestroyOpenXrEyeCommandPools()
    {
        List<CommandPool> retiring = [];
        lock (_openXrEyeCommandPoolsGate)
            lock (CommandBuffers.SubmissionStateGate)
            {
                for (int index = 0; index < _openXrEyeCommandPools.Length; index++)
                {
                    CommandPool pool = _openXrEyeCommandPools[index];
                    if (pool.Handle == 0)
                        continue;

                    RemoveOpenXrPrimaryOwnersForPool(pool);
                    retiring.Add(pool);
                    _openXrEyeCommandPools[index] = default;
                }
            }

        for (int index = 0; index < retiring.Count; index++)
        {
            QueueCommandPoolRetirementTracked(
                retiring[index],
                ResourceRuntime.FramebufferRetirementFrameSlot);
        }
    }

    private void RemoveOpenXrPrimaryOwnersForPool(CommandPool pool)
    {
        lock (CommandBuffers.OpenXrPrimaryOwnersGate)
        {
            List<ulong>? removalKeys = null;
            foreach (KeyValuePair<ulong, PrimaryCommandArtifactOwner> entry in
                     CommandBuffers.OpenXrPrimaryOwners)
            {
                if (entry.Value.PrimaryCommandPool.Handle != pool.Handle)
                    continue;

                removalKeys ??= [];
                removalKeys.Add(entry.Key);
            }

            if (removalKeys is null)
                return;
            for (int index = 0; index < removalKeys.Count; index++)
                CommandBuffers.OpenXrPrimaryOwners.Remove(removalKeys[index]);
        }
    }
}
