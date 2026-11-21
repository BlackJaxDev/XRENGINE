namespace XREngine.Rendering;

/// <summary>
/// The measurements of one render frame for <see cref="TextureStreamingDecodeWidthController"/>.
/// </summary>
/// <param name="FrameMilliseconds">The frame time, or <see cref="double.NaN"/> when it is not known.</param>
/// <param name="TargetFrameMilliseconds">The explicit target frame period, or zero.</param>
/// <param name="ActiveDecodes">The decodes that held a permit of the gate in the frame.</param>
/// <param name="WaitingDecodes">The decodes that waited for a permit.</param>
internal readonly record struct TextureStreamingDecodeSample(
    double FrameMilliseconds,
    double TargetFrameMilliseconds,
    int ActiveDecodes,
    int WaitingDecodes);
