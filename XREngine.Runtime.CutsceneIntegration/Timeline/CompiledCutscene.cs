namespace XREngine.Cutscenes;

/// <summary>
/// A time index of a cutscene asset. The index splits the timeline at every clip edge.
/// Each segment lists the clips that are active inside it. A query does a binary search and does not allocate.
/// </summary>
public sealed class CompiledCutscene
{
    /// <summary>The data of one clip that the compiler accepted.</summary>
    internal readonly record struct ClipData(
        int TrackIndex,
        int ClipIndex,
        long Start,
        long Duration,
        long ClipOffset,
        double Speed,
        long BlendIn,
        long BlendOut,
        ECutsceneClipExtrapolation Extrapolation);

    private readonly ClipData[] _clips;
    private readonly long[] _segmentStarts;
    private readonly int[] _segmentOffsets;
    private readonly int[] _segmentCounts;
    private readonly int[] _clipIndices;

    internal CompiledCutscene(
        CutsceneAsset asset,
        ClipData[] clips,
        long[] segmentStarts,
        int[] segmentOffsets,
        int[] segmentCounts,
        int[] clipIndices,
        int maxActiveClips)
    {
        Asset = asset;
        _clips = clips;
        _segmentStarts = segmentStarts;
        _segmentOffsets = segmentOffsets;
        _segmentCounts = segmentCounts;
        _clipIndices = clipIndices;
        MaxActiveClips = maxActiveClips;
    }

    /// <summary>The asset that was compiled. It is the source of the track and binding data.</summary>
    public CutsceneAsset Asset { get; }

    /// <summary>The largest number of clips that are active at the same time.</summary>
    public int MaxActiveClips { get; }

    /// <summary>
    /// Finds the clips that are active at a time. Any time is valid, also a time outside the cutscene duration.
    /// </summary>
    /// <param name="time">The time to query.</param>
    /// <param name="destination">
    /// The storage for the results. If it is smaller than the active count, only the clips that fit are written.
    /// </param>
    /// <returns>The number of results written to <paramref name="destination"/>.</returns>
    public int QueryActive(CutsceneTime time, Span<CutsceneActiveClip> destination)
    {
        int segment = FindSegment(time.Flicks);
        if (segment < 0)
            return 0;

        int count = Math.Min(_segmentCounts[segment], destination.Length);
        int offset = _segmentOffsets[segment];
        for (int i = 0; i < count; i++)
            destination[i] = Evaluate(in _clips[_clipIndices[offset + i]], time.Flicks);

        return count;
    }

    // Returns the segment that holds the time, or -1 if the time is outside every segment.
    private int FindSegment(long t)
    {
        int length = _segmentStarts.Length;
        if (length < 2 || t < _segmentStarts[0] || t >= _segmentStarts[length - 1])
            return -1;

        // The last segment start is the end edge of the final segment, so the search covers the first length - 1 starts.
        int low = 0;
        int high = length - 2;
        while (low < high)
        {
            int mid = low + ((high - low + 1) >> 1);
            if (_segmentStarts[mid] <= t)
                low = mid;
            else
                high = mid - 1;
        }
        return low;
    }

    private static CutsceneActiveClip Evaluate(in ClipData clip, long t)
    {
        long elapsed = t - clip.Start;
        long local;
        float weight = 1f;

        if (elapsed < clip.Duration)
        {
            local = clip.ClipOffset + ScaleBySpeed(elapsed, clip.Speed);
            if (clip.BlendIn > 0 && elapsed < clip.BlendIn)
                weight = (float)((double)elapsed / clip.BlendIn);
            long remaining = clip.Duration - elapsed;
            if (clip.BlendOut > 0 && remaining < clip.BlendOut)
                weight = Math.Min(weight, (float)((double)remaining / clip.BlendOut));
        }
        else if (clip.Extrapolation == ECutsceneClipExtrapolation.Loop)
            local = clip.ClipOffset + ScaleBySpeed(elapsed % clip.Duration, clip.Speed);
        else
            local = clip.ClipOffset + ScaleBySpeed(clip.Duration, clip.Speed);

        return new CutsceneActiveClip(clip.TrackIndex, clip.ClipIndex, new CutsceneTime(local), weight);
    }

    private static long ScaleBySpeed(long flicks, double speed)
        => (long)Math.Round(flicks * speed);
}
