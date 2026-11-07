namespace XREngine.Rendering.VideoStreaming;

/// <summary>The accuracy of a seek of a media stream session.</summary>
public enum EVideoSeekMode
{
    /// <summary>
    /// Decodes only the keyframe at or before the target and then stops. The frame is never after the target.
    /// This is fast, so a drag of the playhead uses it.
    /// </summary>
    NearestKeyframe = 0,

    /// <summary>
    /// Decodes from the keyframe at or before the target to the frame that shows at the target, and then continues
    /// to decode forward. The first frame after the seek is the frame with the requested time.
    /// </summary>
    Exact = 1,
}
