namespace XREngine.Execution;

/// <summary>
/// Renderer-neutral lane/frame-slot attachment table. Backends may register an
/// arena or command-pool owner without introducing a Runtime.Core dependency on
/// Vulkan, OpenGL, Direct3D, or OpenXR.
/// </summary>
public sealed class RenderLaneBackendAttachments
{
    private readonly object?[] _attachments;

    internal RenderLaneBackendAttachments(int logicalLaneCount, int frameSlotCount)
    {
        if (logicalLaneCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(logicalLaneCount));
        if (frameSlotCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(frameSlotCount));

        LogicalLaneCount = logicalLaneCount;
        FrameSlotCount = frameSlotCount;
        _attachments = new object?[checked(logicalLaneCount * frameSlotCount)];
    }

    public int LogicalLaneCount { get; }
    public int FrameSlotCount { get; }

    /// <summary>
    /// Atomically installs an opaque backend owner and returns the previous value.
    /// Registration is a cold lifecycle operation; reads are allocation-free.
    /// </summary>
    public object? Register(int laneId, int frameSlot, object? attachment)
        => Interlocked.Exchange(ref _attachments[GetIndex(laneId, frameSlot)], attachment);

    public object? Get(int laneId, int frameSlot)
        => Volatile.Read(ref _attachments[GetIndex(laneId, frameSlot)]);

    public bool TryGet<T>(int laneId, int frameSlot, out T? attachment)
        where T : class
    {
        attachment = Get(laneId, frameSlot) as T;
        return attachment is not null;
    }

    public void Clear()
    {
        for (int index = 0; index < _attachments.Length; index++)
            Interlocked.Exchange(ref _attachments[index], null);
    }

    internal bool HasAnyForLane(int laneId)
    {
        if ((uint)laneId >= (uint)LogicalLaneCount)
            throw new ArgumentOutOfRangeException(nameof(laneId));

        for (int frameSlot = 0; frameSlot < FrameSlotCount; frameSlot++)
            if (Get(laneId, frameSlot) is not null)
                return true;

        return false;
    }

    private int GetIndex(int laneId, int frameSlot)
    {
        if ((uint)laneId >= (uint)LogicalLaneCount)
            throw new ArgumentOutOfRangeException(nameof(laneId));
        if ((uint)frameSlot >= (uint)FrameSlotCount)
            throw new ArgumentOutOfRangeException(nameof(frameSlot));

        return checked((frameSlot * LogicalLaneCount) + laneId);
    }
}
