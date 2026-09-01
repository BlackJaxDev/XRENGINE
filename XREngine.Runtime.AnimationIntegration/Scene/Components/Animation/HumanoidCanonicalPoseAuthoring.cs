using System.Numerics;

namespace XREngine.Components.Animation;

/// <summary>
/// Authors the avatar-independent Mecanim neutral posture and continuous
/// anatomical joint frames from a mapped bind skeleton. The posture constants
/// are a versioned interoperability contract; the basis is always derived from
/// the current avatar's geometry and is never sampled from a reference avatar.
/// </summary>
internal static class HumanoidCanonicalPoseAuthoring
{
    /// <summary>Versioned contract for generated canonical-pose corrections.</summary>
    public const string CurrentModelId = "XRE.MecanimCanonicalPose.2022.3.v2";
    public const string NeutralPostureModelId = "XRE.MecanimNeutralPosture.2022.3.v1";

    /// <summary>
    /// Replaces generated canonical corrections while retaining explicitly
    /// authored corrections selected by the caller.
    /// </summary>
    public static void ApplyGeneratedCorrections(
        HumanoidAvatarBoneBinding[] bindings,
        HumanoidAvatarBodyAxes bodyAxes,
        ReadOnlySpan<bool> preserveAuthoredCorrection)
    {
        for (int i = 0; i < bindings.Length; i++)
        {
            HumanoidAvatarBoneBinding binding = bindings[i];
            if (binding.NodePath.Length == 0
                || ((uint)i < (uint)preserveAuthoredCorrection.Length && preserveAuthoredCorrection[i]))
                continue;

            if (!TryCreateJointBasis(
                    bindings,
                    bodyAxes,
                    binding.Role,
                    out Quaternion jointBasis))
            {
                binding.CanonicalPoseCorrection = Quaternion.Identity;
                continue;
            }

            Quaternion neutralBasis = TryCreateNeutralPostureBasis(
                bindings,
                bodyAxes,
                binding.Role,
                out Quaternion authoredNeutralBasis)
                ? authoredNeutralBasis
                : jointBasis;
            Quaternion rawToCanonical = Quaternion.Normalize(
                Quaternion.Inverse(binding.NeutralLocalRotation) * binding.CanonicalLocalRotation);
            binding.CanonicalPoseCorrection = Quaternion.Normalize(rawToCanonical * CreateCanonicalCorrection(
                binding.Role,
                neutralBasis));
        }
    }

    /// <summary>
    /// Builds the body-aligned upper-arm frame used by Mecanim's universal
    /// relaxed posture. Runtime muscle deltas retain their actual chain frame;
    /// separating the two prevents a slightly non-horizontal bind arm from
    /// turning the universal neutral correction into avatar-specific data.
    /// </summary>
    private static bool TryCreateNeutralPostureBasis(
        ReadOnlySpan<HumanoidAvatarBoneBinding> bindings,
        HumanoidAvatarBodyAxes bodyAxes,
        EHumanoidAvatarBoneRole role,
        out Quaternion basis)
    {
        basis = Quaternion.Identity;
        if (!TryGetBinding(bindings, role, out HumanoidAvatarBoneBinding binding)
            || !TryGetWorldRotation(binding.CanonicalWorldTransform, out Quaternion bindWorldRotation))
            return false;

        bool isUpperArm = role is EHumanoidAvatarBoneRole.LeftUpperArm
            or EHumanoidAvatarBoneRole.RightUpperArm;
        if (!isUpperArm && !ShouldPreserveCanonicalY(role))
            return false;

        float side = IsLeftRole(role) ? 1.0f : -1.0f;
        float handedness = GetFrameHandedness(bodyAxes);
        Vector3 xWorld;
        Vector3 yWorld;
        if (isUpperArm)
        {
            xWorld = bodyAxes.Up * handedness;
            yWorld = -side * bodyAxes.Right * handedness;
        }
        else if (IsThumbRole(role))
        {
            // Mecanim authors zero-muscle thumb corrections in a body-aligned
            // reference frame that is separate from the live phalanx frame.
            bool isProximal = role is EHumanoidAvatarBoneRole.LeftThumbProximal
                or EHumanoidAvatarBoneRole.RightThumbProximal;
            xWorld = isProximal
                ? handedness * side * bodyAxes.Forward
                : -handedness * side * bodyAxes.Up;
            yWorld = -handedness * side * bodyAxes.Right;
        }
        else
        {
            if (!TryGetCanonicalAxes(
                    bindings,
                    bodyAxes,
                    role,
                    handedness,
                    out xWorld,
                    out yWorld))
                return false;

            xWorld *= handedness;
            yWorld *= handedness;
        }

        return HumanoidJointFrameAuthoring.TryCreateJointBasis(
            bindWorldRotation,
            xWorld,
            yWorld,
            preserveCanonicalY: false,
            out basis);
    }

