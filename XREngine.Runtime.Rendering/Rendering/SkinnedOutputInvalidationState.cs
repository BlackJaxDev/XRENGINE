namespace XREngine.Rendering;

/// <summary>
/// Tracks transient compute-output invalidation independently of authored mesh
/// properties. These per-frame cache revisions are neither serialized properties
/// nor cancellable editor changes, and must not allocate property event arguments.
/// </summary>
internal sealed class SkinnedOutputInvalidationState
{
    private long _version;
    private long _cleanVersion = -1;

    internal ulong Version => unchecked((ulong)Volatile.Read(ref _version));
    internal bool IsDirty => Volatile.Read(ref _cleanVersion) != Volatile.Read(ref _version);

    internal void MarkDirty()
        => Interlocked.Increment(ref _version);

    /// <summary>Preserves invalidations that arrive after the dispatched input revision.</summary>
    internal void MarkClean(ulong dispatchedVersion)
        => Volatile.Write(ref _cleanVersion, unchecked((long)dispatchedVersion));
}
