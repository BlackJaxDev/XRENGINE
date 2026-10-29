namespace XREngine.Execution;

/// <summary>Completes an admitted asynchronous operation on a host that permits blocking.</summary>
public interface IRuntimeBlockingTaskBackend
{
    T Run<T>(Func<Task<T>> operation);
}
