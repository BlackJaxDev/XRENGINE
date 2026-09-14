using XREngine.Rendering.API.Rendering.OpenXR;

namespace XREngine.Rendering;

/// <summary>
/// Provides backend-owned evidence required by the OpenXR smoke harness.
/// </summary>
public interface IOpenXrSmokeDiagnosticsBackendCapability
{
    void ConfigureOpenXrSubmissionValidation(in OpenXrSubmissionValidationRequest request);

    OpenXrSubmissionValidationSnapshot CaptureOpenXrSubmissionValidation();

    void ResetDesktopRejectionEvidence(bool injectionRequested);

    OpenXrSmokeDesktopRejectionEvidence CaptureDesktopRejectionEvidence();
}
