using XREngine.Animation;

namespace XREngine.Cutscenes;

/// <summary>
/// Plays the clips of animation tracks. Each binding slot has one <see cref="CutsceneAnimationBinding"/>,
/// which the evaluator builds when the cutscene opens, so the frame path makes no clip copies.
/// The total weight of the active clips of a slot decides the result. A total of zero means no clip: the
/// target keeps or gets back its original value. A total above one normalizes the weights. A total
/// below one blends from the original value to the clip blend by the total.
/// </summary>
public sealed class AnimationClipTrackEvaluator : ICutsceneTrackEvaluator
{
    private readonly CutsceneAnimationBinding?[] _bindings;
    private readonly int[]?[] _entryByTrackAndClip;
    private readonly CutsceneClipTypeReporter _reporter;

    /// <summary>Creates an evaluator, and makes and binds a copy of each clip.</summary>
    /// <param name="compiled">The compiled cutscene.</param>
    /// <param name="table">The table that holds the bound nodes.</param>
    /// <param name="diagnostics">The list that receives the problems that the evaluator finds.</param>
    public AnimationClipTrackEvaluator(CompiledCutscene compiled, CutsceneBindingTable table, List<CutsceneCompileDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(compiled);
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(diagnostics);

        _reporter = new CutsceneClipTypeReporter(diagnostics);
        _bindings = new CutsceneAnimationBinding?[table.SlotCount];

        List<CutsceneTrack> tracks = compiled.Asset.Tracks;
        _entryByTrackAndClip = new int[tracks.Count][];

        for (int slot = 0; slot < _bindings.Length; slot++)
        {
            if (table.GetNode(slot) is { } node)
                _bindings[slot] = CutsceneAnimationBinding.TryCreate(slot, node, tracks, _entryByTrackAndClip, diagnostics);
            else
                ReportTracksWithoutNode(slot, tracks, diagnostics);
        }
    }

    /// <inheritdoc/>
    public void Evaluate(in CutsceneEvaluationContext context, ReadOnlySpan<CutsceneActiveClip> clips)
    {
        for (int slot = 0; slot < _bindings.Length; slot++)
            _bindings[slot]?.BeginCollect();

        List<CutsceneTrack> tracks = context.Compiled.Asset.Tracks;
        for (int i = 0; i < clips.Length; i++)
        {
            CutsceneActiveClip active = clips[i];
            if (tracks[active.TrackIndex] is not AnimationClipTrack track)
                continue;

            CutsceneClip source = track.Clips[active.ClipIndex];
            if (source is not AnimationClipTrackClip)
            {
                _reporter.Report(active.TrackIndex, active.ClipIndex, track.Name, nameof(AnimationClipTrackClip), source);
                continue;
            }

            int slot = track.BindingSlot;
            if ((uint)slot >= (uint)_bindings.Length || _bindings[slot] is not { } binding)
                continue;

            int[]? entries = _entryByTrackAndClip[active.TrackIndex];
            int entry = entries is null ? -1 : entries[active.ClipIndex];
            if (entry >= 0)
                binding.Collect(entry, in active);
        }

        for (int slot = 0; slot < _bindings.Length; slot++)
        {
            if (_bindings[slot] is { } binding)
                binding.Evaluate(context.Bindings.GetNode(slot), context.Snapshot);
        }
    }

    /// <summary>
    /// Ends all clips for a stop. A target with the restore setting gets its original values back.
    /// Each lease ends, and the next evaluation captures new original values.
    /// </summary>
    /// <param name="forceAll">True to restore each target, whatever its finish behavior is.</param>
    /// <param name="snapshot">The store of the original root transforms.</param>
    public void RestoreForStop(bool forceAll, CutsceneStateSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        for (int slot = 0; slot < _bindings.Length; slot++)
            _bindings[slot]?.EndForStop(forceAll, snapshot);
    }

    private static void ReportTracksWithoutNode(int slot, List<CutsceneTrack> tracks, List<CutsceneCompileDiagnostic> diagnostics)
    {
        for (int trackIndex = 0; trackIndex < tracks.Count; trackIndex++)
        {
            if (tracks[trackIndex] is AnimationClipTrack track && track.BindingSlot == slot && track.Clips.Count > 0)
                diagnostics.Add(new CutsceneCompileDiagnostic(trackIndex, -1, $"The track '{track.Name}' has no node when the cutscene opens. Clip copies are made only at open, so its clips do not play. Call CutsceneDirectorComponent.SetRuntimeSlot before the open."));
        }
    }

    /// <inheritdoc/>
    public void Reset()
    {
        for (int slot = 0; slot < _bindings.Length; slot++)
            _bindings[slot]?.Forget();
    }
}
