using System;

namespace XREngine
{
    /// <summary>
    /// Garbage-collector activity measured across one render-thread job.
    /// </summary>
    /// <param name="PauseDuration">Total GC pause time that occurred while the job ran.</param>
    /// <param name="Gen2Collections">Number of generation 2 collections that occurred while the job ran.</param>
    internal readonly record struct RenderThreadJobGcSample(TimeSpan PauseDuration, int Gen2Collections);
}
