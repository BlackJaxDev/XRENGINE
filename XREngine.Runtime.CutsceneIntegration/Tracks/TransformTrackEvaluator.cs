using System.Numerics;
using XREngine.Scene.Transforms;

namespace XREngine.Cutscenes;

/// <summary>
/// Writes the local position, rotation, and scale of the nodes that transform tracks bind.
/// Position and scale use the weighted sum of the active clips of a track.
/// Rotation uses a normalized weighted sum, with a flip of each rotation into the hemisphere of the first one.
/// A track with a total weight of zero does not write.
/// </summary>
public sealed class TransformTrackEvaluator : ICutsceneTrackEvaluator
{
    private readonly bool[] _written;
    private readonly bool[] _touched;
    private readonly CutsceneClipTypeReporter _reporter;

    /// <summary>Creates an evaluator with storage for each binding slot.</summary>
    /// <param name="slotCount">The number of binding slots of the cutscene.</param>
    /// <param name="diagnostics">The list that receives the problems that the evaluator finds.</param>
    public TransformTrackEvaluator(int slotCount, List<CutsceneCompileDiagnostic> diagnostics)
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

        // A target that was written before and has no clip now goes back to its original value.
        for (int slot = 0; slot < _written.Length; slot++)
        {
            if (!_written[slot] || _touched[slot])
                continue;
            context.Snapshot.ApplyTransform(slot);
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
        if (track is not TransformTrack)
            return;

        int slot = track.BindingSlot;
        if ((uint)slot >= (uint)_written.Length || !context.Bindings.IsLive(slot))
            return;
        if (context.Bindings.GetNode(slot)?.Transform is not Transform transform)
            return;

        float totalWeight = 0f;
        Vector3 position = Vector3.Zero;
        Vector3 scale = Vector3.Zero;
        Quaternion rotation = default;
        Quaternion firstRotation = Quaternion.Identity;
        bool hasRotation = false;

        for (int i = 0; i < clips.Length; i++)
        {
            CutsceneActiveClip active = clips[i];
            CutsceneClip source = track.Clips[active.ClipIndex];
            if (source is not TransformTrackClip clip)
            {
                _reporter.Report(trackIndex, active.ClipIndex, track.Name, nameof(TransformTrackClip), source);
                continue;
            }

            float weight = active.Weight;
            if (weight <= 0f)
                continue;

            totalWeight += weight;
            position += clip.Position.Sample(active.LocalTime) * weight;
            scale += clip.Scale.Sample(active.LocalTime) * weight;

            Quaternion sampled = clip.Rotation.Sample(active.LocalTime);
            if (!hasRotation)
            {
                firstRotation = sampled;
                hasRotation = true;
            }
            else if (Quaternion.Dot(sampled, firstRotation) < 0f)
                sampled = Quaternion.Negate(sampled);
            rotation += sampled * weight;
        }

        if (totalWeight <= 0f)
            return;

        context.Snapshot.CaptureTransform(slot);
        transform.Translation = position;
        transform.Scale = scale;
        if (rotation.LengthSquared() > 1e-12f)
            transform.Rotation = Quaternion.Normalize(rotation);

        _touched[slot] = true;
        _written[slot] = true;
    }
}
