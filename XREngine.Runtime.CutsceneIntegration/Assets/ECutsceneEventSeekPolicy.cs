namespace XREngine.Cutscenes;

/// <summary>Tells an event track what to do with the events that a seek moves across.</summary>
public enum ECutsceneEventSeekPolicy
{
    /// <summary>A seek fires no event.</summary>
    Skip,

    /// <summary>A seek fires only the last crossed event of each key.</summary>
    FireLatest,

    /// <summary>A seek fires all crossed events in time order.</summary>
    FireAll,
}
