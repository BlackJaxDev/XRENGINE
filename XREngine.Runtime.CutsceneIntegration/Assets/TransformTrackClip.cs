using System.Numerics;

namespace XREngine.Cutscenes;

/// <summary>A clip that sets the local position, rotation, and scale of its target from curves.</summary>
public class TransformTrackClip : CutsceneClip
{
    /// <summary>The position curve. Its time is the clip time.</summary>
    public CutsceneVector3Curve Position { get; set; } = new();

    /// <summary>The rotation curve. Its time is the clip time.</summary>
    public CutsceneQuaternionCurve Rotation { get; set; } = new();

    /// <summary>The scale curve. Its time is the clip time. An empty curve gives a scale of one.</summary>
    public CutsceneVector3Curve Scale { get; set; } = new() { DefaultValue = Vector3.One };
}
