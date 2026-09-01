using System.Numerics;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.Components.Animation;

/// <summary>
/// Authors the temporary canonical skeleton pose used to describe a humanoid avatar.
/// </summary>
/// <remarks>
/// This mirrors the direction-normalisation portion of Unity's avatar authoring
/// validation without mutating the imported scene. It deliberately works on the
/// complete transform hierarchy, so helper bones can determine a limb direction
/// even when they are not mapped to a humanoid role.
/// </remarks>
internal static class HumanoidCanonicalSkeletonAuthoring
{
    /// <summary>
    /// Creates root-relative canonical transforms and canonical local rotations for mapped bones.
    /// </summary>
    public static void Normalize(
        HumanoidAvatarBoneBinding[] bindings,
        HumanoidAvatarBodyAxes axes,
        SceneNode root,
        IReadOnlyDictionary<SceneNode, Matrix4x4> localBinds,
        IReadOnlyDictionary<EHumanoidAvatarBoneRole, SceneNode> roleNodes)
    {
        // A failed normalisation must be indistinguishable from the native bind pose.
        foreach (HumanoidAvatarBoneBinding binding in bindings)
            binding.CanonicalWorldTransform = binding.NeutralWorldTransform;

        if (!axes.IsFiniteOrthonormal())
            return;

        var hierarchy = new Dictionary<SceneNode, CanonicalNode>(ReferenceEqualityComparer.Instance);
        CanonicalNode? hierarchyRoot = BuildHierarchy(root, null, localBinds, hierarchy);
        if (hierarchyRoot is null)
            return;

        RecomputeWorldTransforms(hierarchyRoot);

        var roleBones = new Dictionary<EHumanoidAvatarBoneRole, CanonicalNode>();
        foreach ((EHumanoidAvatarBoneRole role, SceneNode node) in roleNodes)
            if (hierarchy.TryGetValue(node, out CanonicalNode? canonicalNode))
                roleBones[role] = canonicalNode;

        if (!TryCreateBodyFrame(roleBones, axes, out BodyFrame bodyFrame))
            return;

        foreach (EHumanoidAvatarBoneRole role in s_UnityValidationOrder)
        {
            if (role == EHumanoidAvatarBoneRole.Hips ||
                !roleBones.TryGetValue(role, out CanonicalNode? bone) ||
                !TryGetRule(role, out CanonicalisationRule rule))
                continue;

            NormalizeBoneDirection(role, bone, roleBones, bodyFrame, rule);
        }

        // Unity's hips-specific orientation test depends on the full importer-side
        // human-description construction. We have no ratified equivalent yet;
        // applying a guessed correction here would corrupt otherwise exact imported
        // canonical frames. The evidenced and intentional behaviour is no-op.

        if (!Matrix4x4.Invert(hierarchyRoot.WorldMatrix, out Matrix4x4 rootInverse))
            return;

        foreach ((EHumanoidAvatarBoneRole role, CanonicalNode node) in roleBones)
        {
            if (!TryGetBinding(bindings, role, out HumanoidAvatarBoneBinding binding))
                continue;

            binding.CanonicalLocalRotation = Quaternion.Normalize(node.LocalRotation);
            binding.CanonicalWorldTransform = node.WorldMatrix * rootInverse;
        }
    }

