using XREngine.Scene.Transforms;

namespace XREngine.Execution;

/// <summary>Creates the built-in persistent transform workers.</summary>
internal sealed class ThreadedTransformPropagationWorkerPoolFactory : ITransformPropagationWorkerPoolFactory
{
    public ITransformPropagationWorkerPool Create(TransformHierarchyStore store, Action<int> execute)
        => new TransformPropagationWorkers(store, execute);
}
