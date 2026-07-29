namespace XREngine.Rendering;

/// <summary>
/// Previous-deformation requirement for non-primary views.
/// </summary>
public enum EAdvancedCapturePreviousDataPolicy : uint
{
    NotRequired = 0u,
    RequiredForVelocity = 1u,
    RequiredForTemporalHistory = 2u,
}