    private static void NormalizeBoneDirection(
        EHumanoidAvatarBoneRole role,
        CanonicalNode bone,
        IReadOnlyDictionary<EHumanoidAvatarBoneRole, CanonicalNode> roleBones,
        BodyFrame bodyFrame,
        CanonicalisationRule rule)
    {
        Vector3 actualDirection = GetChildDirection(role, bone, roleBones);
        if (!TryNormalize(actualDirection, out actualDirection))
            return;

        Quaternion parentDelta = Quaternion.Identity;
        Vector3 goalDirection = bodyFrame.Map(rule.TargetDirection);
        EHumanoidAvatarBoneRole? parentRole = GetParentRole(role);

        if (rule.IsLocal &&
            parentRole is EHumanoidAvatarBoneRole parent &&
            roleBones.TryGetValue(parent, out CanonicalNode? parentBone) &&
            TryGetRule(parent, out CanonicalisationRule parentRule))
        {
            Vector3 parentDirection = GetChildDirection(parent, parentBone, roleBones);
            if (TryNormalize(parentDirection, out parentDirection))
            {
                parentDelta = FromToRotation(bodyFrame.Map(parentRule.TargetDirection), parentDirection);
                goalDirection = Vector3.Transform(goalDirection, parentDelta);
            }
        }

        if (rule.PlaneNormal != Vector3.Zero)
        {
            // Both the target and the constraint plane move with a local parent.
            Vector3 planeNormal = Vector3.Transform(bodyFrame.Map(rule.PlaneNormal), parentDelta);
            actualDirection -= planeNormal * Vector3.Dot(actualDirection, planeNormal);
            if (!TryNormalize(actualDirection, out actualDirection))
                return;
        }

        float angleDegrees = AngleDegrees(actualDirection, goalDirection);
        if (angleDegrees <= rule.MaximumErrorDegrees * 0.99f)
            return;

        Quaternion correction = Quaternion.Slerp(
            Quaternion.Identity,
            FromToRotation(actualDirection, goalDirection),
            Math.Clamp(1.05f - rule.MaximumErrorDegrees / angleDegrees, 0.0f, 1.0f));

        CanonicalNode? foot = role switch
        {
            EHumanoidAvatarBoneRole.LeftUpperLeg or EHumanoidAvatarBoneRole.LeftLowerLeg =>
                GetRoleNode(roleBones, EHumanoidAvatarBoneRole.LeftFoot),
            EHumanoidAvatarBoneRole.RightUpperLeg or EHumanoidAvatarBoneRole.RightLowerLeg =>
                GetRoleNode(roleBones, EHumanoidAvatarBoneRole.RightFoot),
            _ => null,
        };

        Quaternion savedFootRotation = foot?.WorldRotation ?? Quaternion.Identity;
        SetWorldRotation(bone, Quaternion.Normalize(correction * bone.WorldRotation));

        // Leg corrections may rotate the entire foot subtree. Unity preserves the
        // foot orientation while validating upper and lower leg directions.
        if (foot is not null)
            SetWorldRotation(foot, savedFootRotation);
    }

    /// <summary>Builds a lightweight hierarchy from raw local bind matrices.</summary>
    private static CanonicalNode? BuildHierarchy(
        SceneNode sceneNode,
        CanonicalNode? parent,
        IReadOnlyDictionary<SceneNode, Matrix4x4> localBinds,
        Dictionary<SceneNode, CanonicalNode> hierarchy)
    {
        if (!localBinds.TryGetValue(sceneNode, out Matrix4x4 localMatrix) ||
            !Matrix4x4.Decompose(localMatrix, out Vector3 localScale, out Quaternion localRotation, out Vector3 localPosition))
            return null;

        var node = new CanonicalNode(parent, localPosition, Quaternion.Normalize(localRotation), localScale);
        hierarchy[sceneNode] = node;

        foreach (TransformBase childTransform in sceneNode.Transform.Children)
        {
            if (childTransform.SceneNode is not SceneNode childSceneNode)
                continue;

            CanonicalNode? child = BuildHierarchy(childSceneNode, node, localBinds, hierarchy);
            if (child is not null)
                node.Children.Add(child);
        }

        return node;
    }

    /// <summary>Rebuilds world matrices after an authoring-only local rotation change.</summary>
    private static void RecomputeWorldTransforms(CanonicalNode node)
    {
        node.LocalMatrix = Matrix4x4.CreateScale(node.LocalScale) *
            Matrix4x4.CreateFromQuaternion(node.LocalRotation) *
            Matrix4x4.CreateTranslation(node.LocalPosition);
        node.WorldMatrix = node.Parent is null
            ? node.LocalMatrix
            : node.LocalMatrix * node.Parent.WorldMatrix;

        // Rotation composition remains meaningful for animation-channel authoring.
        // Positions, directions and exported transforms use WorldMatrix so nonuniform
        // parent scale follows normal affine transform semantics.
        node.WorldRotation = node.Parent is null
            ? node.LocalRotation
            : Quaternion.Normalize(node.Parent.WorldRotation * node.LocalRotation);

        foreach (CanonicalNode child in node.Children)
            RecomputeWorldTransforms(child);
    }

    private static void SetWorldRotation(CanonicalNode node, Quaternion worldRotation)
    {
        node.LocalRotation = node.Parent is null
            ? worldRotation
            : Quaternion.Normalize(Quaternion.Inverse(node.Parent.WorldRotation) * worldRotation);
        RecomputeWorldTransforms(node);
    }

