using XREngine.Components;
using XREngine.Scene;

namespace XREngine.Cutscenes;

/// <summary>
/// Selects the active camera of a cutscene from the camera cut tracks.
/// The active camera is the camera of the clip with the highest weight. If two clips have the same weight,
/// the clip with the latest start wins. The result is sampled state, so a seek and a play to the same time give the same camera.
/// </summary>
public sealed class CameraCutTrackEvaluator : ICutsceneTrackEvaluator
{
    private readonly CutsceneClipTypeReporter _reporter;
    private readonly List<CutsceneCompileDiagnostic> _diagnostics;
    private readonly HashSet<int> _reportedSlots = [];

    /// <summary>Creates an evaluator.</summary>
    /// <param name="diagnostics">The list that receives the problems that the evaluator finds.</param>
    public CameraCutTrackEvaluator(List<CutsceneCompileDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        _diagnostics = diagnostics;
        _reporter = new CutsceneClipTypeReporter(diagnostics);
    }

    /// <summary>The binding slot of the selected camera, or -1 if no clip selects a camera.</summary>
    public int ActiveSlot { get; private set; } = -1;

    /// <summary>
    /// The camera component of the selected slot. It is null if no clip is active
    /// or if the bound node has no camera component.
    /// </summary>
    public CameraComponent? ActiveCamera { get; private set; }

    /// <inheritdoc/>
    public void Evaluate(in CutsceneEvaluationContext context, ReadOnlySpan<CutsceneActiveClip> clips)
    {
        bool found = false;
        int bestSlot = -1;
        float bestWeight = 0f;
        CutsceneTime bestStart = default;
        int bestTrack = -1;
        int bestClip = -1;

        for (int i = 0; i < clips.Length; i++)
        {
            CutsceneActiveClip active = clips[i];
            CutsceneTrack track = context.Compiled.Asset.Tracks[active.TrackIndex];
            if (track is not CameraCutTrack)
                continue;

            CutsceneClip source = track.Clips[active.ClipIndex];
            if (source is not CameraCutClip clip)
            {
                _reporter.Report(active.TrackIndex, active.ClipIndex, track.Name, nameof(CameraCutClip), source);
                continue;
            }
            if (active.Weight <= 0f)
                continue;

            bool better = !found
                || active.Weight > bestWeight
                || (active.Weight == bestWeight && clip.Start >= bestStart);
            if (!better)
                continue;

            found = true;
            bestSlot = clip.CameraBindingSlot;
            bestWeight = active.Weight;
            bestStart = clip.Start;
            bestTrack = active.TrackIndex;
            bestClip = active.ClipIndex;
        }

        if (!found)
        {
            ActiveSlot = -1;
            ActiveCamera = null;
            return;
        }

        ActiveSlot = bestSlot;
        SceneNode? node = context.Bindings.GetNode(bestSlot);
        CameraComponent? camera = CutsceneCameraLookup.FindCamera(node);
        if (camera is null && node is not null && _reportedSlots.Add(bestSlot))
            Report(bestTrack, bestClip, bestSlot, node);
        ActiveCamera = camera;
    }

    /// <inheritdoc/>
    public void Reset()
    {
        ActiveSlot = -1;
        ActiveCamera = null;
    }

    private void Report(int trackIndex, int clipIndex, int slot, SceneNode node)
    {
        string message = $"The node '{node.Name}' of binding slot {slot} has no camera component. The camera cut clip {clipIndex} cannot select it.";
        _diagnostics.Add(new CutsceneCompileDiagnostic(trackIndex, clipIndex, message));
        Debug.LogWarning(ELogCategory.Animation, "Cutscene evaluation: {0}", message);
    }
}
