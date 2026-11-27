namespace XREngine.Editor.WindowRecording;

/// <summary>
/// Selects which capture source feeds frames to ffmpeg during an editor window recording.
/// </summary>
public enum EEditorWindowRecordingBackend
{
    /// <summary>
    /// Tries Windows Graphics Capture first and falls back to in-process renderer GPU readback
    /// when OS window capture is unavailable (for example on a locked desktop or without
    /// <c>gfxcapture</c>).
    /// </summary>
    Auto = 0,

    /// <summary>
    /// Captures the native window through ffmpeg's Windows Graphics Capture (<c>gfxcapture</c>)
    /// filter with no engine readback cost.
    /// </summary>
    WindowsGraphicsCapture = 1,

    /// <summary>
    /// Captures composited frames directly inside the renderer via GPU readback and pipes raw
    /// pixels to ffmpeg. Works while the OS desktop is locked or in headless/remote sessions.
    /// </summary>
    RendererReadback = 2,
}
