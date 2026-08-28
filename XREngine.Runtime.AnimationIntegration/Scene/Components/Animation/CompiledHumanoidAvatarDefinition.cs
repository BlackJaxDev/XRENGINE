using System.Numerics;
using XREngine.Scene;

namespace XREngine.Components.Animation;

/// <summary>
/// Immutable role-indexed runtime form of one validated avatar definition.
/// All dictionaries are built once and use object identity rather than names.
/// </summary>
internal sealed class CompiledHumanoidAvatarDefinition
{
    public const int RoleCount = (int)EHumanoidAvatarBoneRole.RightLittleDistal + 1;
    public const int MuscleCount = (int)EHumanoidValue.RightHandThumb3Stretched + 1;

    private readonly Dictionary<SceneNode, EHumanoidAvatarBoneRole> _rolesByNode;

    public CompiledHumanoidAvatarDefinition(
        int schemaVersion,
        int definitionRevision,
        string definitionContentSha256,
        SceneNode?[] nodes,
        Matrix4x4[] neutralLocalTransforms,
        Matrix4x4[] neutralWorldTransforms,
        Quaternion[] canonicalPoseCorrections,
        Quaternion[] preRotations,
        Quaternion[] postRotations,
        EHumanoidAvatarRotationOrder[] rotationOrders,
        bool[] hasTranslationDegreesOfFreedom,
        BoneAxisMapping[] axisMappings,
        bool[] hasAxisMappings,
        HumanoidAvatarJointLimit[] jointLimits,
        Vector2[] muscleRanges,
        HumanoidAvatarSolverSettings solverSettings,
        HumanoidAvatarBodyAxes bodyAxes,
        float humanScale,
        float modelUnitsPerMeter,
        CompiledHumanoidAvatarTwistChain[] twistChains,
        CompiledHumanoidAvatarAuxiliaryBone[] auxiliaryBones,
        CompiledHumanoidBoneSolvePlan[] boneSolvePlans,
        int[] boneSolvePlanOrder,
        CompiledHumanoidConcreteCommitTarget[] concreteCommitTargets,
        int[] concreteCommitOrder,
        Matrix4x4 hipsParentInModelRootFrame,
        Matrix4x4 inverseHipsParentInModelRootFrame)
    {
        SchemaVersion = schemaVersion;
        DefinitionRevision = definitionRevision;
        DefinitionContentSha256 = definitionContentSha256;
        Nodes = nodes;
        NeutralLocalTransforms = neutralLocalTransforms;
        NeutralWorldTransforms = neutralWorldTransforms;
        CanonicalPoseCorrections = canonicalPoseCorrections;
        PreRotations = preRotations;
        PostRotations = postRotations;
        RotationOrders = rotationOrders;
        HasTranslationDegreesOfFreedom = hasTranslationDegreesOfFreedom;
        AxisMappings = axisMappings;
        HasAxisMappings = hasAxisMappings;
        JointLimits = jointLimits;
        MuscleRanges = muscleRanges;
        SolverSettings = solverSettings;
        BodyAxes = bodyAxes;
        HumanScale = humanScale;
        ModelUnitsPerMeter = modelUnitsPerMeter;
        TwistChains = twistChains;
        AuxiliaryBones = auxiliaryBones;
        BoneSolvePlans = boneSolvePlans;
        BoneSolvePlanOrder = boneSolvePlanOrder;
        ConcreteCommitTargets = concreteCommitTargets;
        ConcreteCommitOrder = concreteCommitOrder;
        HipsParentInModelRootFrame = hipsParentInModelRootFrame;
        InverseHipsParentInModelRootFrame = inverseHipsParentInModelRootFrame;

        _rolesByNode = new Dictionary<SceneNode, EHumanoidAvatarBoneRole>(RoleCount);
        for (int i = 0; i < nodes.Length; i++)
            if (nodes[i] is SceneNode node)
                _rolesByNode.TryAdd(node, (EHumanoidAvatarBoneRole)i);
    }

