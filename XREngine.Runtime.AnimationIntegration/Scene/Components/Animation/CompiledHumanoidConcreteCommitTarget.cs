namespace XREngine.Components.Animation;

/// <summary>
/// Immutable parent-before-child concrete commit target. Index addresses either
/// the role solve arrays or the auxiliary solve arrays.
/// </summary>
internal readonly struct CompiledHumanoidConcreteCommitTarget
{
    public CompiledHumanoidConcreteCommitTarget(bool isAuxiliary, int index, int parentTargetIndex)
    {
        IsAuxiliary = isAuxiliary;
        Index = index;
        ParentTargetIndex = parentTargetIndex;
    }

    public bool IsAuxiliary { get; }
    public int Index { get; }
    public int ParentTargetIndex { get; }
}
