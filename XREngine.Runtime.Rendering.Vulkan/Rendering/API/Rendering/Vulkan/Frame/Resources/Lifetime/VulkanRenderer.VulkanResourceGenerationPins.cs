namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Generation-local pins shared by descriptor contents, command recording,
/// the queue gateway, and completion-aware retirement. Keeping this state
/// independent of native Vulkan objects also makes the descriptor/recorded
/// -&gt; queued -&gt; in-flight contract directly regression-testable.
/// </summary>
internal struct VulkanResourceGenerationPins
{
    public int DescriptorReferenceCount { get; private set; }
    public int RecordedReferenceCount { get; private set; }
    public int QueuedReferenceCount { get; private set; }
    public ulong LastGraphicsSequence { get; private set; }
    public ulong LastTransferSequence { get; private set; }
    public ulong LastOtherSequence { get; private set; }

    public readonly bool HasDescriptorReferences => DescriptorReferenceCount > 0;
    public readonly bool HasRecordedReferences => RecordedReferenceCount > 0;
    public readonly bool HasQueuedReferences => QueuedReferenceCount > 0;

    public void AddDescriptorReference()
        => DescriptorReferenceCount++;

    public void ReleaseDescriptorReference()
    {
        if (DescriptorReferenceCount <= 0)
            throw new InvalidOperationException("Vulkan descriptor-generation pin underflow.");
        DescriptorReferenceCount--;
    }

    public void AddRecordedReference()
        => RecordedReferenceCount++;

    public void ReleaseRecordedReference()
    {
        if (RecordedReferenceCount <= 0)
            throw new InvalidOperationException("Vulkan recorded-generation pin underflow.");
        RecordedReferenceCount--;
    }

    public void AddQueuedReference()
        => QueuedReferenceCount++;

    public void ReleaseQueuedReference()
    {
        if (QueuedReferenceCount <= 0)
            throw new InvalidOperationException("Vulkan queued-generation pin underflow.");
        QueuedReferenceCount--;
    }

    public void MarkSubmitted(EVulkanLifetimeQueueDomain domain, ulong queueSequence)
    {
        switch (domain)
        {
            case EVulkanLifetimeQueueDomain.Graphics:
                LastGraphicsSequence = Math.Max(LastGraphicsSequence, queueSequence);
                break;
            case EVulkanLifetimeQueueDomain.Transfer:
                LastTransferSequence = Math.Max(LastTransferSequence, queueSequence);
                break;
            default:
                LastOtherSequence = Math.Max(LastOtherSequence, queueSequence);
                break;
        }
    }

    public void MergeSubmitted(in VulkanResourceGenerationPins other)
    {
        LastGraphicsSequence = Math.Max(LastGraphicsSequence, other.LastGraphicsSequence);
        LastTransferSequence = Math.Max(LastTransferSequence, other.LastTransferSequence);
        LastOtherSequence = Math.Max(LastOtherSequence, other.LastOtherSequence);
    }

    public readonly bool IsRetirementReady(
        ulong completedGraphicsSequence,
        ulong completedTransferSequence,
        ulong completedOtherSequence)
        => !HasDescriptorReferences &&
           !HasRecordedReferences &&
           !HasQueuedReferences &&
           LastGraphicsSequence <= completedGraphicsSequence &&
           LastTransferSequence <= completedTransferSequence &&
           LastOtherSequence <= completedOtherSequence;

    public void ResetCompletion()
    {
        if (HasQueuedReferences)
            throw new InvalidOperationException("Cannot reset a queued Vulkan resource generation.");
        LastGraphicsSequence = 0;
        LastTransferSequence = 0;
        LastOtherSequence = 0;
    }
}
