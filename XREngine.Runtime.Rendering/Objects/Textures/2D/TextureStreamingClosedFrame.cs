namespace XREngine.Rendering;

/// <summary>
/// The measurements of one sampled render frame that <see cref="TextureStreamingFrameSampler"/> closed.
/// </summary>
/// <param name="FrameMilliseconds">The frame time, or <see cref="double.NaN"/> when the next frame was not sampled directly.</param>
/// <param name="BusyMilliseconds">The render-thread callback time of the frame, or <see cref="double.NaN"/>.</param>
/// <param name="IdleFrameMilliseconds">The average frame time of a gap of frames without samples, or <see cref="double.NaN"/>.</param>
/// <param name="IdleBusyMilliseconds">The average render-thread callback time of that gap, or <see cref="double.NaN"/>.</param>
internal readonly record struct TextureStreamingClosedFrame(
    double FrameMilliseconds,
    double BusyMilliseconds,
    double IdleFrameMilliseconds,
    double IdleBusyMilliseconds);
