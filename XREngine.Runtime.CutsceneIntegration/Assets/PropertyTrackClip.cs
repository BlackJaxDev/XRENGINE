namespace XREngine.Cutscenes;

/// <summary>
/// A clip that sets the member of a property track from a curve. The evaluator reads the curve that matches
/// the type of the member and ignores the other curves.
/// </summary>
public class PropertyTrackClip : CutsceneClip
{
    /// <summary>The curve for a float member. Its time is the clip time.</summary>
    public CutsceneFloatCurve FloatCurve { get; set; } = new();

    /// <summary>The curve for a Vector3 member. Its time is the clip time.</summary>
    public CutsceneVector3Curve Vector3Curve { get; set; } = new();

    /// <summary>The curve for a Quaternion member. Its time is the clip time.</summary>
    public CutsceneQuaternionCurve QuaternionCurve { get; set; } = new();
}
