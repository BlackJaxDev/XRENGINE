namespace XREngine.Cutscenes;

/// <summary>
/// Builds a <see cref="CutsceneResidencyManifest"/> one bucket at a time. The manifest bake and the approximate
/// manifest use it. An asset that occurs more than one time in a bucket keeps its highest level and its highest coverage.
/// </summary>
public sealed class CutsceneResidencyManifestBuilder
{
    /// <summary>The default screen coverage at or above which an asset needs T2. Below it, T1 is enough.</summary>
    /// <remarks>At 1920 x 1080, one tenth of the screen is about 455 px square, close to the 512 px limit of T1.</remarks>
    public const float DefaultTargetCoverage = 0.1f;

    private readonly Dictionary<Guid, int> _assetIndices = [];
    private readonly List<Guid> _assets = [];
    private readonly List<EResidencyAssetKind> _kinds = [];
    private readonly List<long> _proxyBytes = [];
    private readonly List<ResidencyBucket> _buckets = [];
    private readonly Dictionary<int, ResidencyEntry> _bucketEntries = [];
    private bool _inBucket;
    private CutsceneTime _bucketStart;
    private CutsceneTime _bucketEnd;
    private bool _bucketStartsAtCut;

    /// <summary>The number of assets in the table.</summary>
    public int AssetCount => _assets.Count;

    /// <summary>Gets the identifier of an asset of the table.</summary>
    /// <param name="index">The asset index.</param>
    public Guid GetAssetId(int index) => _assets[index];

    /// <summary>Gets the kind of an asset of the table.</summary>
    /// <param name="index">The asset index.</param>
    public EResidencyAssetKind GetAssetKind(int index) => _kinds[index];

    /// <summary>Gets the largest T0 size that was reported for an asset of the table, in bytes.</summary>
    /// <param name="index">The asset index.</param>
    public long GetProxyBytes(int index) => _proxyBytes[index];

    /// <summary>
    /// Selects the level that a screen coverage needs: none (-1) for no coverage, T1 below
    /// <paramref name="targetCoverage"/>, and T2 at or above it.
    /// </summary>
    /// <param name="coverage">The screen coverage, from 0 to 1.</param>
    /// <param name="targetCoverage">The coverage at or above which T2 is needed.</param>
    public static int SelectLevel(float coverage, float targetCoverage)
    {
        if (!(coverage > 0.0f))
            return (int)EResidencyLevel.None;
        return coverage >= targetCoverage ? (int)EResidencyLevel.Target : (int)EResidencyLevel.Coarse;
    }

    /// <summary>
    /// Adds an asset to the table, or finds it. The first kind of an asset stays. The T0 size keeps the largest value.
    /// </summary>
    /// <param name="asset">The asset.</param>
    /// <returns>The asset index.</returns>
    public int AddAsset(in CutsceneResidencyAsset asset)
    {
        if (_assetIndices.TryGetValue(asset.AssetId, out int index))
        {
            if (asset.ProxyBytes > _proxyBytes[index])
                _proxyBytes[index] = asset.ProxyBytes;
            return index;
        }

        index = _assets.Count;
        _assetIndices.Add(asset.AssetId, index);
        _assets.Add(asset.AssetId);
        _kinds.Add(asset.Kind);
        _proxyBytes.Add(Math.Max(0L, asset.ProxyBytes));
        return index;
    }

    /// <summary>Starts a bucket. The bucket must start at or after the end of the last bucket.</summary>
    /// <param name="start">The first time of the range.</param>
    /// <param name="end">The end of the range. It must be after <paramref name="start"/>.</param>
    /// <param name="startsAtCut">True if a camera cut occurs at <paramref name="start"/>.</param>
    public void BeginBucket(CutsceneTime start, CutsceneTime end, bool startsAtCut)
    {
        if (_inBucket)
            throw new InvalidOperationException("End the current bucket before a new bucket starts.");
        if (end <= start)
            throw new ArgumentException("The end of a residency bucket must be after its start.", nameof(end));
        if (_buckets.Count > 0 && start < _buckets[^1].End)
            throw new ArgumentException("A residency bucket must not start before the end of the bucket before it.", nameof(start));

        _inBucket = true;
        _bucketStart = start;
        _bucketEnd = end;
        _bucketStartsAtCut = startsAtCut;
        _bucketEntries.Clear();
    }

    /// <summary>Adds an entry to the current bucket. A level below T0 adds nothing.</summary>
    /// <param name="assetIndex">The asset index from <see cref="AddAsset"/>.</param>
    /// <param name="level">The level that the bucket needs.</param>
    /// <param name="coverage">The screen coverage, from 0 to 1.</param>
    public void AddEntry(int assetIndex, int level, float coverage)
    {
        if (!_inBucket)
            throw new InvalidOperationException("Start a bucket before an entry is added.");
        if ((uint)assetIndex >= (uint)_assets.Count)
            throw new ArgumentOutOfRangeException(nameof(assetIndex));
        if (level < 0)
            return;

        float clamped = Math.Clamp(float.IsFinite(coverage) ? coverage : 0.0f, 0.0f, 1.0f);
        if (_bucketEntries.TryGetValue(assetIndex, out ResidencyEntry existing))
        {
            level = Math.Max(level, existing.TargetLevel);
            clamped = MathF.Max(clamped, existing.ScreenCoverage);
        }
        _bucketEntries[assetIndex] = new ResidencyEntry(assetIndex, level, clamped);
    }

    /// <summary>Ends the current bucket. The entries sort by asset index, so the result does not depend on the visit order.</summary>
    public void EndBucket()
    {
        if (!_inBucket)
            throw new InvalidOperationException("No bucket is open.");

        ResidencyEntry[] entries = new ResidencyEntry[_bucketEntries.Count];
        int i = 0;
        foreach (ResidencyEntry entry in _bucketEntries.Values)
            entries[i++] = entry;
        Array.Sort(entries, static (left, right) => left.AssetIndex.CompareTo(right.AssetIndex));

        _buckets.Add(new ResidencyBucket(_bucketStart, _bucketEnd, entries, _bucketStartsAtCut));
        _bucketEntries.Clear();
        _inBucket = false;
    }

    /// <summary>Creates the manifest. A bucket that is open ends first.</summary>
    public CutsceneResidencyManifest Build()
    {
        if (_inBucket)
            EndBucket();
        return new CutsceneResidencyManifest([.. _assets], [.. _kinds], [.. _buckets]);
    }
}
