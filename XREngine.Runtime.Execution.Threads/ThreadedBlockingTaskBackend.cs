namespace XREngine.Execution;

/// <summary>Owns blocking task completion for native hosts.</summary>
internal sealed class ThreadedBlockingTaskBackend : IRuntimeBlockingTaskBackend
{
    public T Run<T>(Func<Task<T>> operation) => operation().GetAwaiter().GetResult();
}
