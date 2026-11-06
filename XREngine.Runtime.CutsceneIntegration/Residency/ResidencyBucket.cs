namespace XREngine.Cutscenes;

/// <summary>The assets that a time range of a cutscene needs, and the level of each asset.</summary>
public sealed class ResidencyBucket
{
    /// <summary>Creates an empty bucket. The serializers use this constructor. <see cref="CutsceneResidencyManifest.Validate"/> checks the values.</summary>
    public ResidencyBucket()
    {
    }

    /// <summary>Creates a bucket.</summary>
    /// <param name="start">The first time of the range.</param>
    /// <param name="end">The end of the range. The range does not include it. It must be after <paramref name="start"/>.</param>
    /// <param name="entries">The assets that the range needs.</param>
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

    /// <summary>The first time of the range.</summary>
    public CutsceneTime Start { get; set; }

    /// <summary>The end of the range. The range does not include it.</summary>
    public CutsceneTime End { get; set; }

    /// <summary>
    /// The assets that the range needs. The planner checks the entries one time, when it is created.
    /// Do not change the array after that.
    /// </summary>
    public ResidencyEntry[] Entries { get; set; } = [];

    /// <summary>True if a camera cut occurs at <see cref="Start"/>. The playback wait holds only at a cut.</summary>
    public bool StartsAtCut { get; set; }
}
