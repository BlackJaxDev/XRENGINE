using System.Diagnostics;

namespace XREngine.Rendering;

/// <summary>
/// Finds render frame boundaries for a texture streaming controller that the host does not call each frame. The first
/// call in a new render frame closes the previous sampled frame. A frame time is known only when exactly one render frame
/// completed between two sampled frames; a longer gap gives an average idle frame instead. It allocates nothing.
/// The caller serializes the calls.
/// </summary>
internal sealed class TextureStreamingFrameSampler
{
    // A gap of at least this many render frames gives one idle sample: the one sampled frame in it is at most a quarter.
    private const long MinimumIdleGapFrames = 4L;

    private long _frameKey = long.MinValue;
    private long _frameRenderCalls;
    private long _frameRenderTicks;

    /// <summary>
    /// Advances to the current render frame.
    /// </summary>
    /// <param name="closed">The measurements of the frame that closed. Valid only when the method returns true.</param>
    /// <returns>True when a new render frame started and an earlier sampled frame closed.</returns>
    public bool TryAdvance(out TextureStreamingClosedFrame closed)
    {
        closed = default;
        long frameKey = RuntimeRenderingHostServices.FrameTiming.LastRenderTimestampTicks;
        if (frameKey == _frameKey)
            return false;

        FrameLifecyclePhaseTotal render = RuntimeEngine.Rendering.Stats.FrameLifecycle.CapturePhaseTotals().Render;
        bool hadFrame = _frameKey != long.MinValue;
        if (hadFrame)
        {
            double frameMilliseconds = double.NaN;
            double busyMilliseconds = double.NaN;
            double idleFrameMilliseconds = double.NaN;
            double idleBusyMilliseconds = double.NaN;
            long completedFrames = render.Calls - _frameRenderCalls;
            double elapsedMilliseconds = (frameKey - _frameKey) * 1000.0 / Stopwatch.Frequency;
            double busyTotalMilliseconds = (render.StopwatchTicks - _frameRenderTicks) * 1000.0 / Stopwatch.Frequency;
            if (completedFrames == 1L && frameKey > _frameKey)
            {
                frameMilliseconds = elapsedMilliseconds;
                busyMilliseconds = busyTotalMilliseconds;
            }
            else if (completedFrames >= MinimumIdleGapFrames && frameKey > _frameKey)
            {
                idleFrameMilliseconds = elapsedMilliseconds / completedFrames;
                idleBusyMilliseconds = busyTotalMilliseconds / completedFrames;
            }

            closed = new TextureStreamingClosedFrame(frameMilliseconds, busyMilliseconds, idleFrameMilliseconds, idleBusyMilliseconds);
        }

        _frameKey = frameKey;
        _frameRenderCalls = render.Calls;
        _frameRenderTicks = render.StopwatchTicks;
        return hadFrame;
    }

    /// <summary>Gets the target frame period from the host, or zero when the frame rate has no explicit target.</summary>
    public static double ResolveTargetFrameMilliseconds()
    {
        float frequency = RuntimeRenderingHostServices.FrameTiming.TargetRenderFrequency;
        return frequency > 0.0f ? 1000.0 / frequency : 0.0;
    }
}
