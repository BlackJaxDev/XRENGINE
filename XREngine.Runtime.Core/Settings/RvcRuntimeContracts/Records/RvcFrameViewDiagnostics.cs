namespace XREngine;

public readonly record struct RvcFrameViewDiagnostics(
    uint ViewId,
    EVrOutputViewKind ViewKind,
    uint RuntimeWidth,
    uint RuntimeHeight,
    float HorizontalFovDegrees,
    float VerticalFovDegrees,
    ulong SwapchainIdentity,
    ulong PixelCount,
    double GpuMilliseconds,
    EVrViewRenderMode StereoMode,
    EVrFoveationMode FoveationMode,
    ERvcFallbackReason FallbackReason);
