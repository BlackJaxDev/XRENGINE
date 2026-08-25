namespace XREngine.Components.Animation;

/// <summary>
/// Records the isolated negative and positive response of one humanoid muscle.
/// The index and name follow Unity's HumanTrait muscle ordering.
/// </summary>
public sealed class HumanoidPoseAuditMuscleProbe
{
    public int Index { get; set; }
    public string Name { get; set; } = string.Empty;
    public List<HumanoidPoseAuditMuscleProbeBone> Bones { get; set; } = [];
}
