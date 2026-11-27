namespace XREngine.Editor.WindowRecording;

/// <summary>
/// Selects the H.264 video encoder passed to ffmpeg during editor window recording.
/// </summary>
public enum EEditorWindowVideoEncoder
{
    /// <summary>
    /// Uses hardware NVIDIA NVENC (<c>h264_nvenc</c>) when supported by ffmpeg and the active GPU,
    /// otherwise falls back to CPU <c>libx264</c>.
    /// </summary>
    Auto = 0,

    /// <summary>
    /// Encodes with hardware NVIDIA NVENC (<c>h264_nvenc</c>) to avoid competing with CPU loading
    /// and render collection. Fails diagnostically when NVENC is unavailable.
    /// </summary>
    Nvenc = 1,

    /// <summary>
    /// Encodes on CPU with <c>libx264</c>.
    /// </summary>
    X264 = 2,
}
