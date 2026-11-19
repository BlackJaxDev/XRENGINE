namespace XREngine.Editor.CutsceneRecording;

/// <summary>
/// Selects the H.264 encoder that ffmpeg uses for the cutscene videos.
/// </summary>
public enum ECutsceneVideoEncoder
{
    /// <summary>NVIDIA NVENC hardware encoding through <c>h264_nvenc</c>. The command fails when NVENC is unavailable.</summary>
    Nvenc,

    /// <summary>CPU encoding through <c>libx264</c>.</summary>
    X264,
}
