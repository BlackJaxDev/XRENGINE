namespace XREngine.Components.Physics;

/// <summary>
/// Stores runtime and asset convex-hull inputs in preferred processing order.
/// </summary>
internal readonly record struct ConvexHullInputCollection(
    ConvexHullInputBatch Runtime,
    ConvexHullInputBatch Asset)
{
    public IEnumerable<ConvexHullInputBatch> EnumeratePreferredBatches()
    {
        if (Runtime.InputCount > 0)
            yield return Runtime;

        if (Asset.InputCount > 0)
            yield return Asset;
    }
}