    private static bool TryCreateBodyFrame(
        IReadOnlyDictionary<EHumanoidAvatarBoneRole, CanonicalNode> roleBones,
        HumanoidAvatarBodyAxes sourceAxes,
        out BodyFrame frame)
    {
        frame = default;
        if (GetRoleNode(roleBones, EHumanoidAvatarBoneRole.LeftUpperLeg) is not CanonicalNode leftLeg ||
            GetRoleNode(roleBones, EHumanoidAvatarBoneRole.RightUpperLeg) is not CanonicalNode rightLeg ||
            GetRoleNode(roleBones, EHumanoidAvatarBoneRole.LeftUpperArm) is not CanonicalNode leftArm ||
            GetRoleNode(roleBones, EHumanoidAvatarBoneRole.RightUpperArm) is not CanonicalNode rightArm)
            return false;

        if (!TryNormalize(rightLeg.WorldPosition - leftLeg.WorldPosition, out Vector3 legRight) ||
            !TryNormalize(rightArm.WorldPosition - leftArm.WorldPosition, out Vector3 armRight) ||
            !TryNormalize(legRight + armRight, out Vector3 right) ||
            !TryNormalize((leftArm.WorldPosition + rightArm.WorldPosition - leftLeg.WorldPosition - rightLeg.WorldPosition) * 0.5f, out Vector3 up))
            return false;

        Vector3 localUp = new(
            Vector3.Dot(up, sourceAxes.Right),
            Vector3.Dot(up, sourceAxes.Up),
            Vector3.Dot(up, sourceAxes.Forward));
        Vector3 localRight = new(
            Vector3.Dot(right, sourceAxes.Right),
            Vector3.Dot(right, sourceAxes.Up),
            Vector3.Dot(right, sourceAxes.Forward));

        // Keep Unity's sensible-axis snap, which prevents a nearly axis-aligned
        // character from acquiring a noisy frame solely from import precision.
        if (MathF.Abs(localRight.X * localRight.Y) < 0.05f &&
            MathF.Abs(localRight.Y * localRight.Z) < 0.05f &&
            MathF.Abs(localRight.Z * localRight.X) < 0.05f)
        {
            int dominantAxis = MathF.Abs(localUp.Y) > MathF.Abs(localUp.X) ? 1 : 0;
            if (MathF.Abs(localUp.Z) > MathF.Abs(dominantAxis == 0 ? localUp.X : localUp.Y))
                dominantAxis = 2;

            float sign = MathF.Sign(dominantAxis switch { 0 => localUp.X, 1 => localUp.Y, _ => localUp.Z });
            up = dominantAxis switch
            {
                0 => sign * sourceAxes.Right,
                1 => sign * sourceAxes.Up,
                _ => sign * sourceAxes.Forward,
            };
        }

        float handedness = Vector3.Dot(Vector3.Cross(sourceAxes.Right, sourceAxes.Up), sourceAxes.Forward) < 0.0f ? -1.0f : 1.0f;
        if (!TryNormalize(handedness * Vector3.Cross(right, up), out Vector3 forward) ||
            !TryNormalize(handedness * Vector3.Cross(up, forward), out right))
            return false;

        frame = new BodyFrame(right, up, forward);
        return true;
    }

    private static Vector3 GetChildDirection(
        EHumanoidAvatarBoneRole role,
        CanonicalNode bone,
        IReadOnlyDictionary<EHumanoidAvatarBoneRole, CanonicalNode> roleBones)
    {
        foreach (EHumanoidAvatarBoneRole preferredChild in GetPreferredChildren(role))
        {
            if (roleBones.TryGetValue(preferredChild, out CanonicalNode? child))
                return child.WorldPosition - bone.WorldPosition;
        }

        foreach ((EHumanoidAvatarBoneRole candidateRole, CanonicalNode candidate) in roleBones)
        {
            if (GetParentRole(candidateRole) == role)
                return candidate.WorldPosition - bone.WorldPosition;
        }

        // Unmapped helper bones are valid direction sources only when unambiguous.
        return bone.Children.Count == 1
            ? bone.Children[0].WorldPosition - bone.WorldPosition
            : Vector3.Zero;
    }

