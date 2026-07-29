namespace XREngine.Scene.Importers;

/// <summary>
/// Selected GUID result together with any lower-precedence duplicates.
/// </summary>
public sealed class UnityGuidResolution
{
    public required string Guid { get; init; }
    public UnityGuidCandidate? Selected { get; init; }
    public IReadOnlyList<UnityGuidCandidate> Candidates { get; init; } = [];
    public bool IsDuplicate => Candidates.Count > 1;
}
