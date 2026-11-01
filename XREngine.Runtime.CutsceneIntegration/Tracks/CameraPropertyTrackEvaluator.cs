using XREngine.Rendering;
using XREngine.Scene;

namespace XREngine.Cutscenes;

/// <summary>
/// Writes the vertical field of view of the cameras that camera property tracks bind.
/// The weights follow the rules of the transform evaluator: a total above one normalizes the weights,
/// and a total below one blends from the original value. The evaluator changes only perspective cameras.
/// </summary>
public sealed class CameraPropertyTrackEvaluator : ICutsceneTrackEvaluator
{
    private readonly bool[] _written;
    private readonly bool[] _touched;
    private readonly CutsceneClipTypeReporter _reporter;
    private readonly List<CutsceneCompileDiagnostic> _diagnostics;
    private readonly HashSet<int> _reportedSlots = [];

    /// <summary>Creates an evaluator with storage for each binding slot.</summary>
    /// <param name="slotCount">The number of binding slots of the cutscene.</param>
    /// <param name="diagnostics">The list that receives the problems that the evaluator finds.</param>
    public CameraPropertyTrackEvaluator(int slotCount, List<CutsceneCompileDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        _written = new bool[slotCount];
        _touched = new bool[slotCount];
        _diagnostics = diagnostics;
        _reporter = new CutsceneClipTypeReporter(diagnostics);
    }

    /// <inheritdoc/>
    public void Evaluate(in CutsceneEvaluationContext context, ReadOnlySpan<CutsceneActiveClip> clips)
    {
        Array.Clear(_touched);

        int start = 0;
        while (start < clips.Length)
        {
            int trackIndex = clips[start].TrackIndex;
            int end = start + 1;
            while (end < clips.Length && clips[end].TrackIndex == trackIndex)
                end++;
            EvaluateTrack(in context, trackIndex, clips.Slice(start, end - start));
            start = end;
        }

        // A camera that was written before and has no clip now goes back to its original value.
        for (int slot = 0; slot < _written.Length; slot++)
        {
            if (!_written[slot] || _touched[slot])
                continue;
            context.Snapshot.ApplyFieldOfView(slot);
            _written[slot] = false;
        }
    }

    /// <inheritdoc/>
    public void Reset()
    {
        Array.Clear(_written);
        Array.Clear(_touched);
    }

    private void EvaluateTrack(in CutsceneEvaluationContext context, int trackIndex, ReadOnlySpan<CutsceneActiveClip> clips)
    {
        CutsceneTrack track = context.Compiled.Asset.Tracks[trackIndex];
        if (track is not CameraPropertyTrack)
            return;

        int slot = track.BindingSlot;
        if ((uint)slot >= (uint)_written.Length || !context.Bindings.IsLive(slot))
            return;
        SceneNode? node = context.Bindings.GetNode(slot);
        if (node is null)
            return;

        float totalWeight = 0f;
        for (int i = 0; i < clips.Length; i++)
        {
            CutsceneActiveClip active = clips[i];
            CutsceneClip source = track.Clips[active.ClipIndex];
            if (source is not CameraPropertyClip)
            {
                _reporter.Report(trackIndex, active.ClipIndex, track.Name, nameof(CameraPropertyClip), source);
                continue;
            }
            if (active.Weight > 0f)
                totalWeight += active.Weight;
        }
        if (totalWeight <= 0f)
            return;

        XRPerspectiveCameraParameters? parameters = CutsceneCameraLookup.FindPerspective(node);
        if (parameters is null)
        {
            if (_reportedSlots.Add(slot))
                Report(trackIndex, slot, node);
            return;
        }

        context.Snapshot.CaptureFieldOfView(slot);
        if (!context.Snapshot.TryGetFieldOfView(slot, out float originalFieldOfView))
            return;

        float divisor = MathF.Max(totalWeight, 1f);
        float fieldOfView = 0f;
        for (int i = 0; i < clips.Length; i++)
        {
            CutsceneActiveClip active = clips[i];
            if (track.Clips[active.ClipIndex] is not CameraPropertyClip clip || active.Weight <= 0f)
                continue;
            fieldOfView += clip.FieldOfView.Sample(active.LocalTime) * (active.Weight / divisor);
        }
        if (totalWeight < 1f)
            fieldOfView += originalFieldOfView * (1f - totalWeight);

        parameters.VerticalFieldOfView = fieldOfView;
        _touched[slot] = true;
        _written[slot] = true;
    }

    private void Report(int trackIndex, int slot, SceneNode node)
    {
        string message = $"The node '{node.Name}' of binding slot {slot} has no perspective camera. The camera property track cannot change its field of view.";
        _diagnostics.Add(new CutsceneCompileDiagnostic(trackIndex, -1, message));
        Debug.LogWarning(ELogCategory.Animation, "Cutscene evaluation: {0}", message);
    }
}
