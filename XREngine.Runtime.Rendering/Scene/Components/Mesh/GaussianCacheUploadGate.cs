namespace XREngine.Components.Scene.Mesh;

/// <summary>
/// Tracks which cache owns the GPU buffers of <see cref="GaussianCacheComponent"/>, and whether the level table of that
/// cache is on the GPU. The compute passes and the draw run only when <see cref="CanDispatch"/> is true, so they never
/// read the level table, the records, or the frames of another cache.
/// </summary>
internal sealed class GaussianCacheUploadGate
{
    private object? _owner;
    private long _levelSequence;

    /// <summary>The cache (or its upload tracker) that owns the buffers, or null.</summary>
    public object? Owner => _owner;

    /// <summary>True when the buffers have an owner and the level table of that owner is on the GPU.</summary>
    public bool CanDispatch => _owner is not null && _levelSequence > 0;

    /// <summary>
    /// Gives the buffers to a new owner, or to no owner. The caller writes a level table with no ready level at the same
    /// time. The passes stay off until <see cref="AcceptLevelTable"/> returns true for the new owner.
    /// </summary>
    public void Begin(object? owner)
    {
        _owner = owner;
        _levelSequence = 0;
    }

    /// <summary>Returns true when an upload belongs to the owner of the buffers.</summary>
    public bool Accepts(object? owner)
        => owner is not null && ReferenceEquals(owner, _owner);

    /// <summary>
    /// Returns true when the caller must write the level table of an upload: the upload belongs to the owner, and it is
    /// newer than the table on the GPU. The sequence of the first upload must be 1 or more.
    /// </summary>
    public bool AcceptLevelTable(object? owner, long sequence)
    {
        if (!Accepts(owner) || sequence <= _levelSequence)
            return false;

        _levelSequence = sequence;
        return true;
    }
}
