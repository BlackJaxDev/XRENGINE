namespace XREngine.Rendering;

/// <summary>Separates execution admission from runtime image and output validation evidence.</summary>
public enum EAdvancedRuntimeValidationState
{
    NotApplicable = 0,
    Pending,
    Accepted,
}