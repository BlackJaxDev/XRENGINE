namespace XREngine.Rendering;

/// <summary>Renderer-neutral readiness snapshot for the native Advanced visibility family.</summary>
public readonly record struct AdvancedVisibilityFamilyAdmission(
    EAdvancedProductionExecutionState State,
    string Reason)
{
    public bool IsAdmitted => State == EAdvancedProductionExecutionState.Admitted;
}