using XREngine.Components;
using XREngine.Core.Files;
using XREngine.Cutscenes;
using XREngine.Data;
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
/// The T0 size of a sub-mesh is the payload size of its coarsest LOD. The bake reports the sub-meshes with one LOD and the
/// sub-meshes whose coarsest LOD did not reduce (see <see cref="CutsceneMeshLodBakeCheck"/>).
/// </para>
/// <para>
/// The bake also makes an <see cref="AudioProxyAsset"/> for each audio clip source file (see <see cref="BakeAudioProxies"/>)
/// and lists each proxy in the manifest as an <see cref="EResidencyAssetKind.Audio"/> asset with no bucket entries, so
/// that its size counts in the T0 report.
/// </para>
/// <para>
/// Each sample gives one bucket that ends at the next sample. A bucket that starts at a camera cut clip has the cut flag.
/// The builder then merges equal buckets and stores delta buckets (see <see cref="CutsceneResidencyManifestBuilder"/>).
/// The bake writes to the bound nodes and restores every recorded value at the end. Run it on the update thread and not
/// while a preview of the same scene is open. The bake does not change the asset; the caller stores the result in
/// <see cref="CutsceneAsset.Manifest"/> with <see cref="CutsceneManifestHash.Compute"/>, and the audio proxies in
/// <see cref="CutsceneAsset.AudioProxies"/>.
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
        => Bake(asset, options, out report, out _);

    /// <summary>Bakes the residency manifest and the audio proxies of a cutscene, and gives its T0 report.</summary>
    /// <param name="asset">The cutscene asset.</param>
    /// <param name="options">The inputs and limits of the bake.</param>
    /// <param name="report">The T0 report of the manifest. It includes the audio proxies.</param>
    /// <param name="audioProxies">
    /// The audio proxies, one for each audio clip source file. Store them in <see cref="CutsceneAsset.AudioProxies"/>.
    /// </param>
    /// <returns>The manifest.</returns>
    /// <exception cref="CutsceneBakeException">The T0 total is above <see cref="CutsceneBakeOptions.T0BudgetBytes"/>.</exception>
    public static CutsceneResidencyManifest Bake(
        CutsceneAsset asset,
        CutsceneBakeOptions options,
        out CutsceneT0Report report,
        out List<AudioProxyAsset> audioProxies)
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

        audioProxies = BakeAudioProxies(asset, options.OpenAudioDecoder, diagnostics);
        for (int i = 0; i < audioProxies.Count; i++)
        {
            AudioProxyAsset proxy = audioProxies[i];
            builder.AddAsset(CutsceneResidencyAssetKeys.ForAudioSource(proxy.SourceReference), EResidencyAssetKind.Audio, proxy.EncodedData.Length);
        }

        CutsceneResidencyManifest manifest = builder.Build();
        report = CreateReport(builder, options);
        if (report.IsOverBudget)
            throw new CutsceneBakeException(report);
        return manifest;
    }

    /// <summary>
    /// Makes or refreshes the audio proxy of each audio clip source file of a cutscene. Clips with the same source share
    /// one proxy. A proxy of <see cref="CutsceneAsset.AudioProxies"/> with the same source reference and the same source
    /// file hash is used again with no new encode. Any other source is decoded, mixed to mono, resampled to 22050 Hz, and
    /// encoded (see <see cref="AudioProxyBuilder"/>). The method does not change the asset.
    /// </summary>
    /// <remarks>
    /// The source reference is the portable asset reference of the file (<c>game://</c> or <c>engine://</c>). If the file
    /// is outside the asset folders, it is the relative <see cref="AudioTrackClip.SourcePath"/> of the clip. A source with
    /// a full path outside the asset folders gets no proxy and one diagnostic, because a proxy must not store a local path.
    /// A source that does not open also gives one diagnostic.
    /// </remarks>
    /// <param name="asset">The cutscene asset. A relative source path starts at the folder of its file.</param>
    /// <param name="openDecoder">Opens the decoder of a full file path, or null for <see cref="AudioTrackEvaluator.OpenDefaultDecoder"/>.</param>
    /// <param name="diagnostics">The list that receives the problems.</param>
    /// <returns>The proxies, in the order of the first clip of each source.</returns>
    public static List<AudioProxyAsset> BakeAudioProxies(
        CutsceneAsset asset,
        Func<string, IStreamingAudioDecoder>? openDecoder,
        List<CutsceneCompileDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentNullException.ThrowIfNull(diagnostics);
        openDecoder ??= AudioTrackEvaluator.OpenDefaultDecoder;

        string? assetDirectory = Path.GetDirectoryName(asset.FilePath);
        List<AudioProxyAsset> proxies = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        for (int t = 0; t < asset.Tracks.Count; t++)
        {
            if (asset.Tracks[t] is not AudioTrack track)
                continue;

            for (int c = 0; c < track.Clips.Count; c++)
            {
                if (track.Clips[c] is not AudioTrackClip clip || string.IsNullOrWhiteSpace(clip.SourcePath))
                    continue;

                string fullPath = AudioTrackEvaluator.ResolveSourcePath(clip.SourcePath, assetDirectory);
                if (!CutsceneAudioProxySources.TryGetSourceReference(clip.SourcePath, fullPath, out string? reference))
                {
                    diagnostics.Add(new CutsceneCompileDiagnostic(t, c,
                        $"The audio file '{Path.GetFileName(fullPath)}' of clip {c} on audio track '{track.Name}' is outside the game and engine asset folders and has a full path, so it gets no audio proxy. Move the file into an asset folder, or use a path relative to the cutscene asset."));
                    continue;
                }
                if (!seen.Add(reference))
                    continue;

                try
                {
                    // A source with no file, for example a decoder that a test gives, has hash zero, so its proxy is always made again.
                    ulong hash = CutsceneAudioProxySources.HashSourceFile(fullPath);
                    AudioProxyAsset? proxy = FindCurrentProxy(asset.AudioProxies, reference, hash);
                    if (proxy is null)
                    {
                        using IStreamingAudioDecoder decoder = openDecoder(fullPath);
                        proxy = AudioProxyBuilder.Build(decoder, reference, hash);
                    }
                    proxies.Add(proxy);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException or ArgumentException)
                {
                    diagnostics.Add(new CutsceneCompileDiagnostic(t, c,
                        $"The audio proxy of '{reference}' (clip {c} on audio track '{track.Name}') was not made: {ex.Message}"));
                }
            }
        }
        return proxies;
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
        HashSet<Guid> machineDependent = [];
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
                if (item.IsMachineDependent)
                    machineDependent.Add(item.AssetId);
                float coverage = view.ScreenCoverage(item.WorldCenter, item.WorldRadius);
                int level = CutsceneResidencyManifestBuilder.SelectLevel(coverage, options.TargetCoverage);
                if (level >= 0)
                    builder.AddEntry(builder.AddAsset(in item), level, coverage);
            }
            builder.EndBucket();
        }

        if (machineDependent.Count > 0)
            diagnostics.Add(new CutsceneCompileDiagnostic(-1, -1,
                $"{machineDependent.Count} texture keys of the manifest use full file paths, because the textures are outside the game and engine asset folders. The manifest matches only on this machine and in this folder. Move the textures into an asset folder to make the keys portable."));

        if (samplesWithoutView > 0)
            diagnostics.Add(new CutsceneCompileDiagnostic(-1, -1,
                $"{samplesWithoutView} of {times.Length} bake samples have no active perspective or orthographic camera. Those buckets need only T0. Add a camera cut track to cover them."));

        // T0 of a sub-mesh is its coarsest LOD. Report the sub-meshes whose proxy is the full mesh or close to it.
        CutsceneMeshLodBakeCheck.Check(roots, diagnostics);
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

    private static AudioProxyAsset? FindCurrentProxy(List<AudioProxyAsset>? proxies, string reference, ulong hash)
    {
        if (hash == 0 || proxies is null)
            return null;

        for (int i = 0; i < proxies.Count; i++)
        {
            AudioProxyAsset? proxy = proxies[i];
            if (proxy is not null
                && proxy.SourceContentHash == hash
                && string.Equals(proxy.SourceReference, reference, StringComparison.OrdinalIgnoreCase)
                && IsCurrentFormat(proxy))
                return proxy;
        }
        return null;
    }

    // A proxy of an older format, or with data that does not agree with its frame count, is made again.
    private static bool IsCurrentFormat(AudioProxyAsset proxy)
        => proxy.SampleRate == ImaAdpcmDecoder.ProxySampleRate
        && proxy.BlockSamples == AudioProxyAsset.DefaultBlockSamples
        && proxy.FrameCount >= 0
        && proxy.EncodedData.Length == ImaAdpcmEncoder.GetEncodedBytes(proxy.FrameCount, proxy.BlockSamples);

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
