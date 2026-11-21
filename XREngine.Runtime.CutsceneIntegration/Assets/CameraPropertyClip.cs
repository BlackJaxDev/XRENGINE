namespace XREngine.Cutscenes;

/// <summary>A clip that sets camera properties from curves.</summary>
public class CameraPropertyClip : CutsceneClip
{
    /// <summary>The vertical field of view curve in degrees. Its time is the clip time.</summary>
    public CutsceneFloatCurve FieldOfView { get; set; } = new();
}
