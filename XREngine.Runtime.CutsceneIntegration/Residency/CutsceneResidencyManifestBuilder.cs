namespace XREngine.Cutscenes;

/// <summary>
/// Builds a <see cref="CutsceneResidencyManifest"/> one bucket at a time. The manifest bake, the approximate manifest,
/// and <see cref="CutsceneResidencyManifest.SelectKind"/> use it. An asset that occurs more than one time in a bucket
/// keeps its highest level and its highest coverage.
/// </summary>
/// <remarks>
/// <para>
/// The builder rounds each coverage to a step of <see cref="CoverageStep"/>, and a coverage above zero stays at one step
/// or more. Coverage only sorts the requests, so the step changes no level. It lets equal samples compare equal.
/// </para>
/// <para>
/// With <see cref="Compress"/> (the default), <see cref="Build"/> merges adjacent buckets with equal entries, unless the
/// second bucket starts at a camera cut. Then it writes a key bucket for the first bucket, for each bucket at a cut, for
/// the first bucket at or after each <see cref="KeyInterval"/> since the last key, and for a bucket whose delta is not
/// smaller than its full list. Each other bucket is a delta against the bucket before it.
/// </para>
/// </remarks>
public sealed class CutsceneResidencyManifestBuilder
{
    /// <summary>The default screen coverage at or above which an asset needs T2. Below it, T1 is enough.</summary>
    /// <remarks>At 1920 x 1080, one tenth of the screen is about 455 px square, close to the 512 px limit of T1.</remarks>
    public const float DefaultTargetCoverage = 0.1f;

    /// <summary>The step to which the builder rounds each coverage: 1/128.</summary>
    public const float CoverageStep = 1.0f / 128.0f;

    /// <summary>The default longest time between two key buckets: 4 seconds. A seek decodes at most the deltas of this time.</summary>
    public static readonly CutsceneTime DefaultKeyInterval = CutsceneTime.FromSeconds(4.0);

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

    /// <summary>True to merge equal buckets and write delta buckets. False writes one key bucket for each bucket. The default is true.</summary>
    public bool Compress { get; set; } = true;

    /// <summary>The longest time between two key buckets when <see cref="Compress"/> is true. It must be greater than zero.</summary>
    public CutsceneTime KeyInterval { get; set; } = DefaultKeyInterval;

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

    /// <summary>Rounds a coverage to <see cref="CoverageStep"/>. A coverage above zero gives one step or more.</summary>
    /// <param name="coverage">The coverage.</param>
    public static float QuantizeCoverage(float coverage)
    {
        if (!(coverage > 0.0f))
            return 0.0f;

        float steps = MathF.Round(MathF.Min(coverage, 1.0f) / CoverageStep);
        return MathF.Max(1.0f, steps) * CoverageStep;
    }

    /// <summary>
    /// Adds an asset to the table, or finds it. The first kind of an asset stays. The T0 size keeps the largest value.
    /// </summary>
    /// <param name="asset">The asset.</param>
    /// <returns>The asset index.</returns>
    public int AddAsset(in CutsceneResidencyAsset asset)
        => AddAsset(asset.AssetId, asset.Kind, asset.ProxyBytes);