    /// <summary>
    /// Builds the proper rotation from canonical joint coordinates to the
    /// role's bind-local coordinates.
    /// </summary>
    public static bool TryCreateJointBasis(
        ReadOnlySpan<HumanoidAvatarBoneBinding> bindings,
        HumanoidAvatarBodyAxes bodyAxes,
        EHumanoidAvatarBoneRole role,
        out Quaternion basis)
    {
        basis = Quaternion.Identity;
        float handedness = GetFrameHandedness(bodyAxes);
        if (!bodyAxes.IsFiniteOrthonormal()
            || !TryGetBinding(bindings, role, out HumanoidAvatarBoneBinding binding)
            || !TryGetWorldRotation(binding.CanonicalWorldTransform, out Quaternion bindWorldRotation)
            || !TryGetCanonicalAxes(
                bindings,
                bodyAxes,
                role,
                handedness,
                out Vector3 xWorld,
                out Vector3 yWorld))
            return false;

        // Imported skeleton coordinates may be reflected. Rotation axes are
        // axial vectors, so the first two polar anatomy axes acquire the frame
        // handedness while their cross-product axis already carries it.
        xWorld *= handedness;
        yWorld *= handedness;

        return HumanoidJointFrameAuthoring.TryCreateJointBasis(
            bindWorldRotation,
            xWorld,
            yWorld,
            ShouldPreserveCanonicalY(role),
            out basis);
    }

    private static Quaternion CreateCanonicalCorrection(
        EHumanoidAvatarBoneRole role,
        Quaternion basis)
    {
        Vector3 tangent = GetNeutralTangent(role);
        if (tangent == Vector3.Zero)
            return Quaternion.Identity;

        Quaternion neutral = Quaternion.Normalize(new Quaternion(
            tangent.X,
            tangent.Y,
            tangent.Z,
            1.0f));
        return Quaternion.Normalize(basis * neutral * Quaternion.Inverse(basis));
    }

    private static Vector3 GetNeutralTangent(EHumanoidAvatarBoneRole role)
    {
        if (role is EHumanoidAvatarBoneRole.LeftUpperLeg
            or EHumanoidAvatarBoneRole.RightUpperLeg)
            return new Vector3(-0.268f, 0.0f, 0.0f);
        if (role is EHumanoidAvatarBoneRole.LeftLowerLeg
            or EHumanoidAvatarBoneRole.RightLowerLeg)
            return new Vector3(0.839f, 0.0f, 0.0f);
        if (role == EHumanoidAvatarBoneRole.LeftUpperArm)
            return new Vector3(0.268f, 0.0f, 0.364f);
        if (role == EHumanoidAvatarBoneRole.RightUpperArm)
            return new Vector3(-0.268f, 0.0f, 0.364f);
        if (role == EHumanoidAvatarBoneRole.LeftLowerArm)
            return new Vector3(0.839f, 0.0f, 0.0f);
        if (role == EHumanoidAvatarBoneRole.RightLowerArm)
            return new Vector3(-0.839f, 0.0f, 0.0f);
        if (role == EHumanoidAvatarBoneRole.Jaw)
            return new Vector3(0.09f, 0.0f, 0.0f);

        bool isLeft = IsLeftRole(role);
        float side = isLeft ? 1.0f : -1.0f;
        return role switch
        {
            EHumanoidAvatarBoneRole.LeftThumbProximal
                or EHumanoidAvatarBoneRole.RightThumbProximal
                => new Vector3(0.125f, 0.0f, -side * 0.125f),
            EHumanoidAvatarBoneRole.LeftThumbIntermediate
                or EHumanoidAvatarBoneRole.RightThumbIntermediate
                or EHumanoidAvatarBoneRole.LeftThumbDistal
                or EHumanoidAvatarBoneRole.RightThumbDistal
                => new Vector3(0.2f, 0.0f, 0.0f),
            EHumanoidAvatarBoneRole.LeftIndexProximal
                or EHumanoidAvatarBoneRole.RightIndexProximal
                => new Vector3(0.3f, 0.0f, -side * 0.08f),
            EHumanoidAvatarBoneRole.LeftMiddleProximal
                or EHumanoidAvatarBoneRole.RightMiddleProximal
                => new Vector3(0.3f, 0.0f, -side * 0.04f),
            EHumanoidAvatarBoneRole.LeftRingProximal
                or EHumanoidAvatarBoneRole.RightRingProximal
                => new Vector3(0.3f, 0.0f, side * 0.04f),
            EHumanoidAvatarBoneRole.LeftLittleProximal
                or EHumanoidAvatarBoneRole.RightLittleProximal
                => new Vector3(0.3f, 0.0f, side * 0.08f),
            _ when IsNonThumbFingerIntermediateOrDistal(role)
                => new Vector3(0.33f, 0.0f, 0.0f),
            _ => Vector3.Zero,
        };
    }

