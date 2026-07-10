namespace XREngine;

public enum EFrameOutputSkipReason
{
    None,
    Cadence,
    Budget,
    MirrorOff,
    SurfaceUnavailable,
    VrGated,
    Disabled,
    HeldLastImage,
}
