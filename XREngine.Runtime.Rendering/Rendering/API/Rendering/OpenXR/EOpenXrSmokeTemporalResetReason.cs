namespace XREngine.Rendering.API.Rendering.OpenXR;

/// <summary>Identifies why an eye's temporal history requires reseeding.</summary>
[Flags]
public enum EOpenXrSmokeTemporalResetReason
{
    None = 0,
    ProfileChanged = 1 << 0,
    CameraCut = 1 << 1,
    MissingCamera = 1 << 2,
    MissingSnapshot = 1 << 3,
    ExplicitReset = 1 << 4,
    LogicalHistoryInvalid = 1 << 5,
}
