namespace XREngine.Cutscenes;

/// <summary>
/// The residency data of a cutscene: a shared table of assets and the buckets that refer to it.
/// The buckets are in time order and do not overlap.
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
    /// with no overlap, and each entry must refer to an asset of the table with a level of zero or more.
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

        for (int b = 0; b < buckets.Length; b++)
        {
            ResidencyBucket bucket = buckets[b] ?? throw new ArgumentException($"Residency bucket {b} is null.", nameof(Buckets));
            if (bucket.End <= bucket.Start)
                throw new ArgumentException($"Residency bucket {b} does not end after its start.", nameof(Buckets));
            if (b > 0 && bucket.Start < buckets[b - 1].End)
                throw new ArgumentException($"Residency bucket {b} starts before the end of the bucket before it.", nameof(Buckets));

            ResidencyEntry[] entries = bucket.Entries ?? throw new ArgumentException($"The entries of residency bucket {b} are null.", nameof(Buckets));
            for (int e = 0; e < entries.Length; e++)
            {
                ResidencyEntry entry = entries[e];
                if ((uint)entry.AssetIndex >= (uint)assets.Length)
                    throw new ArgumentException($"Entry {e} of residency bucket {b} refers to asset index {entry.AssetIndex}, which is not in the asset table.", nameof(Buckets));
                if (entry.TargetLevel < 0)
                    throw new ArgumentException($"Entry {e} of residency bucket {b} has a negative target level.", nameof(Buckets));
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
    /// Creates a manifest that holds only the assets of one kind, with the same buckets and the entries of those assets.
    /// A bucket keeps its time range and its cut flag also when it has no entry left. The result is a new object.
    /// </summary>
    /// <param name="kind">The kind to keep.</param>
    public CutsceneResidencyManifest SelectKind(EResidencyAssetKind kind)
    {
        Validate();

        int[] remap = new int[Assets.Length];
        List<Guid> assets = new(Assets.Length);
        for (int i = 0; i < Assets.Length; i++)
        {
            if (AssetKinds[i] != kind)
            {
                remap[i] = -1;
                continue;
            }

            remap[i] = assets.Count;
            assets.Add(Assets[i]);
        }

        ResidencyBucket[] buckets = new ResidencyBucket[Buckets.Length];
        List<ResidencyEntry> entries = [];
        for (int b = 0; b < Buckets.Length; b++)
        {
            ResidencyBucket source = Buckets[b];
            entries.Clear();
            for (int e = 0; e < source.Entries.Length; e++)
            {
                ResidencyEntry entry = source.Entries[e];
                int index = remap[entry.AssetIndex];
                if (index >= 0)
                    entries.Add(entry with { AssetIndex = index });
            }
            buckets[b] = new ResidencyBucket(source.Start, source.End, [.. entries], source.StartsAtCut);
        }

        EResidencyAssetKind[] kinds = new EResidencyAssetKind[assets.Count];
        Array.Fill(kinds, kind);
        return new CutsceneResidencyManifest([.. assets], kinds, buckets);
    }
}
