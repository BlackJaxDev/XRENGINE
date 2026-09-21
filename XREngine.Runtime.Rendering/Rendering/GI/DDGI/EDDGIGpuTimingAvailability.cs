namespace XREngine.Rendering.GI.DDGI;

/// <summary>Current usability state for a DDGI GPU timestamp ring.</summary>
internal enum EDDGIGpuTimingAvailability
{
    Unrequested,
    Pending,
    Ready,
    Unsupported,
    Saturated,
    RendererChanged,
    OpenScope,
    InvalidWork,
    InvalidResult,
}
