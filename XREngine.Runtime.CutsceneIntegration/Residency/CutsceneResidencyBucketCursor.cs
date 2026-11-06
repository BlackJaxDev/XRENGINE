namespace XREngine.Cutscenes;

/// <summary>
/// Decodes the full entries of the buckets of a delta-compressed <see cref="CutsceneResidencyManifest"/>.
/// A move to a bucket starts at the key bucket at or before it, or continues from the current bucket when that is
/// on the way, and applies the deltas in order. The cursor allocates only in its constructor.
/// </summary>
/// <remarks>The order of <see cref="Members"/> is not the order of the asset indices. It changes when an entry is removed.</remarks>
public sealed class CutsceneResidencyBucketCursor
{
    private readonly ResidencyBucket[] _buckets;
    private readonly int[] _keyBefore;
    private readonly int[] _levels;
    private readonly float[] _coverages;
    private readonly int[] _members;
    private readonly int[] _positions;
    private int _count;
    private int _bucket = -1;

    /// <summary>Creates a cursor. The manifest must be valid. Do not change its arrays while the cursor exists.</summary>
    /// <param name="manifest">The manifest.</param>
    public CutsceneResidencyBucketCursor(CutsceneResidencyManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        _buckets = manifest.Buckets;
        int assetCount = manifest.Assets.Length;
        _levels = new int[assetCount];
        _coverages = new float[assetCount];
        _members = new int[assetCount];
        _positions = new int[assetCount];
        Array.Fill(_positions, -1);

        _keyBefore = new int[_buckets.Length];
        int key = 0;
        for (int b = 0; b < _buckets.Length; b++)
        {
            if (_buckets[b].IsKey)
                key = b;
            _keyBefore[b] = key;
        }
    }

    /// <summary>The bucket that the cursor holds, or -1 before the first move.</summary>
    public int Bucket => _bucket;

    /// <summary>The number of entries of the current bucket.</summary>
    public int Count => _count;

    /// <summary>The asset indices of the entries of the current bucket.</summary>
    public ReadOnlySpan<int> Members => new(_members, 0, _count);

    /// <summary>Gets the level of an asset in the current bucket. Use only an index from <see cref="Members"/>.</summary>
    /// <param name="assetIndex">The asset index.</param>
    public int GetLevel(int assetIndex) => _levels[assetIndex];

    /// <summary>Gets the coverage of an asset in the current bucket. Use only an index from <see cref="Members"/>.</summary>
    /// <param name="assetIndex">The asset index.</param>
    public float GetCoverage(int assetIndex) => _coverages[assetIndex];

    /// <summary>Gets entry <paramref name="index"/> of the current bucket.</summary>
    /// <param name="index">The entry index, from 0 to <see cref="Count"/> - 1.</param>
    public ResidencyEntry GetEntry(int index)
    {
        int asset = _members[index];
        return new ResidencyEntry(asset, _levels[asset], _coverages[asset]);
    }

    /// <summary>Decodes a bucket. A move to the current bucket does nothing.</summary>
    /// <param name="bucket">The bucket index.</param>
    public void MoveTo(int bucket)
    {
        if (bucket == _bucket)
            return;
        if ((uint)bucket >= (uint)_buckets.Length)
            throw new ArgumentOutOfRangeException(nameof(bucket));

        int key = _keyBefore[bucket];
        int from;
        if (_bucket >= key && _bucket < bucket)
            from = _bucket + 1;
        else
        {
            Clear();
            from = key;
        }

        for (int b = from; b <= bucket; b++)
            Apply(_buckets[b]);
        _bucket = bucket;
    }

    private void Apply(ResidencyBucket bucket)
    {
        if (bucket.IsKey)
            Clear();

        int[] removed = bucket.Removed;
        for (int i = 0; i < removed.Length; i++)
            Remove(removed[i]);

        ResidencyEntry[] entries = bucket.Entries;
        for (int i = 0; i < entries.Length; i++)
        {
            ResidencyEntry entry = entries[i];
            int asset = entry.AssetIndex;
            if (_positions[asset] < 0)
            {
                _positions[asset] = _count;
                _members[_count++] = asset;
            }
            _levels[asset] = entry.TargetLevel;
            _coverages[asset] = entry.ScreenCoverage;
        }
    }

    private void Remove(int asset)
    {
        int position = _positions[asset];
        if (position < 0)
            return;

        int last = _members[--_count];
        _members[position] = last;
        _positions[last] = position;
        _positions[asset] = -1;
    }

    private void Clear()
    {
        for (int i = 0; i < _count; i++)
            _positions[_members[i]] = -1;
        _count = 0;
    }
}
