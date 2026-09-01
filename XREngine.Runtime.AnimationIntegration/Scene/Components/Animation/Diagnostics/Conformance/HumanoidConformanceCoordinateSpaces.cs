namespace XREngine.Components.Animation;

/// <summary>Explicit coordinate-space declarations required to compare a known-answer reference.</summary>
public sealed class HumanoidConformanceCoordinateSpaces
{
    public string RootTranslation { get; set; } = string.Empty;
    public string RootRotation { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public string HipsLocal { get; set; } = string.Empty;
    public string HipsWorld { get; set; } = string.Empty;
    public string BoneLocalRotation { get; set; } = string.Empty;
    public string Endpoint { get; set; } = string.Empty;
}
