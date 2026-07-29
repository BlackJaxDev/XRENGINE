namespace XREngine.Rendering;

internal sealed class AdvancedFrameUploadOverflowGeneration : IDisposable
{
    public AdvancedFrameUploadOverflowGeneration(
        AdvancedFrameUploadStorageGeneration storage)
        => Storage = storage;

    public AdvancedFrameUploadStorageGeneration Storage { get; }
    public EAdvancedFrameUploadOverflowState State { get; private set; }
    public ulong ActiveFrameOrdinal { get; private set; }
    public ulong RetireAfterCompletionValue { get; private set; }

    public bool TryActivate(
        ulong frameOrdinal,
        uint frameSlot)
    {
        if (State != EAdvancedFrameUploadOverflowState.Idle)
            return false;

        Storage.BeginFrame(frameSlot);
        State = EAdvancedFrameUploadOverflowState.Active;
        ActiveFrameOrdinal = frameOrdinal;
        RetireAfterCompletionValue = 0UL;
        return true;
    }

    public void ReleaseEmpty()
    {
        if (State != EAdvancedFrameUploadOverflowState.Active)
            return;

        Storage.EndFrame();
        State = EAdvancedFrameUploadOverflowState.Idle;
        ActiveFrameOrdinal = 0UL;
    }

    public void Complete(ulong completionValue)
    {
        if (State != EAdvancedFrameUploadOverflowState.Active)
            return;

        Storage.EndFrame();
        if (completionValue == 0UL)
        {
            State = EAdvancedFrameUploadOverflowState.Idle;
            ActiveFrameOrdinal = 0UL;
            return;
        }

        State = EAdvancedFrameUploadOverflowState.PendingRetirement;
        RetireAfterCompletionValue = completionValue;
    }

    public bool TryRetire(ulong completedValue)
    {
        if (State != EAdvancedFrameUploadOverflowState.PendingRetirement ||
            !AdvancedFrameSlotContract.CanReuse(
                RetireAfterCompletionValue,
                completedValue))
        {
            return false;
        }

        State = EAdvancedFrameUploadOverflowState.Idle;
        ActiveFrameOrdinal = 0UL;
        RetireAfterCompletionValue = 0UL;
        return true;
    }

    public void Dispose()
        => Storage.Dispose();
}
