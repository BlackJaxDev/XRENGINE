namespace XREngine.Cutscenes;

/// <summary>
/// A track that plays audio files through the <c>AudioSourceComponent</c> on its bound node.
/// Its clips are AudioTrackClip instances. The track streams each clip from its file and keeps the
/// sources at the position of the cutscene time. One clip of a track plays at a time.
/// </summary>
public class AudioTrack : CutsceneTrack
{
    private bool _drivesClock = true;

    /// <summary>
    /// True to let this track correct the cutscene clock while the clock is in
    /// <see cref="ECutsceneClockMode.AudioDisciplined"/> mode. The first such track with an active clip corrects the clock.
    /// </summary>
    public bool DrivesClock
    {
        get => _drivesClock;
        set => SetField(ref _drivesClock, value);
    }
}
