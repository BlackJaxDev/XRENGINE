namespace XREngine.Execution;

/// <summary>
/// Observable lifecycle of a pooled render-work batch.
/// </summary>
public enum RenderWorkBatchStatus : byte
{
    Building = 0,
    Running = 1,
    Completed = 2,
    Canceled = 3,
    Faulted = 4,
}
