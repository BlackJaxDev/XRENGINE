namespace XREngine.Cutscenes;

/// <summary>
/// The residency data of a cutscene: a shared table of assets and the buckets that refer to it.
/// The buckets are in time order and do not overlap. The buckets are delta-compressed: a key bucket lists all of its
/// entries, and a delta bucket lists only the changes against the bucket before it (see <see cref="ResidencyBucket"/>).
/// <see cref="CutsceneResidencyBucketCursor"/> reads the full entries of any bucket.
/// The data is serializable, so the properties have setters. A planner calls <see cref="Validate"/> when it is
/// created and copies the bucket times. Do not change the arrays after that.
/// </summary>
public sealed class CutsceneResidencyManifest
{
    /// <summary>Creates an empty manifest. The serializers use this constructor.</summary>
    public CutsceneResidencyManifest()
    {
    }

    /// <summary>Creates a manifest of texture assets and checks its data.</summary>
    /// <param name="assets">The shared asset table. Each identifier must occur one time.</param>
    /// <param name="buckets">The buckets, in time order. A bucket must not start before the end of the bucket before it.</param>
    public CutsceneResidencyManifest(Guid[] assets, ResidencyBucket[] buckets)
        : this(assets, null, buckets)
    {
    }

    /// <summary>Creates a manifest and checks its data.</summary>
    /// <param name="assets">The shared asset table. Each identifier must occur one time.</param>
    /// <param name="assetKinds">The kind of each asset, or null if each asset is a texture.</param>
    /// <param name="buckets">The buckets, in time order. A bucket must not start before the end of the bucket before it.</param>
    public CutsceneResidencyManifest(Guid[] assets, EResidencyAssetKind[]? assetKinds, ResidencyBucket[] buckets)
    {
        ArgumentNullException.ThrowIfNull(assets);
        ArgumentNullException.ThrowIfNull(buckets);

        Assets = assets;
        AssetKinds = assetKinds ?? new EResidencyAssetKind[assets.Length];
        Buckets = buckets;
        Validate();
    }

    /// <summary>The shared asset table. Entries refer to it by index.</summary>
    public Guid[] Assets { get; set; } = [];

    /// <summary>The kind of each asset. Item <c>i</c> belongs to <see cref="Assets"/> item <c>i</c>.</summary>
    public EResidencyAssetKind[] AssetKinds { get; set; } = [];

    /// <summary>The buckets, in time order, with no overlap.</summary>
    public ResidencyBucket[] Buckets { get; set; } = [];

