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

                // A Hold or Loop clip stays active until the next clip on the track finishes its fade in,
                // also when its own duration is longer. Without a next clip, it stays active until the asset end.
                CutsceneClip? next = clip.Extrapolation == ECutsceneClipExtrapolation.None ? null : FindNextClip(trackClips, clip);
                long crossfadeStart = long.MaxValue;
                long crossfadeLength = 0;
                long end = clipEnd;
                if (clip.Extrapolation != ECutsceneClipExtrapolation.None)
                {
                    if (next is null)
                        end = Math.Max(clipEnd, assetEnd);
                    else
                    {
                        crossfadeStart = next.Start.Flicks;
                        crossfadeLength = Math.Max(0, next.BlendIn.Flicks);
                        end = crossfadeStart + crossfadeLength;
                    }
                }

                clips.Add(new CompiledCutscene.ClipData(
                    trackIndex,
                    clipIndex,
                    start,
                    clip.Duration.Flicks,
                    clip.ClipOffset.Flicks,
                    clip.Speed,
                    clip.BlendIn.Flicks,
                    clip.BlendOut.Flicks,
                    clip.Extrapolation,
                    crossfadeStart,
                    crossfadeLength));
                ends.Add(end);
            }
        }

        ReportNoneOverlaps(asset, diagnostics);

        if (clips.Count == 0)
            return new CompiledCutscene(asset, [], [], [], [], [], 0);

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

        return new CompiledCutscene(asset, clips.ToArray(), segmentStarts, segmentOffsets, segmentCounts, clipIndices.ToArray(), maxActive);
    }

    // A clip without extrapolation does not crossfade. A later clip that starts inside it is an overlap
    // that the author must fix. The player blends it by normalized weight.
    private static void ReportNoneOverlaps(CutsceneAsset asset, List<CutsceneCompileDiagnostic> diagnostics)
    {
        for (int trackIndex = 0; trackIndex < asset.Tracks.Count; trackIndex++)
        {
            List<CutsceneClip> trackClips = asset.Tracks[trackIndex].Clips;
            for (int i = 0; i < trackClips.Count; i++)
            {
                CutsceneClip clip = trackClips[i];
                if (clip.Extrapolation != ECutsceneClipExtrapolation.None || clip.Duration.Flicks <= 0 || !IsValidSpeed(clip.Speed))
                    continue;

                long end = clip.Start.Flicks + clip.Duration.Flicks;
                for (int j = 0; j < trackClips.Count; j++)
                {
                    CutsceneClip other = trackClips[j];
                    if (j == i || other.Duration.Flicks <= 0 || !IsValidSpeed(other.Speed))
                        continue;
                    bool startsLater = other.Start.Flicks > clip.Start.Flicks || (other.Start.Flicks == clip.Start.Flicks && j > i);
                    if (!startsLater || other.Start.Flicks >= end)
                        continue;

                    diagnostics.Add(new CutsceneCompileDiagnostic(trackIndex, i, $"The clip overlaps the clip {j} on the same track, and it has no hold or loop. The weights of the two clips are normalized."));
                    break;
                }
            }
        }
    }

    private static bool IsValidSpeed(double speed)
        => speed > 0.0 && !double.IsInfinity(speed);

    // Finds the next valid clip on the track, or null if there is none.
    private static CutsceneClip? FindNextClip(List<CutsceneClip> trackClips, CutsceneClip clip)
    {
        CutsceneClip? next = null;
        for (int i = 0; i < trackClips.Count; i++)
        {
            CutsceneClip other = trackClips[i];
            if (ReferenceEquals(other, clip) || other.Duration.Flicks <= 0 || !IsValidSpeed(other.Speed))
                continue;
            if (other.Start.Flicks > clip.Start.Flicks && (next is null || other.Start.Flicks < next.Start.Flicks))
                next = other;
        }
        return next;
    }
}
