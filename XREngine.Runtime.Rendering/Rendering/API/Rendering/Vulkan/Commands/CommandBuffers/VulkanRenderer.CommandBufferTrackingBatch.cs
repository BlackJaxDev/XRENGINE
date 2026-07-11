using System.Collections.Concurrent;
using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    private readonly record struct VulkanImageAccessRangeDelta(
        ulong ImageHandle,
        ImageSubresourceRange Range,
        VulkanImageAccessState State);

    private sealed class VulkanCommandBufferTrackingBatch
    {
        public readonly HashSet<VulkanResourceLifetimeKey> Dependencies = new(64);
        public readonly Dictionary<ulong, ulong> ExpandedDescriptorGenerations = new(8);
        public readonly Dictionary<ulong, (ulong DescriptorGeneration, ulong LayoutVersion)> ValidatedDescriptorGenerations = new(8);
        public readonly List<VulkanImageAccessRangeDelta> ImageAccessDeltas = new(32);
        public ulong RecordingGeneration;
        public ulong LayoutVersion;
        public int DependencyBindCount;
        public int ImageAccessWriteCount;
        public int PublishedDependencyCount;
        public int PublishedImageDeltaCount;
        public int ReportedDependencyBindCount;
        public int ReportedImageAccessWriteCount;

        public void RecordDependency(VulkanResourceLifetimeKey key)
        {
            DependencyBindCount++;
            Dependencies.Add(key);
        }

        public bool MarkDescriptorExpanded(ulong descriptorSetHandle, ulong descriptorGeneration)
        {
            if (ExpandedDescriptorGenerations.TryGetValue(descriptorSetHandle, out ulong existing) &&
                existing == descriptorGeneration)
            {
                return false;
            }

            ExpandedDescriptorGenerations[descriptorSetHandle] = descriptorGeneration;
            return true;
        }

        public bool MarkDescriptorValidated(ulong descriptorSetHandle, ulong descriptorGeneration)
        {
            (ulong DescriptorGeneration, ulong LayoutVersion) key = (descriptorGeneration, LayoutVersion);
            if (ValidatedDescriptorGenerations.TryGetValue(descriptorSetHandle, out var existing) && existing == key)
                return false;

            ValidatedDescriptorGenerations[descriptorSetHandle] = key;
            return true;
        }

        public void RecordImageAccess(in VulkanImageAccessRangeDelta delta)
        {
            ImageAccessWriteCount++;
            LayoutVersion++;
            if (ImageAccessDeltas.Count > PublishedImageDeltaCount)
            {
                VulkanImageAccessRangeDelta previous = ImageAccessDeltas[^1];
                if (previous.ImageHandle == delta.ImageHandle &&
                    previous.State.Layout == delta.State.Layout &&
                    previous.State.StageMask == delta.State.StageMask &&
                    previous.State.AccessMask == delta.State.AccessMask &&
                    previous.State.QueueFamilyIndex == delta.State.QueueFamilyIndex &&
                    previous.State.ResourceGeneration == delta.State.ResourceGeneration &&
                    TryMergeRanges(previous.Range, delta.Range, out ImageSubresourceRange mergedRange))
                {
                    ImageAccessDeltas[^1] = delta with { Range = mergedRange };
                    return;
                }
            }

            ImageAccessDeltas.Add(delta);
        }

        private static bool SameRange(in ImageSubresourceRange left, in ImageSubresourceRange right)
            => left.AspectMask == right.AspectMask &&
               left.BaseMipLevel == right.BaseMipLevel &&
               left.LevelCount == right.LevelCount &&
               left.BaseArrayLayer == right.BaseArrayLayer &&
               left.LayerCount == right.LayerCount;

        private static bool TryMergeRanges(
            in ImageSubresourceRange left,
            in ImageSubresourceRange right,
            out ImageSubresourceRange merged)
        {
            if (SameRange(left, right))
            {
                merged = right;
                return true;
            }

            if (left.AspectMask == right.AspectMask &&
                left.BaseArrayLayer == right.BaseArrayLayer &&
                left.LayerCount == right.LayerCount &&
                left.BaseMipLevel + Math.Max(left.LevelCount, 1u) == right.BaseMipLevel)
            {
                merged = left with { LevelCount = Math.Max(left.LevelCount, 1u) + Math.Max(right.LevelCount, 1u) };
                return true;
            }

            if (left.AspectMask == right.AspectMask &&
                left.BaseMipLevel == right.BaseMipLevel &&
                left.LevelCount == right.LevelCount &&
                left.BaseArrayLayer + Math.Max(left.LayerCount, 1u) == right.BaseArrayLayer)
            {
                merged = left with { LayerCount = Math.Max(left.LayerCount, 1u) + Math.Max(right.LayerCount, 1u) };
                return true;
            }

            merged = default;
            return false;
        }
    }

    private readonly ConcurrentDictionary<ulong, VulkanCommandBufferTrackingBatch> _commandBufferTrackingBatches = new();

    private void BeginCommandBufferTrackingBatch(CommandBuffer commandBuffer)
    {
        ulong handle = unchecked((ulong)commandBuffer.Handle);
        if (handle == 0)
            return;

        _commandBufferTrackingBatches[handle] = new VulkanCommandBufferTrackingBatch
        {
            RecordingGeneration = ResolveCommandBufferRecordingGeneration(commandBuffer),
        };
    }

    private void RemoveCommandBufferTrackingBatch(CommandBuffer commandBuffer)
    {
        ulong handle = unchecked((ulong)commandBuffer.Handle);
        if (handle != 0)
            _commandBufferTrackingBatches.TryRemove(handle, out _);
    }

    private bool TryRecordCommandBufferDependency(CommandBuffer commandBuffer, ObjectType type, ulong handle)
    {
        ulong commandBufferHandle = unchecked((ulong)commandBuffer.Handle);
        if (commandBufferHandle == 0 || handle == 0 ||
            !_commandBufferTrackingBatches.TryGetValue(commandBufferHandle, out VulkanCommandBufferTrackingBatch? batch))
        {
            return false;
        }

        batch.RecordDependency(ResourceKey(type, handle));
        return true;
    }

    private bool TryRecordImageAccessDelta(
        CommandBuffer commandBuffer,
        Image image,
        ImageSubresourceRange range,
        ImageLayout layout,
        PipelineStageFlags stageMask,
        AccessFlags accessMask,
        uint queueFamilyIndex)
    {
        ulong commandBufferHandle = unchecked((ulong)commandBuffer.Handle);
        if (commandBufferHandle == 0 || image.Handle == 0 ||
            !_commandBufferTrackingBatches.TryGetValue(commandBufferHandle, out VulkanCommandBufferTrackingBatch? batch))
        {
            return false;
        }

        ImageAspectFlags primaryAspect = (range.AspectMask & ImageAspectFlags.ColorBit) != 0
            ? ImageAspectFlags.ColorBit
            : (range.AspectMask & ImageAspectFlags.DepthBit) != 0
                ? ImageAspectFlags.DepthBit
                : ImageAspectFlags.StencilBit;
        ulong serial = unchecked((ulong)Interlocked.Increment(ref _vulkanImageLayoutTransitionSerial));
        VulkanImageAccessState resolved = ResolveVulkanImageAccessState(
            layout,
            primaryAspect,
            queueFamilyIndex,
            serial,
            GetCurrentVulkanResourceGeneration(ObjectType.Image, image.Handle));
        resolved = resolved with
        {
            StageMask = stageMask == 0 ? resolved.StageMask : NormalizePipelineStages2(stageMask),
            AccessMask = NormalizeAccessFlags2(accessMask),
        };
        batch.RecordImageAccess(new VulkanImageAccessRangeDelta(image.Handle, range, resolved));
        return true;
    }

    private bool TryGetPendingImageAccessState(
        CommandBuffer commandBuffer,
        Image image,
        ImageSubresourceRange range,
        out VulkanImageAccessState state)
    {
        state = VulkanImageAccessState.Undefined;
        ulong commandBufferHandle = unchecked((ulong)commandBuffer.Handle);
        if (commandBufferHandle == 0 || image.Handle == 0 ||
            !_commandBufferTrackingBatches.TryGetValue(commandBufferHandle, out VulkanCommandBufferTrackingBatch? batch))
        {
            return false;
        }

        for (int i = batch.ImageAccessDeltas.Count - 1; i >= 0; i--)
        {
            VulkanImageAccessRangeDelta delta = batch.ImageAccessDeltas[i];
            if (delta.ImageHandle != image.Handle || !Contains(delta.Range, range))
                continue;

            state = delta.State;
            return state.Layout != ImageLayout.Undefined;
        }

        return false;
    }

    private static bool Contains(in ImageSubresourceRange outer, in ImageSubresourceRange inner)
    {
        uint outerLevels = Math.Max(outer.LevelCount, 1u);
        uint outerLayers = Math.Max(outer.LayerCount, 1u);
        uint innerLevels = Math.Max(inner.LevelCount, 1u);
        uint innerLayers = Math.Max(inner.LayerCount, 1u);
        return (outer.AspectMask & inner.AspectMask) == inner.AspectMask &&
            inner.BaseMipLevel >= outer.BaseMipLevel &&
            inner.BaseMipLevel + innerLevels <= outer.BaseMipLevel + outerLevels &&
            inner.BaseArrayLayer >= outer.BaseArrayLayer &&
            inner.BaseArrayLayer + innerLayers <= outer.BaseArrayLayer + outerLayers;
    }

    private void FlushCommandBufferTrackingBatch(CommandBuffer commandBuffer)
    {
        if (!TryFlushCommandBufferTrackingBatch(commandBuffer, out string failureReason))
            throw new InvalidOperationException(failureReason);
    }

    private bool TryFlushCommandBufferTrackingBatch(CommandBuffer commandBuffer, out string failureReason)
    {
        failureReason = string.Empty;
        ulong handle = unchecked((ulong)commandBuffer.Handle);
        if (handle == 0 || !_commandBufferTrackingBatches.TryGetValue(handle, out VulkanCommandBufferTrackingBatch? batch))
            return true;

        if (batch.PublishedDependencyCount == batch.Dependencies.Count &&
            batch.PublishedImageDeltaCount == batch.ImageAccessDeltas.Count)
        {
            return true;
        }

        int newUniqueDependencies = batch.Dependencies.Count - batch.PublishedDependencyCount;
        int newCompactImageRanges = batch.ImageAccessDeltas.Count - batch.PublishedImageDeltaCount;

        bool lifetimeLockContended = !Monitor.TryEnter(_vulkanResourceLifetimeLock);
        if (lifetimeLockContended)
            Monitor.Enter(_vulkanResourceLifetimeLock);
        try
        {
            if (!_vulkanCommandBufferLifetimes.TryGetValue(handle, out VulkanCommandBufferLifetimeRecord? lifetime))
            {
                lifetime = new VulkanCommandBufferLifetimeRecord();
                _vulkanCommandBufferLifetimes[handle] = lifetime;
            }

            foreach (VulkanResourceLifetimeKey key in batch.Dependencies)
            {
                if (!TryTrackVulkanCommandBufferResource_NoLock(
                    handle,
                    key,
                    "CommandBuffer.LocalBatch",
                    out failureReason))
                {
                    return false;
                }
            }

            lifetime.RefreshTouchedDependencies();
        }
        finally
        {
            Monitor.Exit(_vulkanResourceLifetimeLock);
        }

        bool layoutLockContended = FlushCommandBufferImageAccessBatch(commandBuffer, batch);
        RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanTrackingBatch(
            dependencyBinds: batch.DependencyBindCount - batch.ReportedDependencyBindCount,
            uniqueDependencies: newUniqueDependencies,
            imageAccessWrites: batch.ImageAccessWriteCount - batch.ReportedImageAccessWriteCount,
            compactImageRanges: newCompactImageRanges);
        RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanTrackingContention(
            lifetimeLockContended ? 1 : 0,
            layoutLockContended ? 1 : 0);
        batch.PublishedDependencyCount = batch.Dependencies.Count;
        batch.ReportedDependencyBindCount = batch.DependencyBindCount;
        batch.ReportedImageAccessWriteCount = batch.ImageAccessWriteCount;
        return true;
    }
}