    private static bool TryGetCanonicalAxes(
        ReadOnlySpan<HumanoidAvatarBoneBinding> bindings,
        HumanoidAvatarBodyAxes bodyAxes,
        EHumanoidAvatarBoneRole role,
        float handedness,
        out Vector3 xWorld,
        out Vector3 yWorld)
    {
        bool isLeft = IsLeftRole(role);
        float side = isLeft ? 1.0f : -1.0f;

        if (role == EHumanoidAvatarBoneRole.Head)
        {
            // Mecanim's head frame is body-aligned. Eye placement and a
            // slightly tilted terminal neck segment must not skew turn/tilt.
            xWorld = bodyAxes.Right;
            yWorld = bodyAxes.Up;
            return true;
        }

        if (role is EHumanoidAvatarBoneRole.LeftEye
            or EHumanoidAvatarBoneRole.RightEye
            or EHumanoidAvatarBoneRole.Jaw)
        {
            xWorld = bodyAxes.Right;
            yWorld = bodyAxes.Forward;
            return true;
        }

        if (role is EHumanoidAvatarBoneRole.LeftHand
            or EHumanoidAvatarBoneRole.RightHand)
        {
            xWorld = bodyAxes.Up;
            return TryGetIncomingChainDirection(bindings, role, out yWorld);
        }

        if (IsFingerRole(role))
        {
            if (!TryGetChainDirection(bindings, role, allowParentFallback: true, out yWorld))
            {
                xWorld = Vector3.Zero;
                return false;
            }

            xWorld = IsThumbRole(role)
                ? -side * bodyAxes.Up
                : side * bodyAxes.Forward;
            return IsUsableDirection(xWorld);
        }

        xWorld = IsUpperLimbRole(role) ? bodyAxes.Up : bodyAxes.Right;
        bool allowParentFallback = role is not EHumanoidAvatarBoneRole.LeftFoot
            and not EHumanoidAvatarBoneRole.RightFoot;
        if (TryGetChainDirection(bindings, role, allowParentFallback, out yWorld))
            return true;

        yWorld = role is EHumanoidAvatarBoneRole.LeftFoot
            or EHumanoidAvatarBoneRole.RightFoot
            or EHumanoidAvatarBoneRole.LeftToes
            or EHumanoidAvatarBoneRole.RightToes
            ? bodyAxes.Forward
            : bodyAxes.Up;
        return true;
    }

    private static bool TryGetChainDirection(
        ReadOnlySpan<HumanoidAvatarBoneBinding> bindings,
        EHumanoidAvatarBoneRole role,
        bool allowParentFallback,
        out Vector3 direction)
    {
        direction = Vector3.Zero;
        if (!TryGetBinding(bindings, role, out HumanoidAvatarBoneBinding binding))
            return false;

        EHumanoidAvatarBoneRole[] candidates = GetChainCandidates(role);
        for (int i = 0; i < candidates.Length; i++)
        {
            if (!TryGetBinding(bindings, candidates[i], out HumanoidAvatarBoneBinding child))
                continue;
            direction = child.CanonicalWorldTransform.Translation - binding.CanonicalWorldTransform.Translation;
            if (IsUsableDirection(direction))
                return true;
        }

        if (allowParentFallback
            && binding.ParentRole.HasValue
            && TryGetBinding(bindings, binding.ParentRole.Value, out HumanoidAvatarBoneBinding parent))
        {
            direction = binding.CanonicalWorldTransform.Translation - parent.CanonicalWorldTransform.Translation;
            if (IsUsableDirection(direction))
                return true;
        }

        direction = Vector3.Zero;
        return false;
    }

