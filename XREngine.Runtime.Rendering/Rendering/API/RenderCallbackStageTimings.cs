using System.Diagnostics;

namespace XREngine.Rendering;

/// <summary>
/// Always-on cumulative render-thread time of the main parts of <c>XRWindow.RenderCallback</c>,
/// summed over all windows. Capture twice inside a timed window and divide each difference by the
/// difference of <see cref="RenderCallbackStageTimingSnapshot.Calls"/> to get milliseconds per frame.
/// </summary>
/// <remarks>
/// Only the render thread writes the sums, so the writes need no interlocked operation, and a
/// 64-bit read is not torn. The cost is a few timestamp reads per frame.
/// </remarks>
public static class RenderCallbackStageTimings
{
    internal enum Stage
    {
        Total,
        PreRender,
        ViewportsCallback,
        Viewports,
        PostRender,
        RendererWindow,
        PostViewportsCallback,
        MainThreadJobs,
        Count,
    }

    private static readonly long[] s_ticks = new long[(int)Stage.Count];
    private static long s_calls;

    /// <summary>Adds the time since <paramref name="startTicks"/> to one stage.</summary>
    internal static void Add(Stage stage, long startTicks)
        => s_ticks[(int)stage] += Stopwatch.GetTimestamp() - startTicks;

    /// <summary>Counts one render callback that reached the frame body.</summary>
    internal static void CountCall() => ++s_calls;

    /// <summary>Captures the cumulative stage times in milliseconds.</summary>
    public static RenderCallbackStageTimingSnapshot Capture()
    {
        static double Ms(Stage stage) => Volatile.Read(ref s_ticks[(int)stage]) * 1000.0 / Stopwatch.Frequency;
        return new(Volatile.Read(ref s_calls), Ms(Stage.Total), Ms(Stage.PreRender),
            Ms(Stage.ViewportsCallback), Ms(Stage.Viewports), Ms(Stage.PostRender),
            Ms(Stage.RendererWindow), Ms(Stage.PostViewportsCallback), Ms(Stage.MainThreadJobs));
    }
}
