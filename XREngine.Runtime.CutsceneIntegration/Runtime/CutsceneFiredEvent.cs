namespace XREngine.Cutscenes;

/// <summary>An event that the player fired.</summary>
/// <param name="TrackIndex">The index of the event track in the cutscene asset.</param>
/// <param name="EventIndex">The index of the event in <see cref="EventTrack.Events"/>.</param>
/// <param name="Event">The event.</param>
/// <param name="FromSeek">True if a seek fired the event, false if forward playback crossed it.</param>
public readonly record struct CutsceneFiredEvent(int TrackIndex, int EventIndex, CutsceneEvent Event, bool FromSeek);
