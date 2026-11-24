namespace XREngine.Rendering.VideoStreaming;

/// <summary>The state of a seek request of a media stream session.</summary>
public enum EVideoSeekState
{
    /// <summary>The decoder has not finished the request yet.</summary>
    Pending = 0,

    /// <summary>The first frame of the new position is in the queue.</summary>
    Completed = 1,

    /// <summary>A newer request, a cancel, or a close replaced the request. Its frames are dropped.</summary>
    Cancelled = 2,

    /// <summary>The seek failed. <see cref="Interfaces.IMediaStreamSession.LastSeekError"/> tells why.</summary>
    Failed = 3,
}
