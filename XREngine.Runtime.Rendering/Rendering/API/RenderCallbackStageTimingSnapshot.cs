namespace XREngine.Rendering;

/// <summary>
/// Cumulative render-thread milliseconds of the main parts of the window render callback.
/// <see cref="TotalMilliseconds"/> covers the frame body, so it includes the stages and the
/// small steps between them.
/// </summary>
public readonly record struct RenderCallbackStageTimingSnapshot(
    long Calls,
    double TotalMilliseconds,
    double PreRenderMilliseconds,
    double ViewportsCallbackMilliseconds,
    double ViewportsMilliseconds,
    double PostRenderMilliseconds,
    double RendererWindowMilliseconds,
    double PostViewportsCallbackMilliseconds,
    double MainThreadJobsMilliseconds);