    private static bool TryGetRule(EHumanoidAvatarBoneRole role, out CanonicalisationRule rule)
    {
        bool isLeft = IsLeft(role);
        bool isFinger = IsFinger(role);
        bool isThumb = IsThumb(role);
        float side = isLeft ? -1.0f : 1.0f;

        Vector3 targetDirection = role switch
        {
            EHumanoidAvatarBoneRole.Hips or EHumanoidAvatarBoneRole.Spine or EHumanoidAvatarBoneRole.Chest or
            EHumanoidAvatarBoneRole.UpperChest or EHumanoidAvatarBoneRole.Neck => Vector3.UnitY,
            EHumanoidAvatarBoneRole.LeftUpperLeg => new Vector3(-0.05f, -1.0f, 0.0f),
            EHumanoidAvatarBoneRole.RightUpperLeg => new Vector3(0.05f, -1.0f, 0.0f),
            EHumanoidAvatarBoneRole.LeftLowerLeg => new Vector3(-0.05f, -1.0f, -0.15f),
            EHumanoidAvatarBoneRole.RightLowerLeg => new Vector3(0.05f, -1.0f, -0.15f),
            EHumanoidAvatarBoneRole.LeftFoot => new Vector3(-0.05f, 0.0f, 1.0f),
            EHumanoidAvatarBoneRole.RightFoot => new Vector3(0.05f, 0.0f, 1.0f),
            EHumanoidAvatarBoneRole.LeftShoulder or EHumanoidAvatarBoneRole.LeftUpperArm or
            EHumanoidAvatarBoneRole.LeftLowerArm or EHumanoidAvatarBoneRole.LeftHand => -Vector3.UnitX,
            EHumanoidAvatarBoneRole.RightShoulder or EHumanoidAvatarBoneRole.RightUpperArm or
            EHumanoidAvatarBoneRole.RightLowerArm or EHumanoidAvatarBoneRole.RightHand => Vector3.UnitX,
            _ when isThumb => new Vector3(side, 0.0f, 1.0f),
            _ when isFinger => side * Vector3.UnitX,
            _ => Vector3.Zero,
        };

        if (targetDirection == Vector3.Zero)
        {
            rule = default;
            return false;
        }

        bool isHand = role is EHumanoidAvatarBoneRole.LeftHand or EHumanoidAvatarBoneRole.RightHand;
        bool isArm = role is EHumanoidAvatarBoneRole.LeftUpperArm or EHumanoidAvatarBoneRole.RightUpperArm or
            EHumanoidAvatarBoneRole.LeftLowerArm or EHumanoidAvatarBoneRole.RightLowerArm;
        bool isLegJoint = role is EHumanoidAvatarBoneRole.LeftLowerLeg or EHumanoidAvatarBoneRole.RightLowerLeg or
            EHumanoidAvatarBoneRole.LeftFoot or EHumanoidAvatarBoneRole.RightFoot;
        bool isUpperLeg = role is EHumanoidAvatarBoneRole.LeftUpperLeg or EHumanoidAvatarBoneRole.RightUpperLeg;

        float maximumErrorDegrees = isArm ? 5.0f :
            isFinger && !IsProximalFinger(role) ? 5.0f :
            isLegJoint ? 20.0f :
            role is EHumanoidAvatarBoneRole.LeftShoulder or EHumanoidAvatarBoneRole.RightShoulder ? 20.0f :
            isUpperLeg ? 15.0f :
            isFinger || isHand ? 10.0f : 30.0f;

        Vector3 planeNormal = role is EHumanoidAvatarBoneRole.LeftFoot or EHumanoidAvatarBoneRole.RightFoot
            ? Vector3.UnitY
            : isHand ? Vector3.UnitZ : Vector3.Zero;
        rule = new CanonicalisationRule(targetDirection, maximumErrorDegrees, isFinger || isHand, planeNormal);
        return true;
    }

