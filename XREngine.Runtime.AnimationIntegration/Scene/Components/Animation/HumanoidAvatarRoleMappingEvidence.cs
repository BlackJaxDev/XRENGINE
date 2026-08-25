namespace XREngine.Components.Animation;

/// <summary>
/// Non-serialized result of selecting one role candidate during automatic mapping.
/// It is copied into the canonical binding when the definition is refreshed.
/// </summary>
internal sealed class HumanoidAvatarRoleMappingEvidence
{
    public EHumanoidAvatarMappingSource Source { get; set; }
    public float Confidence { get; set; }
    public float ImportedMetadataScore { get; set; }
    public float TopologyScore { get; set; }
    public float GeometryScore { get; set; }
    public float AxisScore { get; set; }
    public float SymmetryScore { get; set; }
    public float AliasScore { get; set; }
    public string Summary { get; set; } = string.Empty;
}
