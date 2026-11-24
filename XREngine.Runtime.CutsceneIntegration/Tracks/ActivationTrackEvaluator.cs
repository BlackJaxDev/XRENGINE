using XREngine.Scene;

namespace XREngine.Cutscenes;

/// <summary>
/// Sets a bound node to active while at least one clip of an activation track is active.
/// A node that has no active clip but had one before gets its original activation back.
/// </summary>
public sealed class ActivationTrackEvaluator : ICutsceneTrackEvaluator
{
    private readonly bool[] _written;
    private readonly bool[] _touched;
    private readonly CutsceneClipTypeReporter _reporter;

    /// <summary>Creates an evaluator with storage for each binding slot.</summary>
    /// <param name="slotCount">The number of binding slots of the cutscene.</param>
    /// <param name="diagnostics">The list that receives the problems that the evaluator finds.</param>
    public ActivationTrackEvaluator(int slotCount, List<CutsceneCompileDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        _written = new bool[slotCount];
        _touched = new bool[slotCount];
        _reporter = new CutsceneClipTypeReporter(diagnostics);
    }

    /// <inheritdoc/>
    public void Evaluate(in CutsceneEvaluationContext context, ReadOnlySpan<CutsceneActiveClip> clips)
    {
        Array.Clear(_touched);

        for (int i = 0; i < clips.Length; i++)
        {
            CutsceneActiveClip active = clips[i];
            CutsceneTrack track = context.Compiled.Asset.Tracks[active.TrackIndex];
            if (track is not ActivationTrack)
                continue;

            CutsceneClip clip = track.Clips[active.ClipIndex];
            if (clip is not ActivationClip)
            {
                _reporter.Report(active.TrackIndex, active.ClipIndex, track.Name, nameof(ActivationClip), clip);
                continue;
            }

            int slot = track.BindingSlot;
            if ((uint)slot >= (uint)_written.Length || !context.Bindings.IsLive(slot))
                continue;
            if (context.Bindings.GetNode(slot) is not SceneNode node)
                continue;

            context.Snapshot.CaptureActive(slot);
            node.IsActiveSelf = true;
            _touched[slot] = true;
            _written[slot] = true;
        }

        // A target that was written before and has no clip now goes back to its original activation.
        for (int slot = 0; slot < _written.Length; slot++)
        {
            if (!_written[slot] || _touched[slot])
                continue;
            context.Snapshot.ApplyActive(slot);
            _written[slot] = false;
        }
    }

    /// <inheritdoc/>
    public void Reset()
    {
        Array.Clear(_written);
        Array.Clear(_touched);
    }
}
