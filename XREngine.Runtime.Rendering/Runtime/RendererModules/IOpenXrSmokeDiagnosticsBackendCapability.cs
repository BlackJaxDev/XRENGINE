using XREngine.Rendering.API.Rendering.OpenXR;

namespace XREngine.Rendering;

/// <summary>
/// Provides backend-owned evidence required by the OpenXR smoke harness.
/// </summary>
public interface IOpenXrSmokeDiagnosticsBackendCapability
{
    void ResetDesktopRejectionEvidence(bool injectionRequested);

    OpenXrSmokeDesktopRejectionEvidence CaptureDesktopRejectionEvidence();
}
