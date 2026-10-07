namespace XREngine.Components;

/// <summary>Runs a fixed set of host workers alongside the calling thread.</summary>
/// <remarks>The owner must serialize runs and disposal.</remarks>
public interface IPhysicsChainCpuWorkerGroup : IDisposable
{
    /// <summary>Gets the number of persistent host workers.</summary>
    int FixedWorkerCount { get; }

    /// <summary>Wakes each worker, runs the caller, and waits for all workers to finish.</summary>
    void SynchronousRun();
}
