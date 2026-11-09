namespace XREngine.Components;

/// <summary>
/// Selects how stiffness acts on a chain segment.
/// </summary>
public enum EPhysicsChainStiffnessMode
{
    /// <summary>Stiffness limits how far a particle can move from its rest position. A stiffness of 1 holds the rest shape.</summary>
    DistanceLimit,
    /// <summary>
    /// Stiffness holds the offset of a particle from its parent as it was before the step.
    /// A particle moves more with its parent and does not change where it settles.
    /// </summary>
    HoldParentOffset,
}
