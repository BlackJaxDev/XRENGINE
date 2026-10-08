namespace XREngine.Scene.Transforms;

/// <summary>Creates a persistent transform worker pool for one hierarchy store.</summary>
internal interface ITransformPropagationWorkerPoolFactory
{
    /// <summary>Creates workers that run the given range callback.</summary>
    ITransformPropagationWorkerPool Create(TransformHierarchyStore store, Action<int> execute);
}
