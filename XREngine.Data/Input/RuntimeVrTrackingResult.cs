namespace XREngine.Input;

/// <summary>Tracked-pose status values carried by the VR input transport.</summary>
public enum RuntimeVrTrackingResult
{
    Uninitialized = 1,
    CalibratingInProgress = 100,
    CalibratingOutOfRange = 101,
    RunningOk = 200,
    RunningOutOfRange = 201,
    FallbackRotationOnly = 300,
}
