namespace XREngine.Scene.Transforms;

/// <summary>
/// Dirty-state snapshot used to make temporary diagnostic pose evaluation observational.
/// </summary>
public readonly struct TransformDiagnosticInvalidationState
{
    internal TransformDiagnosticInvalidationState(bool isLocalMatrixDirty, bool isWorldMatrixDirty, bool hasChanged)
    {
        IsLocalMatrixDirty = isLocalMatrixDirty;
        IsWorldMatrixDirty = isWorldMatrixDirty;
        HasChanged = hasChanged;
    }

    public bool IsLocalMatrixDirty { get; }
    public bool IsWorldMatrixDirty { get; }
    public bool HasChanged { get; }
}
