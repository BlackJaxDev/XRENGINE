namespace XREngine.Rendering;

/// <summary>
/// Structured cutover status for one Advanced pipeline/output profile. Execution admission
/// never implies that the output has passed runtime or production acceptance.
/// </summary>
public readonly record struct AdvancedProductionCutoverStatus(
    EAdvancedProductionExecutionState ExecutionState,
    EAdvancedRuntimeValidationState RuntimeValidationState,
    EAdvancedProductionAcceptanceState ProductionAcceptanceState,
    string? BlockerReason)
{
    public bool IsExecutionAdmitted => ExecutionState == EAdvancedProductionExecutionState.Admitted;
    public bool IsProductionAccepted => ProductionAcceptanceState == EAdvancedProductionAcceptanceState.Accepted;

    public string Diagnostic => BlockerReason ?? ExecutionState switch
    {
        EAdvancedProductionExecutionState.Admitted when RuntimeValidationState == EAdvancedRuntimeValidationState.Pending =>
            "Advanced execution is admitted; runtime output validation is pending.",
        EAdvancedProductionExecutionState.Admitted => "Advanced execution and runtime validation are accepted.",
        EAdvancedProductionExecutionState.PendingResources => "Advanced execution is awaiting runtime resources.",
        _ => "Advanced execution is unsupported for this output profile.",
    };
}