    private static EHumanoidAvatarBoneRole? GetParentRole(EHumanoidAvatarBoneRole role) => role switch
    {
        EHumanoidAvatarBoneRole.Spine => EHumanoidAvatarBoneRole.Hips,
        EHumanoidAvatarBoneRole.Chest => EHumanoidAvatarBoneRole.Spine,
        EHumanoidAvatarBoneRole.UpperChest => EHumanoidAvatarBoneRole.Chest,
        EHumanoidAvatarBoneRole.Neck => EHumanoidAvatarBoneRole.UpperChest,
        EHumanoidAvatarBoneRole.Head => EHumanoidAvatarBoneRole.Neck,
        EHumanoidAvatarBoneRole.LeftEye or EHumanoidAvatarBoneRole.RightEye or EHumanoidAvatarBoneRole.Jaw => EHumanoidAvatarBoneRole.Head,
        EHumanoidAvatarBoneRole.LeftShoulder or EHumanoidAvatarBoneRole.RightShoulder => EHumanoidAvatarBoneRole.UpperChest,
        EHumanoidAvatarBoneRole.LeftUpperArm => EHumanoidAvatarBoneRole.LeftShoulder,
        EHumanoidAvatarBoneRole.RightUpperArm => EHumanoidAvatarBoneRole.RightShoulder,
        EHumanoidAvatarBoneRole.LeftLowerArm => EHumanoidAvatarBoneRole.LeftUpperArm,
        EHumanoidAvatarBoneRole.RightLowerArm => EHumanoidAvatarBoneRole.RightUpperArm,
        EHumanoidAvatarBoneRole.LeftHand => EHumanoidAvatarBoneRole.LeftLowerArm,
        EHumanoidAvatarBoneRole.RightHand => EHumanoidAvatarBoneRole.RightLowerArm,
        EHumanoidAvatarBoneRole.LeftUpperLeg or EHumanoidAvatarBoneRole.RightUpperLeg => EHumanoidAvatarBoneRole.Hips,
        EHumanoidAvatarBoneRole.LeftLowerLeg => EHumanoidAvatarBoneRole.LeftUpperLeg,
        EHumanoidAvatarBoneRole.RightLowerLeg => EHumanoidAvatarBoneRole.RightUpperLeg,
        EHumanoidAvatarBoneRole.LeftFoot => EHumanoidAvatarBoneRole.LeftLowerLeg,
        EHumanoidAvatarBoneRole.RightFoot => EHumanoidAvatarBoneRole.RightLowerLeg,
        EHumanoidAvatarBoneRole.LeftToes => EHumanoidAvatarBoneRole.LeftFoot,
        EHumanoidAvatarBoneRole.RightToes => EHumanoidAvatarBoneRole.RightFoot,
        _ => GetFingerParentRole(role),
    };

    private static EHumanoidAvatarBoneRole? GetFingerParentRole(EHumanoidAvatarBoneRole role) => role switch
    {
        EHumanoidAvatarBoneRole.LeftThumbProximal or EHumanoidAvatarBoneRole.LeftIndexProximal or
        EHumanoidAvatarBoneRole.LeftMiddleProximal or EHumanoidAvatarBoneRole.LeftRingProximal or
        EHumanoidAvatarBoneRole.LeftLittleProximal => EHumanoidAvatarBoneRole.LeftHand,
        EHumanoidAvatarBoneRole.RightThumbProximal or EHumanoidAvatarBoneRole.RightIndexProximal or
        EHumanoidAvatarBoneRole.RightMiddleProximal or EHumanoidAvatarBoneRole.RightRingProximal or
        EHumanoidAvatarBoneRole.RightLittleProximal => EHumanoidAvatarBoneRole.RightHand,
        _ when IsFinger(role) => (EHumanoidAvatarBoneRole)((int)role - 1),
        _ => null,
    };

    private static ReadOnlySpan<EHumanoidAvatarBoneRole> GetPreferredChildren(EHumanoidAvatarBoneRole role) => role switch
    {
        EHumanoidAvatarBoneRole.Spine => [EHumanoidAvatarBoneRole.Chest, EHumanoidAvatarBoneRole.UpperChest, EHumanoidAvatarBoneRole.Neck, EHumanoidAvatarBoneRole.Head],
        EHumanoidAvatarBoneRole.Chest => [EHumanoidAvatarBoneRole.UpperChest, EHumanoidAvatarBoneRole.Neck, EHumanoidAvatarBoneRole.Head],
        EHumanoidAvatarBoneRole.UpperChest => [EHumanoidAvatarBoneRole.Neck, EHumanoidAvatarBoneRole.Head],
        EHumanoidAvatarBoneRole.LeftHand => [EHumanoidAvatarBoneRole.LeftMiddleProximal],
        EHumanoidAvatarBoneRole.RightHand => [EHumanoidAvatarBoneRole.RightMiddleProximal],
        _ => [],
    };

