using System.Numerics;

namespace XREngine.Components.Animation;

/// <summary>
/// Allocation-free canonical humanoid joint math for compiled avatar plans.
/// Matrices and quaternions follow the engine's row-vector convention.
/// </summary>
internal static class CompiledHumanoidPoseSolver
{
    private const float DegreesToRadians = MathF.PI / 180.0f;

    /// <summary>
    /// Evaluates the finalized local rotation around the compiled rest joint.
    /// </summary>
    public static Quaternion EvaluateLocalRotation(
        in CompiledHumanoidBoneSolvePlan plan,
        float twistDegrees,
        float frontBackDegrees,
        float leftRightDegrees)
    {
        Vector3 localDegrees = MapMusclesToLocalAxes(
            plan,
            twistDegrees,
            frontBackDegrees,
            leftRightDegrees);

        Vector3 centerDegrees = plan.JointLimit.UseDefaultValues
            ? Vector3.Zero
            : plan.JointLimit.CenterDegrees;
        Quaternion centerRotation;
        Quaternion jointRotation;
        if (plan.HasContinuousJointBasis)
        {
            centerRotation = CreateTangentRotation(centerDegrees);
            if (UsesCoupledFootSwing(plan.Role))
            {
                jointRotation = CreateTangentRotation(centerDegrees + localDegrees);
            }
            else
            {
                Vector3 swingDegrees = MapMusclesToLocalAxes(
                    plan,
                    0.0f,
                    frontBackDegrees,
                    leftRightDegrees);
                Vector3 twistAxisDegrees = MapMusclesToLocalAxes(
                    plan,
                    twistDegrees,
                    0.0f,
                    0.0f);
                jointRotation = Quaternion.Normalize(
                    CreateTangentRotation(centerDegrees + swingDegrees)
                    * CreateTangentRotation(twistAxisDegrees));
            }
        }
        else
        {
            Vector3 jointDegrees = centerDegrees + localDegrees;
            centerRotation = CreateOrderedRotation(centerDegrees, plan.RotationOrder);
            jointRotation = CreateOrderedRotation(jointDegrees, plan.RotationOrder);
        }
        Quaternion result = Quaternion.Normalize(
            plan.ZeroMuscleRotation
            * plan.JointBasisToZeroLocal
            * Quaternion.Inverse(centerRotation)
            * jointRotation
            * Quaternion.Inverse(plan.JointBasisToZeroLocal));
        return Quaternion.Dot(result, plan.ZeroMuscleRotation) < 0.0f
            ? new Quaternion(-result.X, -result.Y, -result.Z, -result.W)
            : result;
    }

    /// <summary>
    /// Unity's foot in/out channel is part of the continuous two-axis foot
    /// swing rather than the axial twist stage used by torso and limb chains.
    /// Keeping it in the coupled joint vector preserves the public foot muscle
    /// response while proximal lower-leg roll remains a separate inherited
    /// rotation.
    /// </summary>
    private static bool UsesCoupledFootSwing(EHumanoidAvatarBoneRole role)
        => role is EHumanoidAvatarBoneRole.LeftFoot or EHumanoidAvatarBoneRole.RightFoot;

    /// <summary>
    /// Applies permitted avatar translation to the compiled neutral local position.
    /// Values are avatar meters and are scaled into model units exactly once.
    /// </summary>
    public static Vector3 EvaluateLocalTranslation(
        in CompiledHumanoidBoneSolvePlan plan,
        Vector3 avatarTranslation,
        float humanScale,
        float modelUnitsPerMeter)
    {
        if (!plan.PermitsTranslationDegreesOfFreedom)
            return plan.NeutralTranslation;

        float scale = float.IsFinite(humanScale) && float.IsFinite(modelUnitsPerMeter)
            ? humanScale * modelUnitsPerMeter
            : 0.0f;
        return float.IsFinite(avatarTranslation.X)
            && float.IsFinite(avatarTranslation.Y)
            && float.IsFinite(avatarTranslation.Z)
            ? plan.NeutralTranslation + avatarTranslation * scale
            : plan.NeutralTranslation;
    }

