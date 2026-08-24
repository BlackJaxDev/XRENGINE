namespace XREngine.Execution;

/// <summary>
/// Stack-owned context for a stable logical render lane.
/// </summary>
public struct RenderWorkerContext
{
    private readonly RenderLaneBackendAttachments _attachments;

    internal RenderWorkerContext(
        int laneId,
        int managedThreadId,
        int frameSlot,
        long batchGeneration,
        int itemIndex,
        RenderLaneBackendAttachments attachments)
    {
        LaneId = laneId;
        ManagedThreadId = managedThreadId;
        FrameSlot = frameSlot;
        BatchGeneration = batchGeneration;
        ItemIndex = itemIndex;
        _attachments = attachments;
    }

    public int LaneId { get; }
    public int ManagedThreadId { get; }
    public int FrameSlot { get; }
    public long BatchGeneration { get; }
    public int ItemIndex { get; }
    public int BackendAttachmentIndex => checked((FrameSlot * _attachments.LogicalLaneCount) + LaneId);
    public object? BackendAttachment => _attachments.Get(LaneId, FrameSlot);

    public bool TryGetBackendAttachment<T>(out T? attachment)
        where T : class
        => _attachments.TryGet(LaneId, FrameSlot, out attachment);
}
