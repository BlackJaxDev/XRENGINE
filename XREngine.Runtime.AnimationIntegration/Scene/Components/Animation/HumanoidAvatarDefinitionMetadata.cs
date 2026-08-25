using XREngine.Animation.Importers;

namespace XREngine.Components.Animation;

/// <summary>
/// Version, identity, stable role bindings, and validation state for the avatar
/// definition owned by a <see cref="HumanoidComponent"/>. This is the canonical
/// serialized target-avatar representation; authoring settings and live
/// <c>BoneDef</c> references are migration/editor inputs only.
/// </summary>
public sealed class HumanoidAvatarDefinitionMetadata
{
    public const int CurrentSchemaVersion = 3;
    public const int CurrentAutoMappingAlgorithmVersion = 2;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public int AutoMappingAlgorithmVersion { get; set; } = CurrentAutoMappingAlgorithmVersion;
    public int DefinitionRevision { get; set; }
    public EHumanoidAvatarDefinitionStatus Status { get; set; }
    public string Source { get; set; } = "Automatic";
    public bool EditorConfirmed { get; set; }
    public string SkeletonContentSha256 { get; set; } = string.Empty;
    public string DefinitionContentSha256 { get; set; } = string.Empty;
    public EHumanoidAvatarSourceProvenance SourceProvenance { get; set; }
    public string SourceModelContentSha256 { get; set; } = string.Empty;
    public string CoordinateContractId { get; set; } = UnityAnimationCoordinateContract.CurrentContractId;
    public float HumanScale { get; set; }
    public float ModelUnitsPerMeter { get; set; } = 1.0f;
    public float MuscleInputScale { get; set; } = 1.0f;
    public HumanoidAvatarSolverSettings SolverSettings { get; set; } = new();
    public HumanoidAvatarBodyAxes BodyAxes { get; set; } = new();
    public HumanoidAvatarBoneBinding[] Bones { get; set; } = [];
    public HumanoidAvatarMuscleLimit[] MuscleLimits { get; set; } = [];
    public HumanoidAvatarTwistChain[] TwistChains { get; set; } = [];
    public HumanoidAvatarAuxiliaryBoneBinding[] AuxiliaryBones { get; set; } = [];
    public HumanoidAvatarLegacyCalibration? LegacyCalibration { get; set; }
    public string[] Diagnostics { get; set; } = [];

    public bool IsFinalized => Status == EHumanoidAvatarDefinitionStatus.Valid;
}
