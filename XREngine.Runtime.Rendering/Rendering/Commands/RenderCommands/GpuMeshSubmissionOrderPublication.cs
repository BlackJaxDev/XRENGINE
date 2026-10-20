namespace XREngine.Rendering.Commands;

/// <summary>
/// Frozen source ordering for one pass of a verified full-resident collection.
/// Membership remains owned by the resident scene; this publication only supplies sort inputs.
/// </summary>
public sealed class GpuMeshSubmissionOrderPublication
{
    private const int MaximumSources = 1 << 20;
    private readonly Dictionary<IRenderCommandMesh, GpuMeshSubmissionOrderSource> _sources =
        new(ReferenceEqualityComparer.Instance);

    /// <summary>Zero preserves insertion, one sorts near to far, and two sorts far to near.</summary>
    public int SortPolicy { get; private set; }
    /// <summary>Whether the pass applies the shared authored transparent priority before distance.</summary>
    public bool UsePriority { get; private set; }

    /// <summary>Looks up one resident source without exposing a CPU-derived draw list.</summary>
    public bool TryGetSource(IRenderCommandMesh source, out GpuMeshSubmissionOrderSource entry)
        => _sources.TryGetValue(source, out entry);

    internal void Reset(int sortPolicy = 0, bool usePriority = false)
    {
        _sources.Clear();
        SortPolicy = sortPolicy;
        UsePriority = usePriority;
    }

    internal bool TryAdd(in GpuMeshSubmissionOrderSource entry)
        => _sources.Count < MaximumSources && _sources.TryAdd(entry.Source, entry);

    internal void CopyFrom(GpuMeshSubmissionOrderPublication source)
    {
        Reset(source.SortPolicy, source.UsePriority);
        _sources.EnsureCapacity(source._sources.Count);
        foreach (KeyValuePair<IRenderCommandMesh, GpuMeshSubmissionOrderSource> entry in source._sources)
            _sources.Add(entry.Key, entry.Value);
    }
}
