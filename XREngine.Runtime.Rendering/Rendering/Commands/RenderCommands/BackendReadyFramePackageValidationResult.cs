namespace XREngine.Rendering.Commands;

/// <summary>
/// Allocation-free result of backend-ready package validation.
/// </summary>
public readonly record struct BackendReadyFramePackageValidationResult(
    bool Accepted,
    EBackendReadyFramePackageValidationFailure Failure)
{
    public static BackendReadyFramePackageValidationResult Success => new(
        true,
        EBackendReadyFramePackageValidationFailure.None);

    public static BackendReadyFramePackageValidationResult Reject(
        EBackendReadyFramePackageValidationFailure failure)
        => new(false, failure);
}
