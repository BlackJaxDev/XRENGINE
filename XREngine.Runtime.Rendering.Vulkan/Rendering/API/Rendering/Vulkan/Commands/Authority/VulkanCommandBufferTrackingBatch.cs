using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>Owns mutable resource and image-access tracking for one command-buffer recording.</summary>
internal sealed class VulkanCommandBufferTrackingBatch
{
    public readonly HashSet<VulkanResourceLifetimeKey> Dependencies = new(64);
    public readonly Dictionary<ulong, ulong> ExpandedDescriptorGenerations = new(8);
    public readonly Dictionary<ulong, (ulong DescriptorGeneration, ulong LayoutVersion)> ValidatedDescriptorGenerations = new(8);
    public readonly List<VulkanImageAccessRangeDelta> ImageAccessDeltas = new(32);
    public readonly List<VulkanQueueOwnershipTransferRequirement> QueueOwnershipTransfers = new(4);
    public readonly VulkanCommandBufferImageAccessIndex LatestImageAccessStates = new(32);
    public ulong RecordingGeneration;
    // The lifetime reset epoch and global command-recording sequence are
    // independent counters. Capture the former at Begin instead of comparing them.
    public ulong LifetimeRecordingGeneration;
    public ulong LayoutVersion;
    public int DependencyBindCount;
    public int ImageAccessWriteCount;
    public int PublishedImageDeltaCount;
    public int PublishedQueueOwnershipTransferCount;
    public int ReportedDependencyBindCount;
    public int ReportedImageAccessWriteCount;
    public int QueuedSubmissionCount;
    public bool IsRecording;

    public void Reset(ulong recordingGeneration)
    {
        ClearCompletedRecording();
        RecordingGeneration = recordingGeneration;
        IsRecording = true;
    }

    /// <summary>
    /// Clears mutable recording state after a successful native reset while
    /// retaining allocated capacity for the next recording on this command
    /// buffer. Callers must hold the tracker and batch locks.
    /// </summary>
    public void ClearCompletedRecording()
    {
        Dependencies.Clear();
        ExpandedDescriptorGenerations.Clear();
        ValidatedDescriptorGenerations.Clear();
        ImageAccessDeltas.Clear();
        QueueOwnershipTransfers.Clear();
        LatestImageAccessStates.Clear();
        RecordingGeneration = 0;
        LifetimeRecordingGeneration = 0;
        LayoutVersion = 0;
        DependencyBindCount = 0;
        ImageAccessWriteCount = 0;
        PublishedImageDeltaCount = 0;
        PublishedQueueOwnershipTransferCount = 0;
        ReportedDependencyBindCount = 0;
        ReportedImageAccessWriteCount = 0;
        QueuedSubmissionCount = 0;
        IsRecording = false;
    }

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
        LatestImageAccessStates.Record(delta.ImageHandle, delta.Range, delta.State);
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

    /// <summary>
    /// Advances the command-local lookup index after an executed secondary has
    /// already merged its immutable journal directly into the primary journal.
    /// No delta is appended because the merged journal is itself the submission
    /// publication source.
    /// </summary>
    public void RecordExecutedSecondaryImageAccess(in VulkanImageAccessRangeDelta delta)
    {
        ImageAccessWriteCount++;
        LayoutVersion++;
        LatestImageAccessStates.Record(delta.ImageHandle, delta.Range, delta.State);
    }

    public void RecordQueueOwnershipTransfer(in VulkanQueueOwnershipTransferRequirement requirement)
    {
        LayoutVersion++;
        QueueOwnershipTransfers.Add(requirement);
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
