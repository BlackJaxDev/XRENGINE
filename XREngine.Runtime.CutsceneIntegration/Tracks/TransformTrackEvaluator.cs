using System.Numerics;
using XREngine.Scene.Transforms;

namespace XREngine.Cutscenes;

/// <summary>
/// Writes the local position, rotation, and scale of the nodes that transform tracks bind.
/// The total weight of the active clips of a track decides the result. A total of zero leaves the original value.
/// A total above one normalizes the weights. A total below one blends from the original value to the clip blend
/// by the total, with lerp for position and scale and slerp for rotation.
/// The clip blend of rotation is a normalized weighted sum, with a flip of each rotation into the hemisphere of the first one.
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

        // Pass 1: find the total weight of the valid clips on the track.
        float totalWeight = 0f;
        for (int i = 0; i < clips.Length; i++)
        {
            CutsceneActiveClip active = clips[i];
            CutsceneClip source = track.Clips[active.ClipIndex];
            if (source is not TransformTrackClip)
            {
                _reporter.Report(trackIndex, active.ClipIndex, track.Name, nameof(TransformTrackClip), source);
                continue;
            }
            if (active.Weight > 0f)
                totalWeight += active.Weight;
        }

        // A track with no weight leaves the target at its original value.
        if (totalWeight <= 0f)
            return;

        // The original value must be recorded before the first write.
        context.Snapshot.CaptureTransform(slot);
        if (!context.Snapshot.TryGetTransform(slot, out Vector3 originalPosition, out Quaternion originalRotation, out Vector3 originalScale))
            return;

        // Pass 2: blend. Weights above one in total are normalized. Weights below one in total
        // blend from the original value to the clip blend by the total weight.
        float divisor = MathF.Max(totalWeight, 1f);
        Vector3 position = Vector3.Zero;
        Vector3 scale = Vector3.Zero;
        Quaternion rotation = default;
        Quaternion firstRotation = Quaternion.Identity;
        bool hasRotation = false;

        for (int i = 0; i < clips.Length; i++)
        {
            CutsceneActiveClip active = clips[i];
            if (track.Clips[active.ClipIndex] is not TransformTrackClip clip || active.Weight <= 0f)
                continue;

            float weight = active.Weight / divisor;
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
            rotation += sampled * (active.Weight / totalWeight);
        }

        // The accumulated rotation is a convex blend of rotations in one hemisphere, so it is never near zero.
        Quaternion blendedRotation = Quaternion.Normalize(rotation);
        if (totalWeight < 1f)
        {
            position += originalPosition * (1f - totalWeight);
            scale += originalScale * (1f - totalWeight);
            blendedRotation = Quaternion.Slerp(originalRotation, blendedRotation, totalWeight);
        }

        transform.Translation = position;
        transform.Scale = scale;
        transform.Rotation = blendedRotation;

        _touched[slot] = true;
        _written[slot] = true;
    }
}