    private static bool TryGetBinding(HumanoidAvatarBoneBinding[] bindings, EHumanoidAvatarBoneRole role, out HumanoidAvatarBoneBinding binding)
    {
        int index = (int)role;
        if ((uint)index < (uint)bindings.Length && bindings[index].Role == role)
        {
            binding = bindings[index];
            return true;
        }

        binding = null!;
        return false;
    }

    private static CanonicalNode? GetRoleNode(IReadOnlyDictionary<EHumanoidAvatarBoneRole, CanonicalNode> roleBones, EHumanoidAvatarBoneRole role)
        => roleBones.TryGetValue(role, out CanonicalNode? node) ? node : null;

    private static bool TryNormalize(Vector3 value, out Vector3 normalized)
    {
        float lengthSquared = value.LengthSquared();
        if (!float.IsFinite(lengthSquared) || lengthSquared < 1.0e-12f)
        {
            normalized = Vector3.Zero;
            return false;
        }

        normalized = value / MathF.Sqrt(lengthSquared);
        return true;
    }

    private static float AngleDegrees(Vector3 from, Vector3 to)
        => MathF.Acos(Math.Clamp(Vector3.Dot(Vector3.Normalize(from), Vector3.Normalize(to)), -1.0f, 1.0f)) * 180.0f / MathF.PI;

