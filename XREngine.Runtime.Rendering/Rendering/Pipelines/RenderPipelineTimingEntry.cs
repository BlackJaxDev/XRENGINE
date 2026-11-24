namespace XREngine.Rendering;

/// <summary>GPU timing statistics of one profiling path of a pipeline.</summary>
/// <param name="LastMilliseconds">The GPU time of the newest resolved frame.</param>
/// <param name="AverageMilliseconds">The average GPU time for each frame since the profiler started.</param>
/// <param name="MinMilliseconds">The lowest GPU time of one frame.</param>
/// <param name="MaxMilliseconds">The highest GPU time of one frame.</param>
/// <param name="FrameCount">The number of frames that contributed.</param>
public readonly record struct RenderPipelineTimingEntry(
    double LastMilliseconds,
    double AverageMilliseconds,
    double MinMilliseconds,
    double MaxMilliseconds,
    int FrameCount);
