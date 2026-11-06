using XREngine.Components;
using XREngine.Cutscenes;
using XREngine.Scene;

namespace XREngine.Editor.Cutscenes;

/// <summary>
/// Bakes the residency manifest of a cutscene. The bake runs the real <see cref="CutscenePlayer"/> with a
/// <see cref="ECutsceneClockMode.Manual"/> clock and samples the timeline at <see cref="SamplesPerSecond"/> samples per
/// second and at each camera cut. At each sample it records the assets that the active camera sees.
/// </summary>
/// <remarks>
/// <para>
/// Because the bake uses the real evaluator, the result includes the animated poses, the camera orientation, the clip
/// starts and offsets, and the spawned objects. A node that is hidden at a sample adds nothing. The coverage of an asset
/// comes from its bounding sphere in the view of the active camera, so an object behind the camera covers nothing.
/// An asset on screen needs T2 at or above <see cref="CutsceneBakeOptions.TargetCoverage"/> and T1 below it.
/// </para>
/// <para>
/// Each sample gives one bucket that ends at the next sample. A bucket that starts at a camera cut clip has the cut flag.
/// The builder then merges equal buckets and stores delta buckets (see <see cref="CutsceneResidencyManifestBuilder"/>).
/// The bake writes to the bound nodes and restores every recorded value at the end. Run it on the update thread and not
/// while a preview of the same scene is open. The bake does not change the asset; the caller stores the result in
/// <see cref="CutsceneAsset.Manifest"/> with <see cref="CutsceneManifestHash.Compute"/>.
/// </para>
/// </remarks>
public static class CutsceneBakeTool
{
    /// <summary>The number of samples in each second of the timeline. Each camera cut adds one sample.</summary>
    public const int SamplesPerSecond = 4;

    /// <summary>Bakes the residency manifest of a cutscene.</summary>
    /// <param name="asset">The cutscene asset.</param>
    /// <param name="options">The inputs and limits of the bake.</param>
    /// <returns>The manifest.</returns>
    /// <exception cref="CutsceneBakeException">The T0 total is above <see cref="CutsceneBakeOptions.T0BudgetBytes"/>.</exception>
    public static CutsceneResidencyManifest Bake(CutsceneAsset asset, CutsceneBakeOptions options)
        => Bake(asset, options, out _);

