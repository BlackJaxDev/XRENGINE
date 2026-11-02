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

    /// <summary>Gets the version plus <paramref name="externalAdvance"/> outputs of a bound external source.</summary>
    internal ulong GetVersion(ulong externalAdvance)
        => unchecked(Version + externalAdvance);

    /// <summary>Compares the clean version with the version plus <paramref name="externalAdvance"/>.</summary>
    internal bool IsDirtyWith(ulong externalAdvance)
        => unchecked((ulong)Volatile.Read(ref _cleanVersion)) != GetVersion(externalAdvance);

    internal void MarkDirty()
        => Interlocked.Increment(ref _version);

    /// <summary>Adds the outputs of an external source that the owner leaves, so the version never falls.</summary>
    internal void AddVersions(ulong count)
        => Interlocked.Add(ref _version, unchecked((long)count));

    /// <summary>Preserves invalidations that arrive after the dispatched input revision.</summary>
    internal void MarkClean(ulong dispatchedVersion)
        => Volatile.Write(ref _cleanVersion, unchecked((long)dispatchedVersion));
}