    /// <summary>Adds an asset to the table, or finds it. The first kind of an asset stays. The T0 size keeps the largest value.</summary>
    /// <param name="assetId">The asset identifier.</param>
    /// <param name="kind">The asset kind.</param>
    /// <param name="proxyBytes">The estimated T0 size, in bytes.</param>
    /// <returns>The asset index.</returns>
    public int AddAsset(Guid assetId, EResidencyAssetKind kind, long proxyBytes)
    {
        if (_assetIndices.TryGetValue(assetId, out int index))
        {
            if (proxyBytes > _proxyBytes[index])
                _proxyBytes[index] = proxyBytes;
            return index;
        }

        index = _assets.Count;
        _assetIndices.Add(assetId, index);
        _assets.Add(assetId);
        _kinds.Add(kind);
        _proxyBytes.Add(Math.Max(0L, proxyBytes));
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
    /// <param name="assetIndex">The asset index from <see cref="AddAsset(in CutsceneResidencyAsset)"/>.</param>
    /// <param name="level">The level that the bucket needs.</param>
    /// <param name="coverage">The screen coverage, from 0 to 1. The builder rounds it to <see cref="CoverageStep"/>.</param>
    public void AddEntry(int assetIndex, int level, float coverage)
    {
        if (!_inBucket)
            throw new InvalidOperationException("Start a bucket before an entry is added.");
        if ((uint)assetIndex >= (uint)_assets.Count)
            throw new ArgumentOutOfRangeException(nameof(assetIndex));
        if (level < 0)
            return;

        float rounded = QuantizeCoverage(float.IsFinite(coverage) ? coverage : 0.0f);
        if (_bucketEntries.TryGetValue(assetIndex, out ResidencyEntry existing))
        {
            level = Math.Max(level, existing.TargetLevel);
            rounded = MathF.Max(rounded, existing.ScreenCoverage);
        }
        _bucketEntries[assetIndex] = new ResidencyEntry(assetIndex, level, rounded);
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

        ResidencyBucket[] buckets = Compress ? Encode(Merge(_buckets), KeyInterval) : [.. _buckets];
        return new CutsceneResidencyManifest([.. _assets], [.. _kinds], buckets);
    }

    private static List<ResidencyBucket> Merge(List<ResidencyBucket> source)
    {
        List<ResidencyBucket> merged = new(source.Count);
        for (int i = 0; i < source.Count; i++)
        {
            ResidencyBucket bucket = source[i];
            if (merged.Count > 0)
            {
                ResidencyBucket last = merged[^1];
                if (!bucket.StartsAtCut && last.End == bucket.Start && last.Entries.AsSpan().SequenceEqual(bucket.Entries))
                {
                    merged[^1] = new ResidencyBucket(last.Start, bucket.End, last.Entries, last.StartsAtCut);
                    continue;
                }
            }
            merged.Add(bucket);
        }
        return merged;
    }

    private static ResidencyBucket[] Encode(List<ResidencyBucket> full, CutsceneTime keyInterval)
    {
        if (keyInterval.Flicks <= 0)
            throw new InvalidOperationException("The key interval must be greater than zero.");

        ResidencyBucket[] encoded = new ResidencyBucket[full.Count];
        List<ResidencyEntry> changed = [];
        List<int> removed = [];
        CutsceneTime lastKey = CutsceneTime.Zero;
        for (int i = 0; i < full.Count; i++)
        {
            ResidencyBucket bucket = full[i];
            bool key = i == 0 || bucket.StartsAtCut || bucket.Start - lastKey >= keyInterval;
            if (!key)
            {
                Diff(full[i - 1].Entries, bucket.Entries, changed, removed);
                key = changed.Count + removed.Count >= bucket.Entries.Length;
            }

            if (key)
            {
                encoded[i] = bucket;
                lastKey = bucket.Start;
            }
            else
                encoded[i] = ResidencyBucket.CreateDelta(bucket.Start, bucket.End, [.. changed], [.. removed]);
        }
        return encoded;
    }

    /// <summary>Finds the entries of <paramref name="current"/> that are new or changed, and the assets that left. Both lists sort by asset index.</summary>
    private static void Diff(ResidencyEntry[] previous, ResidencyEntry[] current, List<ResidencyEntry> changed, List<int> removed)
    {
        changed.Clear();
        removed.Clear();
        int p = 0;
        int c = 0;
        while (p < previous.Length || c < current.Length)
        {
            if (c >= current.Length || (p < previous.Length && previous[p].AssetIndex < current[c].AssetIndex))
            {
                removed.Add(previous[p++].AssetIndex);
                continue;
            }
            if (p >= previous.Length || current[c].AssetIndex < previous[p].AssetIndex)
            {
                changed.Add(current[c++]);
                continue;
            }
            if (previous[p] != current[c])
                changed.Add(current[c]);
            p++;
            c++;
        }
    }
}
