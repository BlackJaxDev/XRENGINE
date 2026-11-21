using XREngine.Components;
using XREngine.Scene;
using XREngine.Rendering.Residency;

namespace XREngine.Cutscenes;

/// <summary>
/// Builds a residency manifest without a bake, from the clip ranges of the tracks and the root bounds of the bound nodes.
/// Playback uses it when the asset has no manifest or its manifest is stale.
/// </summary>
/// <remarks>
/// <para>
/// The buckets split the timeline at each clip edge. A bucket holds the assets of the bound nodes, and of their
/// descendants, whose tracks have an active clip in the bucket. A camera cut clip marks a cut at its start.
/// </para>
/// <para>
/// The result is an estimate. It does not run the timeline, so it uses the poses of the open time and not the animated
/// poses. It does not see objects that no track binds. A spawned node is hidden at the open, so the estimate also
/// includes hidden nodes. The coverage comes from the camera of the active camera cut at its open pose. An asset with
/// coverage at or above the target coverage, or with no camera, needs T2, and any other asset needs T1. The estimate
/// never drops an asset for its coverage, because the open pose can be wrong.
/// </para>
/// </remarks>
public static class CutsceneApproximateManifest
{
    /// <summary>Builds the approximate manifest of a compiled cutscene.</summary>
    /// <param name="compiled">The compiled cutscene.</param>
    /// <param name="bindings">The resolved bindings of the cutscene.</param>
    /// <param name="collector">The collector that finds the assets of each node.</param>
    /// <param name="targetCoverage">The screen coverage at or above which an asset needs T2.</param>
    public static CutsceneResidencyManifest Build(
        CompiledCutscene compiled,
        CutsceneBindingTable bindings,
        ICutsceneResidencyAssetCollector collector,
        float targetCoverage = CutsceneResidencyManifestBuilder.DefaultTargetCoverage)
    {
        ArgumentNullException.ThrowIfNull(compiled);
        ArgumentNullException.ThrowIfNull(bindings);
        ArgumentNullException.ThrowIfNull(collector);

        CutsceneAsset asset = compiled.Asset;
        CutsceneResidencyManifestBuilder builder = new();
        long duration = asset.Duration.Flicks;
        if (duration <= 0)
            return builder.Build();

        SortedSet<long> edges = [0L, duration];
        HashSet<long> cuts = [];
        for (int t = 0; t < asset.Tracks.Count; t++)
        {
            CutsceneTrack track = asset.Tracks[t];
            if (track is EventTrack or AudioTrack)
                continue;

            for (int c = 0; c < track.Clips.Count; c++)
            {
                CutsceneClip clip = track.Clips[c];
                AddEdge(edges, clip.Start.Flicks, duration);
                AddEdge(edges, (clip.Start + clip.Duration).Flicks, duration);
                if (track is CameraCutTrack && clip.Start.Flicks > 0 && clip.Start.Flicks < duration)
                    cuts.Add(clip.Start.Flicks);
            }
        }

        Dictionary<int, List<CutsceneResidencyAsset>> slotAssets = [];
        CutsceneActiveClip[] active = new CutsceneActiveClip[Math.Max(1, compiled.MaxActiveClips)];
        long[] times = [.. edges];
        for (int i = 0; i + 1 < times.Length; i++)
        {
            long start = times[i];
            long end = times[i + 1];
            int count = compiled.QueryActive(new CutsceneTime(start), active);
            bool hasView = TryFindView(asset, bindings, active, count, out CutsceneResidencyView view);

            builder.BeginBucket(new CutsceneTime(start), new CutsceneTime(end), cuts.Contains(start));
            for (int k = 0; k < count; k++)
            {
                CutsceneTrack track = asset.Tracks[active[k].TrackIndex];
                if (track is EventTrack or AudioTrack or CameraCutTrack)
                    continue;

                List<CutsceneResidencyAsset> assets = GetSlotAssets(slotAssets, bindings, track.BindingSlot, collector);
                for (int a = 0; a < assets.Count; a++)
                {
                    CutsceneResidencyAsset item = assets[a];
                    float coverage = hasView ? view.ScreenCoverage(item.WorldCenter, item.WorldRadius) : 0.0f;
                    int level = !hasView || coverage >= targetCoverage ? (int)EResidencyLevel.Target : (int)EResidencyLevel.Coarse;
                    builder.AddEntry(builder.AddAsset(in item), level, coverage);
                }
            }
            builder.EndBucket();
        }

        return builder.Build();
    }

    private static void AddEdge(SortedSet<long> edges, long time, long duration)
    {
        if (time > 0 && time < duration)
            edges.Add(time);
    }

    private static List<CutsceneResidencyAsset> GetSlotAssets(
        Dictionary<int, List<CutsceneResidencyAsset>> cache,
        CutsceneBindingTable bindings,
        int slot,
        ICutsceneResidencyAssetCollector collector)
    {
        if (cache.TryGetValue(slot, out List<CutsceneResidencyAsset>? assets))
            return assets;

        assets = [];
        if (bindings.GetNode(slot) is { } node)
            CutsceneResidencySceneWalker.Collect(node, collector, assets, shownOnly: false);
        cache.Add(slot, assets);
        return assets;
    }

    private static bool TryFindView(CutsceneAsset asset, CutsceneBindingTable bindings, CutsceneActiveClip[] active, int count, out CutsceneResidencyView view)
    {
        // The camera cut with the highest weight, and the latest start on a tie, selects the camera, as in playback.
        CameraCutClip? best = null;
        float bestWeight = 0.0f;
        for (int k = 0; k < count; k++)
        {
            CutsceneTrack track = asset.Tracks[active[k].TrackIndex];
            if (track is not CameraCutTrack || track.Clips[active[k].ClipIndex] is not CameraCutClip clip || active[k].Weight <= 0.0f)
                continue;
            if (best is null || active[k].Weight > bestWeight || (active[k].Weight == bestWeight && clip.Start >= best.Start))
            {
                best = clip;
                bestWeight = active[k].Weight;
            }
        }

        SceneNode? node = best is null ? null : bindings.GetNode(best.CameraBindingSlot);
        CameraComponent? camera = CutsceneCameraLookup.FindCamera(node);
        if (camera is not null && CutsceneResidencyView.TryCreate(camera, out view))
            return true;

        view = default;
        return false;
    }
}
