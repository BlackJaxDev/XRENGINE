namespace XREngine.Components;

/// <summary>Runs a fixed set of host workers alongside the calling thread.</summary>
/// <remarks>The owner must serialize runs and disposal.</remarks>
public interface IPhysicsChainCpuWorkerGroup : IDisposable
{
    /// <summary>Gets the number of persistent host workers.</summary>
    int FixedWorkerCount { get; }

    /// <summary>
    /// Wakes each worker, runs the caller, and waits for all workers to finish.
    /// Worker <c>i</c> passes <c>i</c> to the callback. The calling thread passes <see cref="FixedWorkerCount"/>.
    /// Each worker signals completion even when the callback throws.
    /// </summary>
    void SynchronousRun();
}
