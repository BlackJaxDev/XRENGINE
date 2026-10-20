namespace XREngine.Scene.Transforms;

/// <summary>Runs persistent workers for disjoint transform ranges.</summary>
/// <remarks>The store serializes runs and disposal under its pass gate.</remarks>
internal interface ITransformPropagationWorkerPool : IDisposable
{
    /// <summary>Runs all ranges and returns the workers' allocated byte count.</summary>
    long Run(int count);
}
