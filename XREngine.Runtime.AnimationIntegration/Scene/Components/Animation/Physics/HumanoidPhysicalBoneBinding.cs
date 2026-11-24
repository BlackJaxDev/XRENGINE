using XREngine.Scene.Physics;
using XREngine.Scene.Transforms;

namespace XREngine.Components.Animation;

/// <summary>
/// Runtime association between one humanoid bone <see cref="Transform"/> and its backing
/// <see cref="IAbstractDynamicRigidBody"/> inside <see cref="HumanoidPhysicalAnimationComponent"/>.
/// </summary>
public sealed class HumanoidPhysicalBoneBinding
{
    public HumanoidPhysicalBoneBinding(
        string name,
        EHumanoidBodyMask region,
        Transform boneTransform,
        IAbstractDynamicRigidBody rigidBody)
    {
        Name = name;
        Region = region;
        BoneTransform = boneTransform;
        RigidBody = rigidBody;
    }

    public string Name { get; }
    public EHumanoidBodyMask Region { get; }
    public Transform BoneTransform { get; }
    public IAbstractDynamicRigidBody RigidBody { get; }

    internal float HitComplianceRemainingSeconds { get; set; }
    internal float HitComplianceTotalSeconds { get; set; }
    internal float HitComplianceWeightScale { get; set; } = 1.0f;
}
