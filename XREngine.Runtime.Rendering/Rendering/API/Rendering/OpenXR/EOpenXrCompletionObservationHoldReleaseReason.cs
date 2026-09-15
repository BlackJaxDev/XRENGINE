namespace XREngine.Rendering.API.Rendering.OpenXR;

/// <summary>Identifies the real boundary that ended a smoke completion-observation hold.</summary>
public enum EOpenXrCompletionObservationHoldReleaseReason : byte
{
    None,
    CapacityDeferral,
    AfterNativeCapacityWait,
    FrameDataSlotPressureAbort,
    Teardown,
    Multiple,
}
