namespace XREngine.Components.Animation;

/// <summary>One avatar, clip, playback-route, and reference pairing in the conformance matrix.</summary>
public sealed class HumanoidConformanceMatrixCase
{
    public string Id { get; set; } = string.Empty;
    public string AvatarId { get; set; } = string.Empty;
    public string ClipId { get; set; } = string.Empty;
    public HumanoidConformancePlaybackMode PlaybackMode { get; set; }
    public string ReferenceFileId { get; set; } = string.Empty;
    public string ReferenceSignature { get; set; } = string.Empty;
    public string AvatarDefinitionSignature { get; set; } = string.Empty;
    public string ClipSignature { get; set; } = string.Empty;
    public string Provenance { get; set; } = string.Empty;
    public HumanoidConformanceKnownAnswerProvenance KnownAnswer { get; set; } = new();
    public bool IsIntegrationOnly { get; set; }
    public HumanoidConformanceCapability ExpectedCapabilities { get; set; }
    public HumanoidConformanceCoordinateSpaces CoordinateSpaces { get; set; } = new();
    public HumanoidConformanceTolerances Tolerances { get; set; } = new();
}
