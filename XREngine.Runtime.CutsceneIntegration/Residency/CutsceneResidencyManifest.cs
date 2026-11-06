namespace XREngine.Cutscenes;

/// <summary>
/// The residency data of a cutscene: a shared table of assets and the buckets that refer to it.
/// The buckets are in time order and do not overlap.
/// The constructor checks the data one time. Do not change the arrays after that, because a planner
/// copies the bucket times when it is created.
/// </summary>
public sealed class CutsceneResidencyManifest
{
    /// <summary>Creates a manifest and checks its data.</summary>
    /// <param name="assets">The shared asset table. Each identifier must occur one time.</param>
    /// <param name="buckets">The buckets, in time order. A bucket must not start before the end of the bucket before it.</param>
    public CutsceneResidencyManifest(Guid[] assets, ResidencyBucket[] buckets)
    {
        ArgumentNullException.ThrowIfNull(assets);
        ArgumentNullException.ThrowIfNull(buckets);

        HashSet<Guid> seen = new(assets.Length);
        for (int i = 0; i < assets.Length; i++)
            if (!seen.Add(assets[i]))
                throw new ArgumentException($"Asset {assets[i]} occurs more than one time in the residency manifest.", nameof(assets));

        for (int b = 0; b < buckets.Length; b++)
        {
            ResidencyBucket bucket = buckets[b] ?? throw new ArgumentException($"Residency bucket {b} is null.", nameof(buckets));
            if (b > 0 && bucket.Start < buckets[b - 1].End)
                throw new ArgumentException($"Residency bucket {b} starts before the end of the bucket before it.", nameof(buckets));

            ResidencyEntry[] entries = bucket.Entries;
            for (int e = 0; e < entries.Length; e++)
            {
                ResidencyEntry entry = entries[e];
                if ((uint)entry.AssetIndex >= (uint)assets.Length)
                    throw new ArgumentException($"Entry {e} of residency bucket {b} refers to asset index {entry.AssetIndex}, which is not in the asset table.", nameof(buckets));
                if (entry.TargetLevel < 0)
                    throw new ArgumentException($"Entry {e} of residency bucket {b} has a negative target level.", nameof(buckets));
            }
        }

        Assets = assets;
        Buckets = buckets;
    }

    /// <summary>The shared asset table. Entries refer to it by index.</summary>
    public Guid[] Assets { get; }

    /// <summary>The buckets, in time order, with no overlap.</summary>
    public ResidencyBucket[] Buckets { get; }
}
