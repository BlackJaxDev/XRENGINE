namespace XREngine.Rendering;

/// <summary>Describes whether an Advanced output can execute its admitted backend stage family.</summary>
public enum EAdvancedProductionExecutionState
{
    Unsupported = 0,
    PendingResources,
    Admitted,
}