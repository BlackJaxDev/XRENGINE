namespace XREngine.Cutscenes;

/// <summary>The transport state of a <see cref="CutscenePlayer"/>.</summary>
public enum ECutsceneTransportState
{
    /// <summary>The player is at the start and the targets have their original values.</summary>
    Stopped,

    /// <summary>The clock moves the position on each tick.</summary>
    Playing,

    /// <summary>The position is held. A seek still moves it.</summary>
    Paused,

    /// <summary>A scrub is in progress. The clock does not move the position.</summary>
    Scrubbing,
}