    private static Vector3 MapMusclesToLocalAxes(
        in CompiledHumanoidBoneSolvePlan plan,
        float twistDegrees,
        float frontBackDegrees,
        float leftRightDegrees)
    {
        if (plan.HasContinuousJointBasis || !plan.HasAxisMapping)
            return new Vector3(frontBackDegrees, twistDegrees, leftRightDegrees);

        BoneAxisMapping mapping = plan.AxisMapping;
        Vector3 result = Vector3.Zero;
        SetAxis(ref result, mapping.TwistAxis, twistDegrees * NormalizeSign(mapping.TwistSign));
        SetAxis(ref result, mapping.FrontBackAxis, frontBackDegrees * NormalizeSign(mapping.FrontBackSign));
        SetAxis(ref result, mapping.LeftRightAxis, leftRightDegrees * NormalizeSign(mapping.LeftRightSign));
        return result;
    }

    internal static Quaternion CreateRestJoint(in CompiledHumanoidJointLimit limit, EHumanoidAvatarRotationOrder order, Quaternion preRotation, Quaternion postRotation)
    {
        Vector3 center = limit.UseDefaultValues ? Vector3.Zero : limit.CenterDegrees;
        return Quaternion.Normalize(preRotation * CreateOrderedRotation(center, order) * postRotation);
    }

    private static Vector3 ClampCanonicalDegrees(Vector3 degrees, in CompiledHumanoidJointLimit limit)
        => new(
            Math.Clamp(degrees.X, limit.MinimumDegrees.X, limit.MaximumDegrees.X),
            Math.Clamp(degrees.Y, limit.MinimumDegrees.Y, limit.MaximumDegrees.Y),
            Math.Clamp(degrees.Z, limit.MinimumDegrees.Z, limit.MaximumDegrees.Z));

    private static Quaternion CreateOrderedRotation(Vector3 degrees, EHumanoidAvatarRotationOrder order)
    {
        Quaternion x = Quaternion.CreateFromAxisAngle(Vector3.UnitX, degrees.X * DegreesToRadians);
        Quaternion y = Quaternion.CreateFromAxisAngle(Vector3.UnitY, degrees.Y * DegreesToRadians);
        Quaternion z = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, degrees.Z * DegreesToRadians);
        return order switch
        {
            EHumanoidAvatarRotationOrder.XYZ => Quaternion.Normalize(z * y * x),
            EHumanoidAvatarRotationOrder.XZY => Quaternion.Normalize(y * z * x),
            EHumanoidAvatarRotationOrder.YXZ => Quaternion.Normalize(z * x * y),
            EHumanoidAvatarRotationOrder.YZX => Quaternion.Normalize(x * z * y),
            EHumanoidAvatarRotationOrder.ZYX => Quaternion.Normalize(x * y * z),
            _ => Quaternion.Normalize(y * x * z), // ZXY
        };
    }

    /// <summary>
    /// Creates Unity's continuous humanoid swing/twist parameterization. Each
    /// component is a half-angle tangent, so simultaneous channels remain one
    /// normalized 3D rotation vector rather than acquiring Euler cross terms.
    /// </summary>
    private static Quaternion CreateTangentRotation(Vector3 degrees)
    {
        const float halfDegreesToRadians = DegreesToRadians * 0.5f;
        var tangent = new Vector3(
            MathF.Tan(degrees.X * halfDegreesToRadians),
            MathF.Tan(degrees.Y * halfDegreesToRadians),
            MathF.Tan(degrees.Z * halfDegreesToRadians));
        return Quaternion.Normalize(new Quaternion(tangent, 1.0f));
    }

    private static void SetAxis(ref Vector3 vector, int axis, float value)
    {
        switch (axis)
        {
            case 0: vector.X = value; break;
            case 1: vector.Y = value; break;
            case 2: vector.Z = value; break;
        }
    }

    private static float NormalizeSign(int sign) => sign < 0 ? -1.0f : 1.0f;
}