    private static bool TryGetIncomingChainDirection(
        ReadOnlySpan<HumanoidAvatarBoneBinding> bindings,
        EHumanoidAvatarBoneRole role,
        out Vector3 direction)
    {
        direction = Vector3.Zero;
        if (!TryGetBinding(bindings, role, out HumanoidAvatarBoneBinding binding)
            || !binding.ParentRole.HasValue
            || !TryGetBinding(bindings, binding.ParentRole.Value, out HumanoidAvatarBoneBinding parent))
            return false;

        direction = binding.CanonicalWorldTransform.Translation - parent.CanonicalWorldTransform.Translation;
        return IsUsableDirection(direction);
    }

    private static EHumanoidAvatarBoneRole[] GetChainCandidates(EHumanoidAvatarBoneRole role)
        => role switch
        {
            EHumanoidAvatarBoneRole.Hips => [EHumanoidAvatarBoneRole.Spine],
            EHumanoidAvatarBoneRole.Spine => [EHumanoidAvatarBoneRole.Chest, EHumanoidAvatarBoneRole.UpperChest, EHumanoidAvatarBoneRole.Neck, EHumanoidAvatarBoneRole.Head],
            EHumanoidAvatarBoneRole.Chest => [EHumanoidAvatarBoneRole.UpperChest, EHumanoidAvatarBoneRole.Neck, EHumanoidAvatarBoneRole.Head],
            EHumanoidAvatarBoneRole.UpperChest => [EHumanoidAvatarBoneRole.Neck, EHumanoidAvatarBoneRole.Head],
            EHumanoidAvatarBoneRole.Neck => [EHumanoidAvatarBoneRole.Head],
            EHumanoidAvatarBoneRole.LeftShoulder => [EHumanoidAvatarBoneRole.LeftUpperArm],
            EHumanoidAvatarBoneRole.LeftUpperArm => [EHumanoidAvatarBoneRole.LeftLowerArm],
            EHumanoidAvatarBoneRole.LeftLowerArm => [EHumanoidAvatarBoneRole.LeftHand],
            EHumanoidAvatarBoneRole.LeftHand => [EHumanoidAvatarBoneRole.LeftMiddleProximal, EHumanoidAvatarBoneRole.LeftIndexProximal],
            EHumanoidAvatarBoneRole.RightShoulder => [EHumanoidAvatarBoneRole.RightUpperArm],
            EHumanoidAvatarBoneRole.RightUpperArm => [EHumanoidAvatarBoneRole.RightLowerArm],
            EHumanoidAvatarBoneRole.RightLowerArm => [EHumanoidAvatarBoneRole.RightHand],
            EHumanoidAvatarBoneRole.RightHand => [EHumanoidAvatarBoneRole.RightMiddleProximal, EHumanoidAvatarBoneRole.RightIndexProximal],
            EHumanoidAvatarBoneRole.LeftUpperLeg => [EHumanoidAvatarBoneRole.LeftLowerLeg],
            EHumanoidAvatarBoneRole.LeftLowerLeg => [EHumanoidAvatarBoneRole.LeftFoot],
            EHumanoidAvatarBoneRole.LeftFoot => [EHumanoidAvatarBoneRole.LeftToes],
            EHumanoidAvatarBoneRole.RightUpperLeg => [EHumanoidAvatarBoneRole.RightLowerLeg],
            EHumanoidAvatarBoneRole.RightLowerLeg => [EHumanoidAvatarBoneRole.RightFoot],
            EHumanoidAvatarBoneRole.RightFoot => [EHumanoidAvatarBoneRole.RightToes],
            EHumanoidAvatarBoneRole.LeftThumbProximal => [EHumanoidAvatarBoneRole.LeftThumbIntermediate],
            EHumanoidAvatarBoneRole.LeftThumbIntermediate => [EHumanoidAvatarBoneRole.LeftThumbDistal],
            EHumanoidAvatarBoneRole.LeftIndexProximal => [EHumanoidAvatarBoneRole.LeftIndexIntermediate],
            EHumanoidAvatarBoneRole.LeftIndexIntermediate => [EHumanoidAvatarBoneRole.LeftIndexDistal],
            EHumanoidAvatarBoneRole.LeftMiddleProximal => [EHumanoidAvatarBoneRole.LeftMiddleIntermediate],
            EHumanoidAvatarBoneRole.LeftMiddleIntermediate => [EHumanoidAvatarBoneRole.LeftMiddleDistal],
            EHumanoidAvatarBoneRole.LeftRingProximal => [EHumanoidAvatarBoneRole.LeftRingIntermediate],
            EHumanoidAvatarBoneRole.LeftRingIntermediate => [EHumanoidAvatarBoneRole.LeftRingDistal],
            EHumanoidAvatarBoneRole.LeftLittleProximal => [EHumanoidAvatarBoneRole.LeftLittleIntermediate],
            EHumanoidAvatarBoneRole.LeftLittleIntermediate => [EHumanoidAvatarBoneRole.LeftLittleDistal],
            EHumanoidAvatarBoneRole.RightThumbProximal => [EHumanoidAvatarBoneRole.RightThumbIntermediate],
            EHumanoidAvatarBoneRole.RightThumbIntermediate => [EHumanoidAvatarBoneRole.RightThumbDistal],
            EHumanoidAvatarBoneRole.RightIndexProximal => [EHumanoidAvatarBoneRole.RightIndexIntermediate],
            EHumanoidAvatarBoneRole.RightIndexIntermediate => [EHumanoidAvatarBoneRole.RightIndexDistal],
            EHumanoidAvatarBoneRole.RightMiddleProximal => [EHumanoidAvatarBoneRole.RightMiddleIntermediate],
            EHumanoidAvatarBoneRole.RightMiddleIntermediate => [EHumanoidAvatarBoneRole.RightMiddleDistal],
            EHumanoidAvatarBoneRole.RightRingProximal => [EHumanoidAvatarBoneRole.RightRingIntermediate],
            EHumanoidAvatarBoneRole.RightRingIntermediate => [EHumanoidAvatarBoneRole.RightRingDistal],
            EHumanoidAvatarBoneRole.RightLittleProximal => [EHumanoidAvatarBoneRole.RightLittleIntermediate],
            EHumanoidAvatarBoneRole.RightLittleIntermediate => [EHumanoidAvatarBoneRole.RightLittleDistal],
            _ => [],
        };

