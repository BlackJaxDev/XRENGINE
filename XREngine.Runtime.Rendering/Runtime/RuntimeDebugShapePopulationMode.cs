namespace XREngine;

/// <summary>
/// Selects how transient debug primitive batches are populated.
/// </summary>
public enum RuntimeDebugShapePopulationMode
{
    Tasks,
    ParallelInvoke,
    Sequential,
}
