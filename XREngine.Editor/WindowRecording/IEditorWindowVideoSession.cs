using XREngine.Rendering;

namespace XREngine.Editor.WindowRecording;

/// <summary>
/// Common lifecycle contract for an active editor window video recording session backed by
/// either OS window capture or in-process renderer GPU readback.
/// </summary>
internal interface IEditorWindowVideoSession
{
    /// <summary>Gets the MP4 destination path that this session writes.</summary>
    string OutputPath { get; }

    /// <summary>Gets the original window title saved before recording started.</summary>
    string BaseTitle { get; }

    /// <summary>Gets the target editor window.</summary>
    XRWindow Window { get; }

    /// <summary>Gets the capture backend running this session.</summary>
    EEditorWindowRecordingBackend Backend { get; }

    /// <summary>Gets the normalized options used to configure this session.</summary>
    EditorWindowRecordingOptions Options { get; }

    /// <summary>Gets the elapsed recording duration.</summary>
    TimeSpan Elapsed { get; }

    /// <summary>Gets the number of source frames captured by the engine (zero for external OS capture).</summary>
    long CapturedFrameCount { get; }

    /// <summary>Gets the number of output video frames written to ffmpeg (zero for external OS capture).</summary>
    long WrittenVideoFrameCount { get; }

    /// <summary>Gets the number of frames skipped because the readback queue was full.</summary>
    long DroppedReadbackCount { get; }

    /// <summary>Raised when the recording stops unexpectedly due to an encoder or capture failure.</summary>
    event Action<IEditorWindowVideoSession, string>? Failed;

    /// <summary>Raised when a bounded recording finishes automatically after reaching <see cref="EditorWindowRecordingOptions.MaxDuration"/>.</summary>
    event Action<IEditorWindowVideoSession>? Completed;

    /// <summary>Starts status updates, unexpected-exit monitoring, and optional duration limits.</summary>
    void Begin();

    /// <summary>Stops capture, flushes ffmpeg so the MP4 index is written, and restores the window title.</summary>
    bool Stop(out string? failure);
}
