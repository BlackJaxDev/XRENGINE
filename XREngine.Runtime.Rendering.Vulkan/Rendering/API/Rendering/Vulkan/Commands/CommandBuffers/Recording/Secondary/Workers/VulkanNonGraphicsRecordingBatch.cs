namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Reusable frozen queue for independently safe compute, fixed-barrier,
/// transfer, and ordered query-copy packet recording.
/// </summary>
internal sealed class VulkanNonGraphicsRecordingBatch
{
    internal VulkanNonGraphicsRecordingEntry[] Entries =
        new VulkanNonGraphicsRecordingEntry[8];
    internal FrameOperationSequence Operations;
    internal VulkanQuerySecondaryInheritanceContract QueryInheritance;
    internal uint ImageIndex;
    internal int EntryCount;
    internal uint ActiveWorkerMask;
    internal int CancelRequested;
    internal Exception? Error;
    internal bool Abandoned;

    internal void Reset(
        FrameOperationSequence operations,
        uint imageIndex,
        VulkanQuerySecondaryInheritanceContract queryInheritance,
        int entryCount)
    {
        if (Entries.Length < entryCount)
            Array.Resize(ref Entries, Math.Max(entryCount, Entries.Length * 2));
        Array.Clear(Entries, 0, EntryCount);
        Operations = operations;
        ImageIndex = imageIndex;
        QueryInheritance = queryInheritance;
        EntryCount = entryCount;
        ActiveWorkerMask = 0u;
        CancelRequested = 0;
        Error = null;
        Abandoned = false;
    }

    internal void ClearReferences()
    {
        Array.Clear(Entries, 0, EntryCount);
        Operations = default;
        EntryCount = 0;
        ActiveWorkerMask = 0u;
        CancelRequested = 0;
        Error = null;
        Abandoned = false;
    }
}
