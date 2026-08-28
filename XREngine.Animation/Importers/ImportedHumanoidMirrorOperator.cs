using System.Numerics;
using XREngine.Animation.IK;
using XREngine.Components.Animation;

namespace XREngine.Animation.Importers;

/// <summary>
/// Mirrors semantic humanoid data through the canonical sagittal plane. Concrete
/// skeleton names and axes never participate in this operation.
/// </summary>
public static class ImportedHumanoidMirrorOperator
{
    public static Vector3 MirrorPosition(Vector3 value)
        => new(-value.X, value.Y, value.Z);

    public static Quaternion MirrorRotation(Quaternion value)
        => Quaternion.Normalize(new Quaternion(value.X, -value.Y, -value.Z, value.W));

    public static EHumanoidValue MirrorMuscle(EHumanoidValue value, out float parity)
    {
        parity = IsOddCentralMuscle(value) ? -1.0f : 1.0f;
        return SwapBilateralMuscle(value);
    }

    public static ELimbEndEffector MirrorGoal(ELimbEndEffector goal)
        => goal switch
        {
            ELimbEndEffector.LeftHand => ELimbEndEffector.RightHand,
            ELimbEndEffector.RightHand => ELimbEndEffector.LeftHand,
            ELimbEndEffector.LeftFoot => ELimbEndEffector.RightFoot,
            ELimbEndEffector.RightFoot => ELimbEndEffector.LeftFoot,
            _ => goal,
        };

    private static bool IsOddCentralMuscle(EHumanoidValue value)
        => value is EHumanoidValue.SpineLeftRight
        or EHumanoidValue.SpineTwistLeftRight
        or EHumanoidValue.ChestLeftRight
        or EHumanoidValue.ChestTwistLeftRight
        or EHumanoidValue.UpperChestLeftRight
        or EHumanoidValue.UpperChestTwistLeftRight
        or EHumanoidValue.NeckTiltLeftRight
        or EHumanoidValue.NeckTurnLeftRight
        or EHumanoidValue.HeadTiltLeftRight
        or EHumanoidValue.HeadTurnLeftRight
        or EHumanoidValue.JawLeftRight;

    private static EHumanoidValue SwapBilateralMuscle(EHumanoidValue value)
    {
        int index = (int)value;
        if (index is >= (int)EHumanoidValue.LeftEyeDownUp and <= (int)EHumanoidValue.RightEyeInOut)
            return (EHumanoidValue)(index ^ 2);

        if (index is >= (int)EHumanoidValue.LeftShoulderDownUp and <= (int)EHumanoidValue.LeftToesUpDown)
            return (EHumanoidValue)(index + ((int)EHumanoidValue.RightShoulderDownUp - (int)EHumanoidValue.LeftShoulderDownUp));

        if (index is >= (int)EHumanoidValue.RightShoulderDownUp and <= (int)EHumanoidValue.RightToesUpDown)
            return (EHumanoidValue)(index - ((int)EHumanoidValue.RightShoulderDownUp - (int)EHumanoidValue.LeftShoulderDownUp));

        if (index is >= (int)EHumanoidValue.LeftHandIndexSpread and <= (int)EHumanoidValue.LeftHandThumb3Stretched)
            return (EHumanoidValue)(index + ((int)EHumanoidValue.RightHandIndexSpread - (int)EHumanoidValue.LeftHandIndexSpread));

        if (index is >= (int)EHumanoidValue.RightHandIndexSpread and <= (int)EHumanoidValue.RightHandThumb3Stretched)
            return (EHumanoidValue)(index - ((int)EHumanoidValue.RightHandIndexSpread - (int)EHumanoidValue.LeftHandIndexSpread));

        return value;
    }
}
