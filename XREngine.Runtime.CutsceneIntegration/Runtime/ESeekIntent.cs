namespace XREngine.Cutscenes;

/// <summary>Tells the player and the planner what kind of seek a caller makes.</summary>
public enum ESeekIntent
{
    /// <summary>A drag on the timeline. More seeks are likely to follow.</summary>
    Scrub,

    /// <summary>A single jump to a new position. No more movement is expected.</summary>
    Jump,
}
