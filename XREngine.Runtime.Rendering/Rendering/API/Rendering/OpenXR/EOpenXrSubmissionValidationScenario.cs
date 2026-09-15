namespace XREngine.Rendering.API.Rendering.OpenXR;

/// <summary>Chooses the bounded OpenXR submission validation behavior for a smoke run.</summary>
public enum EOpenXrSubmissionValidationScenario : byte
{
    Disabled,
    Observe,
    HoldCompletionObservationUntilCapacity,
    RejectBeforeNativeSubmit,
    FailAcceptedPublication,
    HoldCompletionObservationUntilRecoveryWait,
}
