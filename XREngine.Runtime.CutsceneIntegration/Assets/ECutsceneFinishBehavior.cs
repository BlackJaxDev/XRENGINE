namespace XREngine.Cutscenes;

/// <summary>Describes what a track does to its target when the cutscene ends.</summary>
public enum ECutsceneFinishBehavior
{
    /// <summary>Put the target back in the state it had before the cutscene.</summary>
    RestoreState,
    /// <summary>Leave the target in the state that the cutscene set.</summary>
    KeepState,
}
