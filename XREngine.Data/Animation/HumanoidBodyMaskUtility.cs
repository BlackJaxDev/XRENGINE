namespace XREngine.Components.Animation;

/// <summary>
/// Maps humanoid muscle and translation-DOF identifiers to their owning <see cref="EHumanoidBodyMask"/> region.
/// </summary>
public static class HumanoidBodyMaskUtility
{
    /// <summary>
    /// Returns the single <see cref="EHumanoidBodyMask"/> bit that owns <paramref name="muscle"/>.
    /// </summary>
    public static EHumanoidBodyMask GetBodyPart(EHumanoidValue muscle)
    {
        int index = (int)muscle;
        return index switch
        {
            >= (int)EHumanoidValue.LeftEyeDownUp and <= (int)EHumanoidValue.RightEyeInOut => EHumanoidBodyMask.Eyes,
            >= (int)EHumanoidValue.SpineFrontBack and <= (int)EHumanoidValue.UpperChestTwistLeftRight => EHumanoidBodyMask.Body,
            >= (int)EHumanoidValue.NeckNodDownUp and <= (int)EHumanoidValue.HeadTurnLeftRight => EHumanoidBodyMask.Head,
            >= (int)EHumanoidValue.JawClose and <= (int)EHumanoidValue.JawLeftRight => EHumanoidBodyMask.Jaw,
            >= (int)EHumanoidValue.LeftShoulderDownUp and <= (int)EHumanoidValue.LeftHandInOut => EHumanoidBodyMask.LeftArm,
            >= (int)EHumanoidValue.LeftUpperLegFrontBack and <= (int)EHumanoidValue.LeftToesUpDown => EHumanoidBodyMask.LeftLeg,
            >= (int)EHumanoidValue.RightShoulderDownUp and <= (int)EHumanoidValue.RightHandInOut => EHumanoidBodyMask.RightArm,
            >= (int)EHumanoidValue.RightUpperLegFrontBack and <= (int)EHumanoidValue.RightToesUpDown => EHumanoidBodyMask.RightLeg,
            >= (int)EHumanoidValue.LeftHandIndexSpread and <= (int)EHumanoidValue.LeftHandThumb3Stretched => EHumanoidBodyMask.LeftFingers,
            >= (int)EHumanoidValue.RightHandIndexSpread and <= (int)EHumanoidValue.RightHandThumb3Stretched => EHumanoidBodyMask.RightFingers,
            _ => EHumanoidBodyMask.NonHumanoid,
        };
    }

    /// <summary>
    /// Returns the single <see cref="EHumanoidBodyMask"/> bit that owns <paramref name="bone"/>.
    /// </summary>
    public static EHumanoidBodyMask GetBodyPart(EHumanoidTranslationDofBone bone)
        => bone switch
        {
            EHumanoidTranslationDofBone.Spine
            or EHumanoidTranslationDofBone.Chest
            or EHumanoidTranslationDofBone.UpperChest => EHumanoidBodyMask.Body,
            EHumanoidTranslationDofBone.Neck
            or EHumanoidTranslationDofBone.Head => EHumanoidBodyMask.Head,
            EHumanoidTranslationDofBone.LeftUpperLeg
            or EHumanoidTranslationDofBone.LeftLowerLeg
            or EHumanoidTranslationDofBone.LeftFoot
            or EHumanoidTranslationDofBone.LeftToes => EHumanoidBodyMask.LeftLeg,
            EHumanoidTranslationDofBone.RightUpperLeg
            or EHumanoidTranslationDofBone.RightLowerLeg
            or EHumanoidTranslationDofBone.RightFoot
            or EHumanoidTranslationDofBone.RightToes => EHumanoidBodyMask.RightLeg,
            EHumanoidTranslationDofBone.LeftShoulder
            or EHumanoidTranslationDofBone.LeftUpperArm
            or EHumanoidTranslationDofBone.LeftLowerArm
            or EHumanoidTranslationDofBone.LeftHand => EHumanoidBodyMask.LeftArm,
            EHumanoidTranslationDofBone.RightShoulder
            or EHumanoidTranslationDofBone.RightUpperArm
            or EHumanoidTranslationDofBone.RightLowerArm
            or EHumanoidTranslationDofBone.RightHand => EHumanoidBodyMask.RightArm,
            _ => EHumanoidBodyMask.NonHumanoid,
        };
}
