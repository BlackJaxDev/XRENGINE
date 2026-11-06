namespace XREngine.Cutscenes;

/// <summary>
/// A clip that plays one audio file. The clip time is the time in the file, so <see cref="CutsceneClip.ClipOffset"/>
/// skips the start of the file. The speed must be one and the extrapolation must not loop.
/// The clip has no sound outside its duration, also with a hold extrapolation.
/// </summary>
public class AudioTrackClip : CutsceneClip
{
    private string _sourcePath = string.Empty;
    private float _volume = 1.0f;

    /// <summary>
    /// The path of the audio file to stream. The supported formats are Ogg Vorbis and WAV.
    /// A relative path starts at the folder of the cutscene asset file. If the asset has no file, it starts at the current folder.
    /// </summary>
    public string SourcePath
    {
        get => _sourcePath;
        set => SetField(ref _sourcePath, value);
    }

    /// <summary>The volume of the clip. It multiplies the gain of the audio source component and the blend weight.</summary>
    public float Volume
    {
        get => _volume;
        set => SetField(ref _volume, value);
    }
}
