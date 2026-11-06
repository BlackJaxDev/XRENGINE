namespace XREngine.Cutscenes;

/// <summary>
/// A track of events. Events are side effects for gameplay code: they never change state that a seek or a scrub needs.
/// Forward playback fires each event that it crosses. A seek fires events only by <see cref="SeekPolicy"/>.
/// Reverse playback fires no event. An event track has no clips and does not use its binding slot.
/// </summary>
public class EventTrack : CutsceneTrack
{
    private ECutsceneEventSeekPolicy _seekPolicy = ECutsceneEventSeekPolicy.Skip;
    private bool _fireInPreview;
    private bool _fireDuringScrub;

    /// <summary>What a forward seek does with the events that it crosses. A backward seek fires no event.</summary>
    public ECutsceneEventSeekPolicy SeekPolicy
    {
        get => _seekPolicy;
        set => SetField(ref _seekPolicy, value);
    }

    /// <summary>
    /// True to fire the events in the editor preview. The default is false, so gameplay code does not run
    /// while an author moves the playhead.
    /// </summary>
    public bool FireInPreview
    {
        get => _fireInPreview;
        set => SetField(ref _fireInPreview, value);
    }

    /// <summary>
    /// True to let a forward scrub step fire events by <see cref="SeekPolicy"/>. The default is false, so a scrub fires no event.
    /// </summary>
    public bool FireDuringScrub
    {
        get => _fireDuringScrub;
        set => SetField(ref _fireDuringScrub, value);
    }

    /// <summary>The events of the track. The order in the list does not matter. Events fire in time order.</summary>
    public List<CutsceneEvent> Events { get; set; } = [];
}
