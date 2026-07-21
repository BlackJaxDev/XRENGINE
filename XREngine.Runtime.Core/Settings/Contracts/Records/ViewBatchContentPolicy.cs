namespace XREngine;

/// <summary>
/// Explicit point-of-view policy for work that cannot inherit union-draw
/// semantics without changing visible results.
/// </summary>
public readonly record struct ViewBatchContentPolicy(
    EMultiviewLodPolicy LodPolicy,
    ETransparentMultiviewPolicy TransparentPolicy)
{
    public static ViewBatchContentPolicy Exact
        => new(EMultiviewLodPolicy.PerViewExact, ETransparentMultiviewPolicy.PerViewSorted);

    public bool RequiresPerViewClassification
        => LodPolicy == EMultiviewLodPolicy.PerViewExact
            || TransparentPolicy is ETransparentMultiviewPolicy.PerViewSorted
                or ETransparentMultiviewPolicy.ForceSplit;

    public bool AllowsTransparentUnionDraw
        => TransparentPolicy == ETransparentMultiviewPolicy.ConservativeSharedOrder;
}
