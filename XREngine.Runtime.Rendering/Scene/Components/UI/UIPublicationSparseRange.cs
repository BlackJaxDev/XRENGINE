namespace XREngine.Components;

/// <summary>
/// One recaptured subtree in a sparse publication capture. Old values index the previous
/// publication. New values index the capture's accumulated subtree records.
/// </summary>
internal readonly record struct UIPublicationSparseRange(
    int Root,
    int End,
    int OldHitStart,
    int OldHitCount,
    int OldScrollStart,
    int OldScrollCount,
    int OldScopeStart,
    int OldScopeCount,
    int OldRenderStart,
    int OldRenderCount,
    int NewHitStart,
    int NewHitCount,
    int NewScrollStart,
    int NewScrollCount,
    int NewScopeStart,
    int NewScopeCount,
    int NewRenderStart,
    int NewRenderCount)
{
    /// <summary>Gets whether the subtree kept the number of each record kind.</summary>
    public bool KeepsCounts
        => OldHitCount == NewHitCount && OldScrollCount == NewScrollCount &&
            OldScopeCount == NewScopeCount && OldRenderCount == NewRenderCount;
}