    private static bool TryGetBinding(
        ReadOnlySpan<HumanoidAvatarBoneBinding> bindings,
        EHumanoidAvatarBoneRole role,
        out HumanoidAvatarBoneBinding binding)
    {
        int directIndex = (int)role;
        if ((uint)directIndex < (uint)bindings.Length
            && bindings[directIndex].Role == role
            && bindings[directIndex].NodePath.Length != 0)
        {
            binding = bindings[directIndex];
            return true;
        }

        for (int i = 0; i < bindings.Length; i++)
        {
            if (bindings[i].Role != role || bindings[i].NodePath.Length == 0)
                continue;
            binding = bindings[i];
            return true;
        }

        binding = null!;
        return false;
    }

    private static bool TryGetWorldRotation(Matrix4x4 matrix, out Quaternion rotation)
    {
        if (!Matrix4x4.Decompose(matrix, out _, out rotation, out _)
            || !IsFinite(rotation)
            || rotation.LengthSquared() <= 1e-12f)
        {
            rotation = Quaternion.Identity;
            return false;
        }

        rotation = Quaternion.Normalize(rotation);
        return true;
    }

    private static bool IsUpperLimbRole(EHumanoidAvatarBoneRole role)
        => role is EHumanoidAvatarBoneRole.LeftShoulder
            or EHumanoidAvatarBoneRole.LeftUpperArm
            or EHumanoidAvatarBoneRole.LeftLowerArm
            or EHumanoidAvatarBoneRole.LeftHand
            or EHumanoidAvatarBoneRole.RightShoulder
            or EHumanoidAvatarBoneRole.RightUpperArm
            or EHumanoidAvatarBoneRole.RightLowerArm
            or EHumanoidAvatarBoneRole.RightHand;

    private static bool IsFingerRole(EHumanoidAvatarBoneRole role)
        => role >= EHumanoidAvatarBoneRole.LeftThumbProximal;

