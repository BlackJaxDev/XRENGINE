namespace XREngine.Components;

/// <summary>Creates a persistent host worker group for one scheduler.</summary>
public interface IPhysicsChainCpuWorkerGroupFactory
{
    /// <summary>
    /// Creates idle workers. They use the callback only during a synchronous run.
    /// The callback argument is a stable worker index, so each worker can own its own counters.
    /// </summary>
    IPhysicsChainCpuWorkerGroup Create(int requestedWorkerCount, Action<int> processRanges);
}
