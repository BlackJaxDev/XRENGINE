namespace XREngine;

/// <summary>
/// Explains why a frame output DAG could not be lowered into a deterministic
/// execution order before command recording begins.
/// </summary>
public enum ERenderOutputDagCompilationFailure : byte
{
    None,
    DestinationCapacity,
    MissingPrerequisite,
    Cycle,
}
