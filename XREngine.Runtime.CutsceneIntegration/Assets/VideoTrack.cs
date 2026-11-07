namespace XREngine.Cutscenes;

/// <summary>
/// A track that shows a local video file on the <c>IVideoFrameTarget</c> component (for example a <c>UIVideoComponent</c>)
/// on its bound node. Its clips are <see cref="VideoTrackClip"/> instances. The track shows the frame of the cutscene time:
/// during a drag it shows the nearest keyframe at or before the time, and when the playhead stops it decodes the exact
/// frame. One clip of a track shows at a time. The audio of the file does not play; use an <see cref="AudioTrack"/>.
/// </summary>
public class VideoTrack : CutsceneTrack
{
    private int _frameCacheCapacity = VideoFrameCache.DefaultCapacity;
    private long _frameCacheBudgetBytes = VideoFrameCache.DefaultBudgetBytes;

    /// <summary>
    /// The number of decoded frames that the track keeps for each clip. A return to a frame in the cache shows it with no
    /// decode. A value below one uses one. Each frame uses <c>width * height * 3</c> bytes; <see cref="FrameCacheBudgetBytes"/>
    /// also limits the cache.
    /// </summary>
    public int FrameCacheCapacity
    {
        get => _frameCacheCapacity;
        set => SetField(ref _frameCacheCapacity, value);
    }

    /// <summary>
    /// The largest number of bytes of decoded frames that the track keeps for each clip (default 64 MiB). The cache holds
    /// at most <see cref="FrameCacheCapacity"/> frames and at most this many bytes, and always one frame. A 1080p RGB frame
    /// uses 5.9 MiB, and a 4K RGB frame uses 23.7 MiB. Slot buffers are made only when frames come.
    /// </summary>
    public long FrameCacheBudgetBytes
    {
        get => _frameCacheBudgetBytes;
        set => SetField(ref _frameCacheBudgetBytes, value);
    }
}