    /// <summary>Bakes the residency manifest of a cutscene and gives its T0 report.</summary>
    /// <param name="asset">The cutscene asset.</param>
    /// <param name="options">The inputs and limits of the bake.</param>
    /// <param name="report">The T0 report of the manifest.</param>
    /// <returns>The manifest.</returns>
    /// <exception cref="CutsceneBakeException">The T0 total is above <see cref="CutsceneBakeOptions.T0BudgetBytes"/>.</exception>
    public static CutsceneResidencyManifest Bake(CutsceneAsset asset, CutsceneBakeOptions options, out CutsceneT0Report report)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.T0BudgetBytes);

        List<CutsceneCompileDiagnostic> diagnostics = options.Diagnostics ?? [];
        ICutsceneResidencyAssetCollector collector = options.Collector ?? CutsceneRenderableAssetCollector.Instance;
        CutsceneResidencyManifestBuilder builder = new() { Compress = options.CompressManifest };

        CompiledCutscene compiled = CutsceneCompiler.Compile(asset, diagnostics);
        CutsceneBindingTable bindings = CutsceneBindingTable.Resolve(compiled, options.Resolver, options.RuntimeSlots);
        diagnostics.AddRange(bindings.Diagnostics);
        try
        {
            CutscenePlayer player = new(compiled, bindings, new CutsceneStateSnapshot(bindings), options.OpenAudioDecoder) { IsPreview = true };
            player.Clock.Mode = ECutsceneClockMode.Manual;
            player.Pause();
            try
            {
                SampleTimeline(asset, player, bindings, options, collector, builder, diagnostics);
            }
            finally
            {
                player.StopAndRestoreAll();
            }
        }
        finally
        {
            bindings.Dispose();
        }

        CutsceneResidencyManifest manifest = builder.Build();
        report = CreateReport(builder, options);
        if (report.IsOverBudget)
            throw new CutsceneBakeException(report);
        return manifest;
    }

    /// <summary>Gets the sample times of a cutscene: each quarter second, and each camera cut, in increasing order.</summary>
    /// <param name="asset">The cutscene asset.</param>
    /// <param name="cuts">The set that receives the times of the camera cuts.</param>
    public static long[] GetSampleTimes(CutsceneAsset asset, HashSet<long> cuts)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentNullException.ThrowIfNull(cuts);

        long duration = asset.Duration.Flicks;
        SortedSet<long> times = [];
        if (duration <= 0)
            return [];

        long step = CutsceneTime.FlicksPerSecond / SamplesPerSecond;
        for (long t = 0; t < duration; t += step)
            times.Add(t);

        for (int i = 0; i < asset.Tracks.Count; i++)
        {
            if (asset.Tracks[i] is not CameraCutTrack track)
                continue;

            for (int c = 0; c < track.Clips.Count; c++)
            {
                long start = track.Clips[c].Start.Flicks;
                if (start <= 0 || start >= duration)
                    continue;
                times.Add(start);
                cuts.Add(start);
            }
        }
        return [.. times];
    }

    private static void SampleTimeline(
        CutsceneAsset asset,
        CutscenePlayer player,
        CutsceneBindingTable bindings,
        CutsceneBakeOptions options,
        ICutsceneResidencyAssetCollector collector,
        CutsceneResidencyManifestBuilder builder,
        List<CutsceneCompileDiagnostic> diagnostics)
    {
        HashSet<long> cuts = [];
        long[] times = GetSampleTimes(asset, cuts);
        List<SceneNode> roots = CollectRoots(bindings, options.SceneRoots);
        List<CutsceneResidencyAsset> assets = [];
        int samplesWithoutView = 0;

        for (int i = 0; i < times.Length; i++)
        {
            long start = times[i];
            long end = i + 1 < times.Length ? times[i + 1] : asset.Duration.Flicks;
            player.Seek(new CutsceneTime(start), ESeekIntent.Jump);
            player.Tick(0.0);

            // The player writes local transforms. The world matrices must be current before the bounds are read.
            for (int r = 0; r < roots.Count; r++)
                roots[r].Transform.RecalculateMatrixHierarchyImmediate(forceWorldRecalc: true);

            builder.BeginBucket(new CutsceneTime(start), new CutsceneTime(end), cuts.Contains(start));
            CameraComponent? camera = player.ActiveCamera;
            if (camera is null || !CutsceneResidencyView.TryCreate(camera, out CutsceneResidencyView view))
            {
                samplesWithoutView++;
                builder.EndBucket();
                continue;
            }

            assets.Clear();
            for (int r = 0; r < roots.Count; r++)
                CutsceneResidencySceneWalker.Collect(roots[r], collector, assets, shownOnly: true);

            for (int a = 0; a < assets.Count; a++)
            {
                CutsceneResidencyAsset item = assets[a];
                float coverage = view.ScreenCoverage(item.WorldCenter, item.WorldRadius);
                int level = CutsceneResidencyManifestBuilder.SelectLevel(coverage, options.TargetCoverage);
                if (level >= 0)
                    builder.AddEntry(builder.AddAsset(in item), level, coverage);
            }
            builder.EndBucket();
        }

        if (samplesWithoutView > 0)
            diagnostics.Add(new CutsceneCompileDiagnostic(-1, -1,
                $"{samplesWithoutView} of {times.Length} bake samples have no active perspective or orthographic camera. Those buckets need only T0. Add a camera cut track to cover them."));
    }

    private static List<SceneNode> CollectRoots(CutsceneBindingTable bindings, IReadOnlyList<SceneNode>? sceneRoots)
    {
        HashSet<SceneNode> seen = new(ReferenceEqualityComparer.Instance);
        List<SceneNode> roots = [];
        if (sceneRoots is not null)
            for (int i = 0; i < sceneRoots.Count; i++)
                if (sceneRoots[i] is { } root && seen.Add(CutsceneResidencySceneWalker.GetRoot(root)))
                    roots.Add(CutsceneResidencySceneWalker.GetRoot(root));

        for (int slot = 0; slot < bindings.SlotCount; slot++)
            if (bindings.GetNode(slot) is { } node && seen.Add(CutsceneResidencySceneWalker.GetRoot(node)))
                roots.Add(CutsceneResidencySceneWalker.GetRoot(node));
        return roots;
    }

    private static CutsceneT0Report CreateReport(CutsceneResidencyManifestBuilder builder, CutsceneBakeOptions options)
    {
        int count = builder.AssetCount;
        CutsceneT0ReportEntry[] entries = new CutsceneT0ReportEntry[count];
        long total = 0;
        for (int i = 0; i < count; i++)
        {
            long bytes = builder.GetProxyBytes(i);
            total += bytes;
            entries[i] = new CutsceneT0ReportEntry(builder.GetAssetId(i), builder.GetAssetKind(i), bytes);
        }

        Array.Sort(entries, static (left, right) =>
        {
            int result = right.ProxyBytes.CompareTo(left.ProxyBytes);
            return result != 0 ? result : left.AssetId.CompareTo(right.AssetId);
        });

        int listed = Math.Clamp(options.ReportAssetCount, 0, count);
        return new CutsceneT0Report(total, options.T0BudgetBytes, count, entries[..listed]);
    }
}
