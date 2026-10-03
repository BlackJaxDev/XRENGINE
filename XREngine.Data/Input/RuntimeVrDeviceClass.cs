namespace XREngine.Input;

/// <summary>Device classes reported by the VR runtime. Values match OpenVR's device class wire values.</summary>
public enum RuntimeVrDeviceClass
{
    Invalid = 0,
    Headset = 1,
    Controller = 2,
    GenericTracker = 3,
    TrackingReference = 4,
    DisplayRedirect = 5,
}
