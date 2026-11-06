namespace XREngine.Cutscenes;

/// <summary>
/// Writes the members that property tracks animate. Tracks that write the same member of the same slot share
/// one member binding, so their clips blend by weight. The weights follow the rules of the transform evaluator:
/// a total of zero leaves or gives back the original value, a total above one normalizes the weights, and a
/// total below one blends from the original value. The original value is restored through the state snapshot.
/// </summary>
public sealed class PropertyTrackEvaluator : ICutsceneTrackEvaluator
{
    private readonly CutscenePropertyBinding[] _bindings;
    private readonly CutscenePropertyBinding?[] _bindingOfTrack;
    private readonly CutsceneClipTypeReporter _reporter;

    /// <summary>Creates an evaluator with one member binding for each animated member.</summary>
    /// <param name="compiled">The compiled cutscene.</param>
    /// <param name="snapshot">The store of the original values. The evaluator registers each property track there.</param>
    /// <param name="diagnostics">The list that receives the problems that the evaluator finds.</param>
    public PropertyTrackEvaluator(CompiledCutscene compiled, CutsceneStateSnapshot snapshot, List<CutsceneCompileDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(compiled);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(diagnostics);
        _reporter = new CutsceneClipTypeReporter(diagnostics);

        List<CutsceneTrack> tracks = compiled.Asset.Tracks;
        _bindingOfTrack = new CutscenePropertyBinding?[tracks.Count];
        List<CutscenePropertyBinding> bindings = [];

        for (int trackIndex = 0; trackIndex < tracks.Count; trackIndex++)
        {
            if (tracks[trackIndex] is not PropertyTrack track || _bindingOfTrack[trackIndex] is not null)
                continue;

            // The first track of a member makes the binding. Later tracks of the same member share it.
            int clipCapacity = 0;
            for (int other = trackIndex; other < tracks.Count; other++)
                if (tracks[other] is PropertyTrack candidate && CutscenePropertyBinding.IsSameMember(track, candidate))
                    clipCapacity += candidate.Clips.Count;

            CutscenePropertyBinding binding = new(trackIndex, track, clipCapacity, diagnostics);
            bindings.Add(binding);
            for (int other = trackIndex; other < tracks.Count; other++)
            {
                if (tracks[other] is PropertyTrack candidate && binding.Matches(candidate))
                {
                    _bindingOfTrack[other] = binding;
                    snapshot.RegisterProperty(other, binding);
                }
            }
        }

        _bindings = [.. bindings];
    }

    /// <inheritdoc/>
    public void Evaluate(in CutsceneEvaluationContext context, ReadOnlySpan<CutsceneActiveClip> clips)
    {
        for (int i = 0; i < _bindings.Length; i++)
            _bindings[i].BeginCollect();

        List<CutsceneTrack> tracks = context.Compiled.Asset.Tracks;
        for (int i = 0; i < clips.Length; i++)
        {
            CutsceneActiveClip active = clips[i];
            if ((uint)active.TrackIndex >= (uint)_bindingOfTrack.Length || _bindingOfTrack[active.TrackIndex] is not { } binding)
                continue;

            CutsceneTrack track = tracks[active.TrackIndex];
            CutsceneClip source = track.Clips[active.ClipIndex];
            if (source is not PropertyTrackClip clip)
            {
                _reporter.Report(active.TrackIndex, active.ClipIndex, track.Name, nameof(PropertyTrackClip), source);
                continue;
            }
            binding.Collect(clip, in active);
        }

        for (int i = 0; i < _bindings.Length; i++)
            _bindings[i].Evaluate(context.Bindings);
    }

    /// <inheritdoc/>
    public void Reset()
    {
        for (int i = 0; i < _bindings.Length; i++)
            _bindings[i].ForgetWrite();
    }
}
