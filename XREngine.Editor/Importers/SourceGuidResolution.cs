namespace XREngine.Scene.Importers;

/// <summary>
/// Selected GUID result together with any lower-precedence duplicates.
/// </summary>
public sealed class SourceGuidResolution
{
    public required string Guid { get; init; }
    public SourceGuidCandidate? Selected { get; init; }
    public IReadOnlyList<SourceGuidCandidate> Candidates { get; init; } = [];
    public bool IsDuplicate => Candidates.Count > 1;
}