    public int SchemaVersion { get; }
    public int DefinitionRevision { get; }
    public string DefinitionContentSha256 { get; }
    public SceneNode?[] Nodes { get; }
    public Matrix4x4[] NeutralLocalTransforms { get; }
    public Matrix4x4[] NeutralWorldTransforms { get; }
    public Quaternion[] CanonicalPoseCorrections { get; }
    public Quaternion[] PreRotations { get; }
    public Quaternion[] PostRotations { get; }
    public EHumanoidAvatarRotationOrder[] RotationOrders { get; }
    public bool[] HasTranslationDegreesOfFreedom { get; }
    public BoneAxisMapping[] AxisMappings { get; }
    public bool[] HasAxisMappings { get; }
    public HumanoidAvatarJointLimit[] JointLimits { get; }
    public Vector2[] MuscleRanges { get; }
    public HumanoidAvatarSolverSettings SolverSettings { get; }
    public HumanoidAvatarBodyAxes BodyAxes { get; }
    public float HumanScale { get; }
    public float ModelUnitsPerMeter { get; }
    public CompiledHumanoidAvatarTwistChain[] TwistChains { get; }
    public CompiledHumanoidAvatarAuxiliaryBone[] AuxiliaryBones { get; }
    /// <summary>Stable role-indexed solver plans, ordered parent-before-child.</summary>
    public CompiledHumanoidBoneSolvePlan[] BoneSolvePlans { get; }
    /// <summary>Role indexes in stable parent-before-child evaluation order.</summary>
    public int[] BoneSolvePlanOrder { get; }
    public CompiledHumanoidConcreteCommitTarget[] ConcreteCommitTargets { get; }
    public int[] ConcreteCommitOrder { get; }
    /// <summary>Canonical neutral Hips-parent frame expressed in model-root coordinates.</summary>
    public Matrix4x4 HipsParentInModelRootFrame { get; }
    public Matrix4x4 InverseHipsParentInModelRootFrame { get; }

    public bool TryGetRole(SceneNode? node, out EHumanoidAvatarBoneRole role)
    {
        if (node is not null && _rolesByNode.TryGetValue(node, out role))
            return true;

        role = default;
        return false;
    }

    public SceneNode? GetNode(EHumanoidAvatarBoneRole role)
    {
        int index = (int)role;
        return (uint)index < (uint)Nodes.Length ? Nodes[index] : null;
    }

    public ref readonly CompiledHumanoidBoneSolvePlan GetBoneSolvePlan(EHumanoidAvatarBoneRole role)
    {
        int index = (int)role;
        if ((uint)index >= (uint)BoneSolvePlans.Length)
            throw new ArgumentOutOfRangeException(nameof(role));

        return ref BoneSolvePlans[index];
    }

    public BoneAxisMapping? GetAxisMapping(EHumanoidAvatarBoneRole role)
    {
        int index = (int)role;
        return (uint)index < (uint)AxisMappings.Length && HasAxisMappings[index]
            ? AxisMappings[index]
            : null;
    }

    public BoneAxisMapping? GetAxisMapping(SceneNode? node)
    {
        if (!TryGetRole(node, out EHumanoidAvatarBoneRole role))
            return null;

        return GetAxisMapping(role);
    }

    public Quaternion GetCanonicalPoseCorrection(EHumanoidAvatarBoneRole role)
    {
        int index = (int)role;
        return (uint)index < (uint)CanonicalPoseCorrections.Length
            ? CanonicalPoseCorrections[index]
            : Quaternion.Identity;
    }

    public Quaternion GetCanonicalPoseCorrection(SceneNode? node)
        => TryGetRole(node, out EHumanoidAvatarBoneRole role)
            ? GetCanonicalPoseCorrection(role)
            : Quaternion.Identity;

    public bool TryGetNeutralLocalState(
        SceneNode? node,
        out Vector3 scale,
        out Quaternion rotation,
        out Vector3 translation)
    {
        if (TryGetRole(node, out EHumanoidAvatarBoneRole role)
            && Matrix4x4.Decompose(
                NeutralLocalTransforms[(int)role],
                out scale,
                out rotation,
                out translation))
        {
            rotation = Quaternion.Normalize(rotation);
            return true;
        }

        scale = Vector3.One;
        rotation = Quaternion.Identity;
        translation = Vector3.Zero;
        return false;
    }

    public Matrix4x4 GetNeutralWorldTransform(SceneNode node)
        => TryGetRole(node, out EHumanoidAvatarBoneRole role)
            ? NeutralWorldTransforms[(int)role]
            : Matrix4x4.Identity;

    public Vector2 GetMuscleRange(EHumanoidValue value)
    {
        int index = (int)value;
        return (uint)index < (uint)MuscleRanges.Length
            ? MuscleRanges[index]
            : Vector2.Zero;
    }

}
