namespace XREngine.Rendering;

/// <summary>An exact prepared Uber pass companion retained by the target material.</summary>
public sealed record UberBasePassArtifact
{
    public string Pass { get; init; } = string.Empty;
    public string ArtifactIdentity { get; init; } = string.Empty;
}
