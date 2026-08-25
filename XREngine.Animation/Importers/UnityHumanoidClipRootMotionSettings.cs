using MemoryPack;

namespace XREngine.Animation.Importers;

/// <summary>
/// Preserves Unity's serialized humanoid clip projection policy. RootT/RootQ
/// contain the retargetable Body Transform; these settings determine which
/// parts Unity bakes into the pose and which parts it projects onto the model
/// root at runtime.
/// </summary>
[MemoryPackable]
public sealed partial class UnityHumanoidClipRootMotionSettings
{
    public float StartTime { get; set; }
    public float StopTime { get; set; }
    public float OrientationOffsetY { get; set; }
    public float Level { get; set; }
    public float CycleOffset { get; set; }
    public bool LoopTime { get; set; }
    public bool LoopPose { get; set; }
    public bool BakeOrientationIntoPose { get; set; }
    public bool BakePositionYIntoPose { get; set; }
    public bool BakePositionXZIntoPose { get; set; }
    public bool KeepOriginalOrientation { get; set; }
    public bool KeepOriginalPositionY { get; set; }
    public bool KeepOriginalPositionXZ { get; set; }
    public bool HeightFromFeet { get; set; }
    public bool Mirror { get; set; }
}
