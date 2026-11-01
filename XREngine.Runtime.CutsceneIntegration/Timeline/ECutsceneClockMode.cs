namespace XREngine.Cutscenes;

/// <summary>Selects what drives the position of a <see cref="CutsceneClock"/>.</summary>
public enum ECutsceneClockMode
{
    /// <summary>Wall time drives the clock through <see cref="CutsceneClock.Advance"/>.</summary>
    Wall,

    /// <summary>Wall time drives the clock. The audio position corrects it through <see cref="CutsceneClock.Discipline"/>.</summary>
    AudioDisciplined,

    /// <summary>Only <see cref="CutsceneClock.SetPosition"/> changes the position.</summary>
    Manual,
}
