using System.Collections.Generic;

namespace XREngine.Rendering.VideoStreaming;

public sealed class StreamOpenOptions
{
    public string? UserAgent { get; set; }
    public string? Referrer { get; set; }
    public IReadOnlyDictionary<string, string>? Headers { get; set; }
    public bool EnableReconnect { get; set; } = true;
    public bool EnableAdaptiveCatchUpPitch { get; set; } = true;
    public int OpenTimeoutMs { get; set; } = 15000;
    public int VideoQueueCapacity { get; set; } = 1;
    public int AudioQueueCapacity { get; set; } = 8;

    /// <summary>
    /// True to pace each video frame only by the clock that the caller passes to
    /// <see cref="Interfaces.IMediaStreamSession.TryDequeueVideoFrame"/>. Zero is then a valid time, and the session never
    /// uses wall time. The decoder waits for queue space instead of dropping frames, and it does not stop at the end of
    /// the file, so a later seek can move it back. The data of a dequeued frame stays valid until the next dequeue call,
    /// seek, or close, because the session uses its buffers again. A cutscene video track uses this mode.
    /// </summary>
    public bool ClockDrivenPresentation { get; set; }

    /// <summary>True to decode the audio stream. False ignores the audio of the file.</summary>
    public bool DecodeAudio { get; set; } = true;
}
