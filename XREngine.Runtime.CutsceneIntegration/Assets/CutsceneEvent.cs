using XREngine.Data.Core;

namespace XREngine.Cutscenes;

/// <summary>A point in time on an event track. Gameplay code reads its key and payload when it fires.</summary>
public class CutsceneEvent : XRBase
{
    private CutsceneTime _time;
    private string _key = string.Empty;
    private string _payload = string.Empty;

    /// <summary>The cutscene time of the event.</summary>
    public CutsceneTime Time
    {
        get => _time;
        set => SetField(ref _time, value);
    }

    /// <summary>
    /// The name that gameplay code uses to identify the event. The <see cref="ECutsceneEventSeekPolicy.FireLatest"/>
    /// policy fires only the last crossed event of each key.
    /// </summary>
    public string Key
    {
        get => _key;
        set => SetField(ref _key, value);
    }

    /// <summary>Data for the gameplay code. The cutscene does not read it.</summary>
    public string Payload
    {
        get => _payload;
        set => SetField(ref _payload, value);
    }
}
