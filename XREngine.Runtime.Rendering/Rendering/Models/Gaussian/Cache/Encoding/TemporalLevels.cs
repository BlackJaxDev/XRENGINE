using System;

namespace XREngine.Rendering.Models.Gaussian.Cache.Encoding;

/// <summary>Defines the coarse-to-fine time levels of stored frames.</summary>
public static class TemporalLevels
{
    public const int Count = 4;

    /// <summary>Returns the temporal level of a frame. T0 is i%8==0, T1 is i%8==4, T2 is i%4==2, T3 is odd.</summary>
    public static byte Of(int frameIndex)
    {
        if ((frameIndex & 7) == 0)
            return 0;
        if ((frameIndex & 7) == 4)
            return 1;
        if ((frameIndex & 3) == 2)
            return 2;
        return 3;
    }

    /// <summary>Returns the distance to the prediction neighbours of a level: 8, 4, 2 or 1.</summary>
    public static int Step(byte level) => level switch
    {
        0 => 8,
        1 => 4,
        2 => 2,
        3 => 1,
        _ => throw new ArgumentOutOfRangeException(nameof(level)),
    };

    /// <summary>Returns the distance between two frames of the same level.</summary>
    public static int FrameStride(byte level) => level switch
    {
        0 => 8,
        1 => 8,
        2 => 4,
        3 => 2,
        _ => throw new ArgumentOutOfRangeException(nameof(level)),
    };

    /// <summary>Returns the first frame index of a level.</summary>
    public static int FirstFrame(byte level) => level switch
    {
        0 => 0,
        1 => 4,
        2 => 2,
        3 => 1,
        _ => throw new ArgumentOutOfRangeException(nameof(level)),
    };

    /// <summary>Returns the number of frames of a level in a cache of the given length.</summary>
    public static int FramesInLevel(byte level, int frameCount)
    {
        int first = FirstFrame(level);
        if (frameCount <= first)
            return 0;
        return ((frameCount - 1 - first) / FrameStride(level)) + 1;
    }

    /// <summary>
    /// Returns the later prediction neighbour. Loop playback wraps. Other modes use the earlier neighbour. Use this
    /// method for motion prediction only. Playback uses <see cref="NextBlendFrame"/>.
    /// </summary>
    public static int NextNeighbor(int i, int step, int frameCount, GaussianCacheLoopMode mode)
    {
        int b = i + step;
        if (b < frameCount)
            return b;
        return mode == GaussianCacheLoopMode.Loop ? b - frameCount : i - step;
    }

    /// <summary>
    /// Returns the second frame of the playback blend that starts at frame <paramref name="i"/>. Loop playback wraps to
    /// the start. The other modes hold frame <paramref name="i"/> when the later frame is after the last frame, because
    /// a blend to an earlier frame moves the pose back in time.
    /// </summary>
    public static int NextBlendFrame(int i, int step, int frameCount, GaussianCacheLoopMode mode)
    {
        int b = i + step;
        if (b < frameCount)
            return b;
        return mode == GaussianCacheLoopMode.Loop ? b - frameCount : i;
    }
}