    /// <summary>Creates a stable shortest-arc rotation, including antiparallel directions.</summary>
    private static Quaternion FromToRotation(Vector3 from, Vector3 to)
    {
        from = Vector3.Normalize(from);
        to = Vector3.Normalize(to);
        float dot = Vector3.Dot(from, to);
        if (dot > 0.999999f)
            return Quaternion.Identity;

        if (dot < -0.999999f)
        {
            Vector3 axis = MathF.Abs(from.X) < 0.9f
                ? Vector3.Cross(from, Vector3.UnitX)
                : Vector3.Cross(from, Vector3.UnitY);
            return Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), MathF.PI);
        }

        return Quaternion.Normalize(new Quaternion(Vector3.Cross(from, to), 1.0f + dot));
    }

    private static bool IsProximalFinger(EHumanoidAvatarBoneRole role) => role is
        EHumanoidAvatarBoneRole.LeftThumbProximal or EHumanoidAvatarBoneRole.RightThumbProximal or
        EHumanoidAvatarBoneRole.LeftIndexProximal or EHumanoidAvatarBoneRole.RightIndexProximal or
        EHumanoidAvatarBoneRole.LeftMiddleProximal or EHumanoidAvatarBoneRole.RightMiddleProximal or
        EHumanoidAvatarBoneRole.LeftRingProximal or EHumanoidAvatarBoneRole.RightRingProximal or
        EHumanoidAvatarBoneRole.LeftLittleProximal or EHumanoidAvatarBoneRole.RightLittleProximal;

    private static bool IsFinger(EHumanoidAvatarBoneRole role)
        => role is >= EHumanoidAvatarBoneRole.LeftThumbProximal and <= EHumanoidAvatarBoneRole.RightLittleDistal;

    private static bool IsThumb(EHumanoidAvatarBoneRole role) => role is
        EHumanoidAvatarBoneRole.LeftThumbProximal or EHumanoidAvatarBoneRole.LeftThumbIntermediate or EHumanoidAvatarBoneRole.LeftThumbDistal or
        EHumanoidAvatarBoneRole.RightThumbProximal or EHumanoidAvatarBoneRole.RightThumbIntermediate or EHumanoidAvatarBoneRole.RightThumbDistal;

    private static bool IsLeft(EHumanoidAvatarBoneRole role) => role is
        >= EHumanoidAvatarBoneRole.LeftEye and <= EHumanoidAvatarBoneRole.LeftHand or
        >= EHumanoidAvatarBoneRole.LeftUpperLeg and <= EHumanoidAvatarBoneRole.LeftToes or
        >= EHumanoidAvatarBoneRole.LeftThumbProximal and <= EHumanoidAvatarBoneRole.LeftLittleDistal;

    private static readonly EHumanoidAvatarBoneRole[] s_UnityValidationOrder =
    [
        EHumanoidAvatarBoneRole.Hips,
        EHumanoidAvatarBoneRole.LeftUpperLeg, EHumanoidAvatarBoneRole.RightUpperLeg,
        EHumanoidAvatarBoneRole.LeftLowerLeg, EHumanoidAvatarBoneRole.RightLowerLeg,
        EHumanoidAvatarBoneRole.LeftFoot, EHumanoidAvatarBoneRole.RightFoot,
        EHumanoidAvatarBoneRole.Spine, EHumanoidAvatarBoneRole.Chest, EHumanoidAvatarBoneRole.Neck, EHumanoidAvatarBoneRole.Head,
        EHumanoidAvatarBoneRole.LeftShoulder, EHumanoidAvatarBoneRole.RightShoulder,
        EHumanoidAvatarBoneRole.LeftUpperArm, EHumanoidAvatarBoneRole.RightUpperArm,
        EHumanoidAvatarBoneRole.LeftLowerArm, EHumanoidAvatarBoneRole.RightLowerArm,
        EHumanoidAvatarBoneRole.LeftHand, EHumanoidAvatarBoneRole.RightHand,
        EHumanoidAvatarBoneRole.LeftThumbProximal, EHumanoidAvatarBoneRole.LeftThumbIntermediate, EHumanoidAvatarBoneRole.LeftThumbDistal,
        EHumanoidAvatarBoneRole.LeftIndexProximal, EHumanoidAvatarBoneRole.LeftIndexIntermediate, EHumanoidAvatarBoneRole.LeftIndexDistal,
        EHumanoidAvatarBoneRole.LeftMiddleProximal, EHumanoidAvatarBoneRole.LeftMiddleIntermediate, EHumanoidAvatarBoneRole.LeftMiddleDistal,
        EHumanoidAvatarBoneRole.LeftRingProximal, EHumanoidAvatarBoneRole.LeftRingIntermediate, EHumanoidAvatarBoneRole.LeftRingDistal,
        EHumanoidAvatarBoneRole.LeftLittleProximal, EHumanoidAvatarBoneRole.LeftLittleIntermediate, EHumanoidAvatarBoneRole.LeftLittleDistal,
        EHumanoidAvatarBoneRole.RightThumbProximal, EHumanoidAvatarBoneRole.RightThumbIntermediate, EHumanoidAvatarBoneRole.RightThumbDistal,
        EHumanoidAvatarBoneRole.RightIndexProximal, EHumanoidAvatarBoneRole.RightIndexIntermediate, EHumanoidAvatarBoneRole.RightIndexDistal,
        EHumanoidAvatarBoneRole.RightMiddleProximal, EHumanoidAvatarBoneRole.RightMiddleIntermediate, EHumanoidAvatarBoneRole.RightMiddleDistal,
        EHumanoidAvatarBoneRole.RightRingProximal, EHumanoidAvatarBoneRole.RightRingIntermediate, EHumanoidAvatarBoneRole.RightRingDistal,
        EHumanoidAvatarBoneRole.RightLittleProximal, EHumanoidAvatarBoneRole.RightLittleIntermediate, EHumanoidAvatarBoneRole.RightLittleDistal,
        EHumanoidAvatarBoneRole.UpperChest,
    ];

    private readonly record struct CanonicalisationRule(Vector3 TargetDirection, float MaximumErrorDegrees, bool IsLocal, Vector3 PlaneNormal);

    private readonly record struct BodyFrame(Vector3 Right, Vector3 Up, Vector3 Forward)
    {
        public Vector3 Map(Vector3 localDirection)
            => Vector3.Normalize(localDirection.X * Right + localDirection.Y * Up + localDirection.Z * Forward);
    }

    /// <summary>Temporary full-hierarchy bind-pose node used only during avatar authoring.</summary>
    private sealed class CanonicalNode
    {
        public CanonicalNode? Parent { get; }
        public List<CanonicalNode> Children { get; } = [];
        public Vector3 LocalPosition { get; }
        public Quaternion LocalRotation { get; set; }
        public Vector3 LocalScale { get; }
        public Matrix4x4 LocalMatrix { get; set; }
        public Matrix4x4 WorldMatrix { get; set; }
        public Quaternion WorldRotation { get; set; }
        public Vector3 WorldPosition => WorldMatrix.Translation;

        public CanonicalNode(CanonicalNode? parent, Vector3 localPosition, Quaternion localRotation, Vector3 localScale)
        {
            Parent = parent;
            LocalPosition = localPosition;
            LocalRotation = localRotation;
            LocalScale = localScale;
        }
    }
}