    /// <summary>
    /// Checks the data. Each asset must occur one time, each asset must have a kind, the buckets must be in time order
    /// with no overlap, and each entry must refer to an asset of the table with a level of zero or more. The first bucket
    /// and each bucket at a camera cut must be key buckets. A delta bucket can remove only an asset that the bucket before
    /// needs, and a bucket must not list an asset two times.
    /// </summary>
    /// <exception cref="ArgumentException">The data is not valid.</exception>
    public void Validate()
    {
        Guid[] assets = Assets ?? throw new ArgumentException("The asset table of the residency manifest is null.", nameof(Assets));
        ResidencyBucket[] buckets = Buckets ?? throw new ArgumentException("The buckets of the residency manifest are null.", nameof(Buckets));
        if (AssetKinds is null || AssetKinds.Length != assets.Length)
            throw new ArgumentException("The residency manifest must have one asset kind for each asset.", nameof(AssetKinds));

        HashSet<Guid> seen = new(assets.Length);
        for (int i = 0; i < assets.Length; i++)
            if (!seen.Add(assets[i]))
                throw new ArgumentException($"Asset {assets[i]} occurs more than one time in the residency manifest.", nameof(Assets));

        bool[] present = new bool[assets.Length];
        bool[] listed = new bool[assets.Length];
        for (int b = 0; b < buckets.Length; b++)
        {
            ResidencyBucket bucket = buckets[b] ?? throw new ArgumentException($"Residency bucket {b} is null.", nameof(Buckets));
            if (bucket.End <= bucket.Start)
                throw new ArgumentException($"Residency bucket {b} does not end after its start.", nameof(Buckets));
            if (b > 0 && bucket.Start < buckets[b - 1].End)
                throw new ArgumentException($"Residency bucket {b} starts before the end of the bucket before it.", nameof(Buckets));
            if (!bucket.IsKey && (b == 0 || bucket.StartsAtCut))
                throw new ArgumentException($"Residency bucket {b} must be a key bucket, because it is the first bucket or it starts at a camera cut.", nameof(Buckets));

            ResidencyEntry[] entries = bucket.Entries ?? throw new ArgumentException($"The entries of residency bucket {b} are null.", nameof(Buckets));
            int[] removed = bucket.Removed ?? throw new ArgumentException($"The removed list of residency bucket {b} is null.", nameof(Buckets));
            if (bucket.IsKey)
            {
                if (removed.Length > 0)
                    throw new ArgumentException($"Key residency bucket {b} must not remove entries.", nameof(Buckets));
                Array.Clear(present);
            }

            for (int r = 0; r < removed.Length; r++)
            {
                int asset = removed[r];
                if ((uint)asset >= (uint)assets.Length || !present[asset])
                    throw new ArgumentException($"Residency bucket {b} removes asset index {asset}, which the bucket before does not need.", nameof(Buckets));
                present[asset] = false;
            }

            Array.Clear(listed);
            for (int e = 0; e < entries.Length; e++)
            {
                ResidencyEntry entry = entries[e];
                if ((uint)entry.AssetIndex >= (uint)assets.Length)
                    throw new ArgumentException($"Entry {e} of residency bucket {b} refers to asset index {entry.AssetIndex}, which is not in the asset table.", nameof(Buckets));
                if (entry.TargetLevel < 0)
                    throw new ArgumentException($"Entry {e} of residency bucket {b} has a negative target level.", nameof(Buckets));
                if (listed[entry.AssetIndex])
                    throw new ArgumentException($"Residency bucket {b} lists asset index {entry.AssetIndex} more than one time.", nameof(Buckets));
                listed[entry.AssetIndex] = true;
                present[entry.AssetIndex] = true;
            }
        }
    }

    /// <summary>Counts the assets of a kind.</summary>
    /// <param name="kind">The kind to count.</param>
    public int CountKind(EResidencyAssetKind kind)
    {
        int count = 0;
        EResidencyAssetKind[] kinds = AssetKinds;
        for (int i = 0; i < kinds.Length; i++)
            if (kinds[i] == kind)
                count++;
        return count;
    }

    /// <summary>
    /// Counts the stored entries: the entries of the key buckets, the changed entries of the delta buckets,
    /// and the removed indices. It shows the size of the compressed data.
    /// </summary>
    public int CountStoredEntries()
    {
        int count = 0;
        for (int b = 0; b < Buckets.Length; b++)
            count += Buckets[b].Entries.Length + Buckets[b].Removed.Length;
        return count;
    }

    /// <summary>
    /// Creates a compressed manifest that holds only the assets of one kind, with the entries of those assets.
    /// The time ranges and the cut flags stay; buckets that become equal are merged. The result is a new object.
    /// </summary>
    /// <param name="kind">The kind to keep.</param>
    public CutsceneResidencyManifest SelectKind(EResidencyAssetKind kind)
    {
        Validate();

        CutsceneResidencyManifestBuilder builder = new();
        int[] remap = new int[Assets.Length];
        for (int i = 0; i < Assets.Length; i++)
            remap[i] = AssetKinds[i] == kind ? builder.AddAsset(Assets[i], kind, 0L) : -1;

        CutsceneResidencyBucketCursor cursor = new(this);
        for (int b = 0; b < Buckets.Length; b++)
        {
            ResidencyBucket source = Buckets[b];
            cursor.MoveTo(b);
            builder.BeginBucket(source.Start, source.End, source.StartsAtCut);
            for (int k = 0; k < cursor.Count; k++)
            {
                ResidencyEntry entry = cursor.GetEntry(k);
                int index = remap[entry.AssetIndex];
                if (index >= 0)
                    builder.AddEntry(index, entry.TargetLevel, entry.ScreenCoverage);
            }
            builder.EndBucket();
        }

        return builder.Build();
    }
}
