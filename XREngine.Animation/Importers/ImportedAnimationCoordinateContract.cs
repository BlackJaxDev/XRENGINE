using MemoryPack;

namespace XREngine.Animation.Importers;

/// <summary>
/// Versioned coordinate and handedness rules used by the native Unity importer.
/// The descriptions are persisted so imported assets never depend on a hidden
/// manual flip preset.
/// </summary>
[MemoryPackable]
public sealed partial class ImportedAnimationCoordinateContract
{
    public const string CurrentContractId = "UnityLH-YUp-to-XRERH-YUp-AssimpBasis-v1";

    public string ContractId { get; set; } = CurrentContractId;
    public string GenericTransformRule { get; set; } =
        "Preserve values in the model importer's established skeleton basis";
    public string HumanoidPositionRule { get; set; } = "(-x,y,z)";
    public string HumanoidBodyPositionRule { get; set; } = "(-x,z,y)";
    public string HumanoidRotationRule { get; set; } = "(x,-y,-z,w)";
    public string MuscleRule { get; set; } =
        "Unity HumanTrait normalized channels; no implicit left/right or sign flip";
}