    private static bool IsThumbIntermediateOrDistal(EHumanoidAvatarBoneRole role)
        => role is EHumanoidAvatarBoneRole.LeftThumbIntermediate
            or EHumanoidAvatarBoneRole.LeftThumbDistal
            or EHumanoidAvatarBoneRole.RightThumbIntermediate
            or EHumanoidAvatarBoneRole.RightThumbDistal;

    private static bool IsThumbRole(EHumanoidAvatarBoneRole role)
        => role is >= EHumanoidAvatarBoneRole.LeftThumbProximal and <= EHumanoidAvatarBoneRole.LeftThumbDistal
            or >= EHumanoidAvatarBoneRole.RightThumbProximal and <= EHumanoidAvatarBoneRole.RightThumbDistal;

    /// <summary>
    /// These roles derive canonical Y from an observed segment direction.
    /// Mecanim preserves that anatomical long axis and projects the reference
    /// bend axis into its perpendicular plane.
    /// </summary>
    private static bool ShouldPreserveCanonicalY(EHumanoidAvatarBoneRole role)
        => role is EHumanoidAvatarBoneRole.Neck
            or EHumanoidAvatarBoneRole.Head
            or EHumanoidAvatarBoneRole.LeftShoulder
            or EHumanoidAvatarBoneRole.RightShoulder
            or EHumanoidAvatarBoneRole.LeftLowerArm
            or EHumanoidAvatarBoneRole.RightLowerArm
            or EHumanoidAvatarBoneRole.LeftHand
            or EHumanoidAvatarBoneRole.RightHand
            or EHumanoidAvatarBoneRole.LeftUpperLeg
            or EHumanoidAvatarBoneRole.RightUpperLeg
            or EHumanoidAvatarBoneRole.LeftLowerLeg
            or EHumanoidAvatarBoneRole.RightLowerLeg
            || IsFingerRole(role);

    private static float GetFrameHandedness(HumanoidAvatarBodyAxes bodyAxes)
        => Vector3.Dot(
            Vector3.Cross(bodyAxes.Right, bodyAxes.Up),
            bodyAxes.Forward) < 0.0f ? -1.0f : 1.0f;

    private static bool IsNonThumbFingerIntermediateOrDistal(EHumanoidAvatarBoneRole role)
        => role is EHumanoidAvatarBoneRole.LeftIndexIntermediate
            or EHumanoidAvatarBoneRole.LeftIndexDistal
            or EHumanoidAvatarBoneRole.LeftMiddleIntermediate
            or EHumanoidAvatarBoneRole.LeftMiddleDistal
            or EHumanoidAvatarBoneRole.LeftRingIntermediate
            or EHumanoidAvatarBoneRole.LeftRingDistal
            or EHumanoidAvatarBoneRole.LeftLittleIntermediate
            or EHumanoidAvatarBoneRole.LeftLittleDistal
            or EHumanoidAvatarBoneRole.RightIndexIntermediate
            or EHumanoidAvatarBoneRole.RightIndexDistal
            or EHumanoidAvatarBoneRole.RightMiddleIntermediate
            or EHumanoidAvatarBoneRole.RightMiddleDistal
            or EHumanoidAvatarBoneRole.RightRingIntermediate
            or EHumanoidAvatarBoneRole.RightRingDistal
            or EHumanoidAvatarBoneRole.RightLittleIntermediate
            or EHumanoidAvatarBoneRole.RightLittleDistal;

    private static bool IsLeftRole(EHumanoidAvatarBoneRole role)
        => role is >= EHumanoidAvatarBoneRole.LeftEye and <= EHumanoidAvatarBoneRole.LeftHand
            or >= EHumanoidAvatarBoneRole.LeftUpperLeg and <= EHumanoidAvatarBoneRole.LeftToes
            or >= EHumanoidAvatarBoneRole.LeftThumbProximal and <= EHumanoidAvatarBoneRole.LeftLittleDistal;

    private static bool IsUsableDirection(Vector3 value)
        => float.IsFinite(value.X)
        && float.IsFinite(value.Y)
        && float.IsFinite(value.Z)
        && value.LengthSquared() > 1e-12f;

    private static bool IsFinite(Quaternion value)
        => float.IsFinite(value.X)
        && float.IsFinite(value.Y)
        && float.IsFinite(value.Z)
        && float.IsFinite(value.W);
}
