namespace XREngine.Rendering;

/// <summary>
/// Completion-gated feedback ring. A backend writes one slot on the GPU and
/// the scheduler may inspect it only after the associated fence/timeline value
/// is complete; no call waits for or maps the producing frame synchronously.
/// </summary>
public sealed class AdvancedVisibilityFeedbackRing
{
    private readonly AdvancedAnimationVisibilityFeedback[][] _slots;
    private readonly int[] _counts;
    private readonly ulong[] _frameIds;
    private readonly ulong[] _completionValues;
    private readonly bool[] _sealed;

    public AdvancedVisibilityFeedbackRing(int slotCount, int recordCapacity)
    {
        if (slotCount < 2)
            throw new ArgumentOutOfRangeException(nameof(slotCount));
        if (recordCapacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(recordCapacity));

        _slots = new AdvancedAnimationVisibilityFeedback[slotCount][];
        for (int i = 0; i < slotCount; i++)
            _slots[i] = new AdvancedAnimationVisibilityFeedback[recordCapacity];
        _counts = new int[slotCount];
        _frameIds = new ulong[slotCount];
        _completionValues = new ulong[slotCount];
        _sealed = new bool[slotCount];
    }

    public int SlotCount => _slots.Length;
    public int RecordCapacity => _slots[0].Length;

    public Span<AdvancedAnimationVisibilityFeedback> GetGpuWritableMirror(
        ulong frameId)
    {
        int slot = ResolveSlot(frameId);
        _sealed[slot] = false;
        _counts[slot] = 0;
        return _slots[slot];
    }

    public void SealGpuWrite(
        ulong frameId,
        int recordCount,
        ulong completionValue)
    {
        if ((uint)recordCount > (uint)RecordCapacity)
            throw new ArgumentOutOfRangeException(nameof(recordCount));

        int slot = ResolveSlot(frameId);
        _counts[slot] = recordCount;
        _frameIds[slot] = frameId;
        _completionValues[slot] = completionValue;
        _sealed[slot] = true;
    }

    public bool TryGetLatestCompleted(
        ulong maximumFrameId,
        ulong completedValue,
        out ReadOnlySpan<AdvancedAnimationVisibilityFeedback> feedback,
        out ulong feedbackFrameId)
    {
        int selectedSlot = -1;
        ulong selectedFrame = 0UL;
        for (int slot = 0; slot < _slots.Length; slot++)
        {
            if (!_sealed[slot])
                continue;

            ulong frame = _frameIds[slot];
            if (frame > maximumFrameId ||
                _completionValues[slot] > completedValue ||
                (selectedSlot >= 0 && frame <= selectedFrame))
            {
                continue;
            }

            selectedSlot = slot;
            selectedFrame = frame;
        }

        if (selectedSlot < 0)
        {
            feedback = default;
            feedbackFrameId = 0UL;
            return false;
        }

        feedback = _slots[selectedSlot].AsSpan(0, _counts[selectedSlot]);
        feedbackFrameId = selectedFrame;
        return true;
    }

    private int ResolveSlot(ulong frameId)
        => checked((int)(frameId % (ulong)_slots.Length));
}
