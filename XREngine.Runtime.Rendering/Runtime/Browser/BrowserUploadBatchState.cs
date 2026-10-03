namespace XREngine.Rendering;

/// <summary>Ownership state for the reusable browser upload arenas.</summary>
internal enum BrowserUploadBatchState : byte
{
    Idle,
    Writing,
    Sealed,
    Consuming,
    Faulted,
    Disposed
}
