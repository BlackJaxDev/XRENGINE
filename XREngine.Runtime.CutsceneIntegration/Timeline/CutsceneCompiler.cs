namespace XREngine.Cutscenes;

/// <summary>Builds a <see cref="CompiledCutscene"/> time index from a cutscene asset.</summary>
public static class CutsceneCompiler
{
    /// <summary>
    /// Compiles an asset. A clip with a duration of zero or less, or with a speed of zero or less, is skipped
    /// and gets a diagnostic.
    /// </summary>
    /// <param name="asset">The asset to compile.</param>
    /// <param name="diagnostics">The list that receives the problems that the compiler finds.</param>
    /// <returns>The compiled time index.</returns>
    public static CompiledCutscene Compile(CutsceneAsset asset, List<CutsceneCompileDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentNullException.ThrowIfNull(diagnostics);

        long assetEnd = asset.Duration.Flicks;
        List<CompiledCutscene.ClipData> clips = [];
        List<long> ends = [];

        for (int trackIndex = 0; trackIndex < asset.Tracks.Count; trackIndex++)
        {
            List<CutsceneClip> trackClips = asset.Tracks[trackIndex].Clips;
            for (int clipIndex = 0; clipIndex < trackClips.Count; clipIndex++)
            {
                CutsceneClip clip = trackClips[clipIndex];
                if (clip.Duration.Flicks <= 0)
                {
                    diagnostics.Add(new CutsceneCompileDiagnostic(trackIndex, clipIndex, "The clip duration must be greater than zero. The clip is skipped."));
                    continue;
                }
                if (!IsValidSpeed(clip.Speed))
                {
                    diagnostics.Add(new CutsceneCompileDiagnostic(trackIndex, clipIndex, "The clip speed must be a finite value greater than zero. The clip is skipped."));
                    continue;
                }

                long start = clip.Start.Flicks;
                long clipEnd = start + clip.Duration.Flicks;
                long end = clip.Extrapolation switch
                {
                    ECutsceneClipExtrapolation.Hold => Math.Max(clipEnd, assetEnd),
                    ECutsceneClipExtrapolation.Loop => Math.Max(clipEnd, FindNextStart(trackClips, clip, assetEnd)),
                    _ => clipEnd,
                };

                clips.Add(new CompiledCutscene.ClipData(
                    trackIndex,
                    clipIndex,
                    start,
                    clip.Duration.Flicks,
                    clip.ClipOffset.Flicks,
                    clip.Speed,
                    clip.BlendIn.Flicks,
                    clip.BlendOut.Flicks,
                    clip.Extrapolation));
                ends.Add(end);
            }
        }

        if (clips.Count == 0)
            return new CompiledCutscene([], [], [], [], [], 0);

        List<long> edges = new(clips.Count * 2);
        for (int i = 0; i < clips.Count; i++)
        {
            edges.Add(clips[i].Start);
            edges.Add(ends[i]);
        }
        edges.Sort();

        // Remove duplicate edges. The last edge stays as the end of the final segment.
        int unique = 0;
        for (int i = 0; i < edges.Count; i++)
            if (i == 0 || edges[i] != edges[i - 1])
                edges[unique++] = edges[i];
        edges.RemoveRange(unique, edges.Count - unique);

        int segmentCount = edges.Count - 1;
        long[] segmentStarts = edges.ToArray();
        int[] segmentOffsets = new int[segmentCount];
        int[] segmentCounts = new int[segmentCount];
        List<int> clipIndices = [];
        int maxActive = 0;

        for (int s = 0; s < segmentCount; s++)
        {
            long segmentStart = edges[s];
            segmentOffsets[s] = clipIndices.Count;
            for (int c = 0; c < clips.Count; c++)
                if (clips[c].Start <= segmentStart && segmentStart < ends[c])
                    clipIndices.Add(c);
            segmentCounts[s] = clipIndices.Count - segmentOffsets[s];
            maxActive = Math.Max(maxActive, segmentCounts[s]);
        }

        return new CompiledCutscene(clips.ToArray(), segmentStarts, segmentOffsets, segmentCounts, clipIndices.ToArray(), maxActive);
    }

    private static bool IsValidSpeed(double speed)
        => speed > 0.0 && !double.IsInfinity(speed);

    // Finds the start of the next valid clip on the track, or the asset end if there is none.
    private static long FindNextStart(List<CutsceneClip> trackClips, CutsceneClip clip, long assetEnd)
    {
        long next = long.MaxValue;
        for (int i = 0; i < trackClips.Count; i++)
        {
            CutsceneClip other = trackClips[i];
            if (ReferenceEquals(other, clip) || other.Duration.Flicks <= 0 || !IsValidSpeed(other.Speed))
                continue;
            if (other.Start.Flicks > clip.Start.Flicks && other.Start.Flicks < next)
                next = other.Start.Flicks;
        }
        return next == long.MaxValue ? assetEnd : next;
    }
}
