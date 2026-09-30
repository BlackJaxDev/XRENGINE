namespace XREngine.Components;

public readonly record struct PhysicsChainColliderCandidateQueryResult(
    int CandidateCount,
    int RequiredCandidateCount,
    bool CandidateOverflow,
    bool TraversalOverflow)
{
    public bool Succeeded => !CandidateOverflow && !TraversalOverflow;
}
