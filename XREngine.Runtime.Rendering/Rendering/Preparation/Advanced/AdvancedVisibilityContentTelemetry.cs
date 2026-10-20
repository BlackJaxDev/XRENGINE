namespace XREngine.Rendering;

/// <summary>
/// Cumulative counts of the visibility content comparison of the shared extractor.
/// Read two captures inside a window and compare the differences with the comparison count.
/// </summary>
/// <param name="Comparisons">Builds that compared their visibility columns.</param>
/// <param name="UnchangedComparisons">Builds whose columns were all equal to the retained columns.</param>
/// <param name="PayloadChanges">Builds whose visibility payloads changed.</param>
/// <param name="ProducerChanges">Builds whose geometry producers changed.</param>
/// <param name="RangeChanges">Builds whose indirect ranges changed.</param>
/// <param name="PayloadIndexChanges">Builds whose range payload order changed.</param>
/// <param name="SliceChanges">Builds whose deformed-arena slices changed, other than their frame slots.</param>
/// <param name="Generation">The current visibility content generation.</param>
public readonly record struct AdvancedVisibilityContentTelemetry(
    long Comparisons,
    long UnchangedComparisons,
    long PayloadChanges,
    long ProducerChanges,
    long RangeChanges,
    long PayloadIndexChanges,
    long SliceChanges,
    ulong Generation);
