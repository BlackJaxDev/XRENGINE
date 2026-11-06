namespace XREngine.Cutscenes;

/// <summary>
/// The assets that a time range of a cutscene needs, and the level of each asset.
/// A bucket is a key bucket or a delta bucket. A key bucket lists all of its entries. A delta bucket lists only the
/// changes against the bucket before it: the entries that are new or changed, and the asset indices that are removed.
/// Use <see cref="CutsceneResidencyBucketCursor"/> to read the full entries of any bucket.
/// </summary>
public sealed class ResidencyBucket
{
    /// <summary>Creates an empty key bucket. The serializers use this constructor. <see cref="CutsceneResidencyManifest.Validate"/> checks the values.</summary>
    public ResidencyBucket()
    {
    }

    /// <summary>Creates a key bucket.</summary>
    /// <param name="start">The first time of the range.</param>
    /// <param name="end">The end of the range. The range does not include it. It must be after <paramref name="start"/>.</param>
    /// <param name="entries">All the assets that the range needs.</param>
    /// <param name="startsAtCut">True if a camera cut occurs at <paramref name="start"/>.</param>
    public ResidencyBucket(CutsceneTime start, CutsceneTime end, ResidencyEntry[] entries, bool startsAtCut = false)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (end <= start)
            throw new ArgumentException("The end of a residency bucket must be after its start.", nameof(end));

        Start = start;
        End = end;
        Entries = entries;
        StartsAtCut = startsAtCut;
    }

    /// <summary>Creates a delta bucket. A delta bucket must not start at a camera cut, because a cut is always a key bucket.</summary>
    /// <param name="start">The first time of the range.</param>
    /// <param name="end">The end of the range. It must be after <paramref name="start"/>.</param>
    /// <param name="changed">The entries that are new, or whose level or coverage changed, against the bucket before.</param>
    /// <param name="removed">The asset indices that the bucket before needs and this bucket does not.</param>
    public static ResidencyBucket CreateDelta(CutsceneTime start, CutsceneTime end, ResidencyEntry[] changed, int[] removed)
    {
        ArgumentNullException.ThrowIfNull(removed);
        return new ResidencyBucket(start, end, changed) { IsDelta = true, Removed = removed };
    }

    /// <summary>The first time of the range.</summary>
    public CutsceneTime Start { get; set; }

    /// <summary>The end of the range. The range does not include it.</summary>
    public CutsceneTime End { get; set; }

    /// <summary>
    /// For a key bucket, all the assets that the range needs. For a delta bucket, the entries that are new or changed
    /// against the bucket before. The planner checks the data one time, when it is created. Do not change the array after that.
    /// </summary>
    public ResidencyEntry[] Entries { get; set; } = [];

    /// <summary>For a delta bucket, the asset indices that the bucket before needs and this bucket does not. Empty for a key bucket.</summary>
    public int[] Removed { get; set; } = [];

    /// <summary>
    /// True for a delta bucket, which lists only its changes against the bucket before. False (the default) for a key bucket,
    /// which lists all of its entries. The first bucket and each bucket at a camera cut are key buckets.
    /// The stored flag is the delta flag, so that the default value means a key bucket in each serializer.
    /// </summary>
    public bool IsDelta { get; set; }

    /// <summary>True for a key bucket. It is the opposite of <see cref="IsDelta"/>.</summary>
    [YamlDotNet.Serialization.YamlIgnore]
    public bool IsKey => !IsDelta;

    /// <summary>True if a camera cut occurs at <see cref="Start"/>. The playback wait holds only at a cut.</summary>
    public bool StartsAtCut { get; set; }
}
