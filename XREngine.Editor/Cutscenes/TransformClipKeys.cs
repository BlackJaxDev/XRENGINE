using System.Numerics;
using XREngine.Cutscenes;

namespace XREngine.Editor.Cutscenes;

/// <summary>
/// The key arrays of a transform clip. An edit replaces these arrays and does not change their content,
/// so an undo step can hold references to them.
/// </summary>
public readonly record struct TransformClipKeys(
    CutsceneTime[] PositionTimes,
    Vector3[] PositionValues,
    CutsceneTime[] RotationTimes,
    Quaternion[] RotationValues,
    CutsceneTime[] ScaleTimes,
    Vector3[] ScaleValues)
{
    /// <summary>Reads the key arrays of a clip.</summary>
    /// <param name="clip">The clip to read.</param>
    /// <returns>The key arrays.</returns>
    public static TransformClipKeys Capture(TransformTrackClip clip)
        => new(clip.Position.Times, clip.Position.Values, clip.Rotation.Times, clip.Rotation.Values, clip.Scale.Times, clip.Scale.Values);

    /// <summary>Writes these key arrays to a clip.</summary>
    /// <param name="clip">The clip to change.</param>
    public void Apply(TransformTrackClip clip)
    {
        clip.Position.Times = PositionTimes;
        clip.Position.Values = PositionValues;
        clip.Rotation.Times = RotationTimes;
        clip.Rotation.Values = RotationValues;
        clip.Scale.Times = ScaleTimes;
        clip.Scale.Values = ScaleValues;
    }
}
