namespace XREngine.Cutscenes;

/// <summary>
/// A clip that shows one local video file. The clip time is the time in the file, so <see cref="CutsceneClip.ClipOffset"/>
/// skips the start of the file. The file must support a seek: a live stream or an HLS playlist gives one diagnostic and
/// shows nothing.
/// </summary>
public class VideoTrackClip : CutsceneClip
{
    private string _sourcePath = string.Empty;

    /// <summary>
    /// The path of the video file. Any container and codec that the FFmpeg media backend decodes is supported.
    /// A relative path starts at the folder of the cutscene asset file. If the asset has no file, it starts at the current folder.
    /// </summary>
    public string SourcePath
    {
        get => _sourcePath;
        set => SetField(ref _sourcePath, value);
    }
}
