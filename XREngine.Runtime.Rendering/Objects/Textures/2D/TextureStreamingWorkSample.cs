namespace XREngine.Rendering;

/// <summary>
/// The measurements of one completed render frame for one texture streaming work lane.
/// A lane makes one sample for each render frame in which it was active.
/// </summary>
/// <param name="FrameMilliseconds">
/// The wall time of the frame, from its start to the start of the next frame.
/// <see cref="double.NaN"/> when the lane did not see the next frame directly, so the time is not known.
/// </param>
/// <param name="BusyMilliseconds">
/// The render-thread time of the frame callback. It can include a present wait. <see cref="double.NaN"/> when not known.
/// </param>
/// <param name="TargetFrameMilliseconds">The explicit target frame period. Zero when the frame rate has no explicit target.</param>
/// <param name="WorkMilliseconds">
/// The measured cost of the lane work in the frame. It is the render-thread CPU time, or the GPU time when a GPU timestamp
/// gives a larger value.
/// </param>
/// <param name="ItemsCompleted">The number of items that the lane did in the frame.</param>
/// <param name="WorkDeferred">True when the frame limit deferred at least one item, so more work was ready.</param>
/// <param name="HeadroomShare">The share, from 0 to 1, of the measured idle frame headroom that the lane can use.</param>
internal readonly record struct TextureStreamingWorkSample(
    double FrameMilliseconds,
    double BusyMilliseconds,
    double TargetFrameMilliseconds,
    double WorkMilliseconds,
    int ItemsCompleted,
    bool WorkDeferred,
    double HeadroomShare);
