namespace XREngine.Components;

/// <summary>Creates a persistent host worker group for one scheduler.</summary>
public interface IPhysicsChainCpuWorkerGroupFactory
{
    /// <summary>Creates idle workers. They use the callback only during a synchronous run.</summary>
    IPhysicsChainCpuWorkerGroup Create(int requestedWorkerCount, Action processRanges);
}
