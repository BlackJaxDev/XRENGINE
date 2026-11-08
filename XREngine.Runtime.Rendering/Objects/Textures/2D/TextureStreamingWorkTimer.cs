using System.Diagnostics;

namespace XREngine.Rendering;

/// <summary>
/// Times one item of texture streaming work on the render thread without the garbage collection pauses in the window.
/// A GC pause stops every managed thread, so it is not a cost of the item. It allocates nothing.
/// </summary>
internal readonly struct TextureStreamingWorkTimer
{
    private readonly long _startTimestamp;
    private readonly TimeSpan _startGcPause;

    private TextureStreamingWorkTimer(long startTimestamp, TimeSpan startGcPause)
    {
        _startTimestamp = startTimestamp;
        _startGcPause = startGcPause;
    }

    /// <summary>Starts a timer.</summary>
    public static TextureStreamingWorkTimer Start()
        => new(Stopwatch.GetTimestamp(), GC.GetTotalPauseDuration());

    /// <summary>The elapsed time since <see cref="Start"/> less the GC pauses in it, in milliseconds.</summary>
    public double ElapsedWorkMilliseconds
    {
        get
        {
            double wall = Stopwatch.GetElapsedTime(_startTimestamp).TotalMilliseconds;
            double paused = (GC.GetTotalPauseDuration() - _startGcPause).TotalMilliseconds;
            return Math.Max(0.0, wall - paused);
        }
    }
}
