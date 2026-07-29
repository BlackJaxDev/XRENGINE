namespace XREngine.Rendering;

internal struct AdvancedFrameUploadRetiredGenerationEntry
{
    public AdvancedFrameUploadStorageGeneration? Storage;
    public ulong RetireAfterCompletionValue;

    public readonly bool IsOccupied => Storage is not null;

    public void Clear()
    {
        Storage = null;
        RetireAfterCompletionValue = 0UL;
    }
}
