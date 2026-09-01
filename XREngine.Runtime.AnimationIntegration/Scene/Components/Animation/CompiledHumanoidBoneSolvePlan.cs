using System.Numerics;
using XREngine.Scene;

namespace XREngine.Components.Animation;

/// <summary>
/// Immutable, role-local input to the humanoid pose solver. This is assembled
/// during avatar compilation so evaluation never consults serialized metadata.
/// </summary>
internal readonly struct CompiledHumanoidBoneSolvePlan
{
    public CompiledHumanoidBoneSolvePlan(
        EHumanoidAvatarBoneRole role,
        SceneNode? node,
        Vector3 neutralScale,
        Quaternion neutralRotation,
        Vector3 neutralTranslation,
        Matrix4x4 neutralTransformRelativeToMappedAncestor,
        CompiledHumanoidParentBridgeSegment[] parentBridgeSegments,
        Quaternion canonicalPoseCorrection,
        Quaternion preRotation,
        Quaternion postRotation,
        EHumanoidAvatarRotationOrder rotationOrder,
        bool permitsTranslationDegreesOfFreedom,
        BoneAxisMapping axisMapping,
        bool hasAxisMapping,
        CompiledHumanoidJointLimit jointLimit,
        EHumanoidAvatarBoneRole? semanticParentRole,
        EHumanoidAvatarBoneRole? effectiveParentRole,
        int mappedAncestorPlanIndex,
        Quaternion zeroMuscleRotation,
        Quaternion inverseRestJoint)
        : this(
            role,
            node,
            neutralScale,
            neutralRotation,
            neutralTranslation,
            neutralTransformRelativeToMappedAncestor,
            parentBridgeSegments,
            canonicalPoseCorrection,
            preRotation,
            postRotation,
            rotationOrder,
            permitsTranslationDegreesOfFreedom,
            axisMapping,
            hasAxisMapping,
            jointLimit,
            semanticParentRole,
            effectiveParentRole,
            mappedAncestorPlanIndex,
            zeroMuscleRotation,
            inverseRestJoint,
            Quaternion.Identity,
            false)
    {
    }

    public CompiledHumanoidBoneSolvePlan(
        EHumanoidAvatarBoneRole role,
        SceneNode? node,
        Vector3 neutralScale,
        Quaternion neutralRotation,
        Vector3 neutralTranslation,
        Matrix4x4 neutralTransformRelativeToMappedAncestor,
        CompiledHumanoidParentBridgeSegment[] parentBridgeSegments,
        Quaternion canonicalPoseCorrection,
        Quaternion preRotation,
        Quaternion postRotation,
        EHumanoidAvatarRotationOrder rotationOrder,
        bool permitsTranslationDegreesOfFreedom,
        BoneAxisMapping axisMapping,
        bool hasAxisMapping,
        CompiledHumanoidJointLimit jointLimit,
        EHumanoidAvatarBoneRole? semanticParentRole,
        EHumanoidAvatarBoneRole? effectiveParentRole,
        int mappedAncestorPlanIndex,
        Quaternion zeroMuscleRotation,
        Quaternion inverseRestJoint,
        Quaternion jointBasisToZeroLocal,
        bool hasContinuousJointBasis)
    {
        Role = role;
        Node = node;
        NeutralScale = neutralScale;
        NeutralRotation = neutralRotation;
        NeutralTranslation = neutralTranslation;
        NeutralTransformRelativeToMappedAncestor = neutralTransformRelativeToMappedAncestor;
        ParentBridgeSegments = parentBridgeSegments;
        CanonicalPoseCorrection = canonicalPoseCorrection;
        PreRotation = preRotation;
        PostRotation = postRotation;
        RotationOrder = rotationOrder;
        PermitsTranslationDegreesOfFreedom = permitsTranslationDegreesOfFreedom;
        AxisMapping = axisMapping;
        HasAxisMapping = hasAxisMapping;
        JointLimit = jointLimit;
        SemanticParentRole = semanticParentRole;
        EffectiveParentRole = effectiveParentRole;
        MappedAncestorPlanIndex = mappedAncestorPlanIndex;
        ZeroMuscleRotation = zeroMuscleRotation;
        InverseRestJoint = inverseRestJoint;
        JointBasisToZeroLocal = jointBasisToZeroLocal;
        HasContinuousJointBasis = hasContinuousJointBasis;
    }

    public EHumanoidAvatarBoneRole Role { get; }
    public SceneNode? Node { get; }
    public Vector3 NeutralScale { get; }
    public Quaternion NeutralRotation { get; }
    public Vector3 NeutralTranslation { get; }
    /// <summary>
    /// Neutral pose relative to the nearest mapped ancestor. This bridges
    /// un-mapped helper nodes without querying live transforms during FK.
    /// </summary>
    public Matrix4x4 NeutralTransformRelativeToMappedAncestor { get; }
    /// <summary>Concrete helper transforms between this role and its nearest mapped ancestor.</summary>
    public CompiledHumanoidParentBridgeSegment[] ParentBridgeSegments { get; }
    public Quaternion CanonicalPoseCorrection { get; }
    public Quaternion PreRotation { get; }
    public Quaternion PostRotation { get; }
    public EHumanoidAvatarRotationOrder RotationOrder { get; }
    public bool PermitsTranslationDegreesOfFreedom { get; }
    public BoneAxisMapping AxisMapping { get; }
    public bool HasAxisMapping { get; }
    public CompiledHumanoidJointLimit JointLimit { get; }
    public EHumanoidAvatarBoneRole? SemanticParentRole { get; }
    public EHumanoidAvatarBoneRole? EffectiveParentRole { get; }
    /// <summary>Nearest mapped parent plan, or -1 when this role is a model-root child.</summary>
    public int MappedAncestorPlanIndex { get; }
    /// <summary>Finalized local rotation for zero muscle input.</summary>
    public Quaternion ZeroMuscleRotation { get; }
    /// <summary>Inverse authored rest joint (<c>Pre * Ordered(Center) * Post</c>).</summary>
    public Quaternion InverseRestJoint { get; }
    /// <summary>
    /// Proper rotation from canonical anatomical joint coordinates into this
    /// bone's zero-muscle local frame. It is compiled once; evaluating a pose
    /// only conjugates the canonical joint delta by this basis.
    /// </summary>
    public Quaternion JointBasisToZeroLocal { get; }
    /// <summary>
    /// Whether staged degrees are already expressed in the continuous
    /// canonical joint frame and must bypass the legacy cardinal-axis mapping.
    /// </summary>
    public bool HasContinuousJointBasis { get; }
}

/// <summary>
/// Value-copy of authored joint limits retained by a compiled avatar plan.
/// </summary>
internal readonly struct CompiledHumanoidJointLimit
{
    public CompiledHumanoidJointLimit(
        bool useDefaultValues,
        Vector3 centerDegrees,
        Vector3 minimumDegrees,
        Vector3 maximumDegrees,
        float axisLength)
    {
        UseDefaultValues = useDefaultValues;
        CenterDegrees = centerDegrees;
        MinimumDegrees = minimumDegrees;
        MaximumDegrees = maximumDegrees;
        AxisLength = axisLength;
    }

    public bool UseDefaultValues { get; }
    public Vector3 CenterDegrees { get; }
    public Vector3 MinimumDegrees { get; }
    public Vector3 MaximumDegrees { get; }
    public float AxisLength { get; }
}
