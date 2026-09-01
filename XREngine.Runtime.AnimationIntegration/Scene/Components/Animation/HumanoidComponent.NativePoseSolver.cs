using System.Numerics;

namespace XREngine.Components.Animation;

public partial class HumanoidComponent
{
    private const int TranslationDofBoneCount = (int)EHumanoidTranslationDofBone.RightHand + 1;

    private readonly HumanoidPoseSolveWorkspace _nativePoseWorkspace = new();
    private readonly Vector3[] _importedTranslationDofValues = new Vector3[TranslationDofBoneCount];
    private uint _importedTranslationDofMask;
    private bool _hasInvalidImportedTranslationDof;

    /// <summary>
    /// Stages one already canonicalized translation-DoF component. Serialized
    /// animation import converts Unity coordinates to engine humanoid coordinates
    /// before invoking this method.
    /// </summary>
    public void SetImportedTranslationDof(
        EHumanoidTranslationDofBone bone,
        int component,
        float amount)
    {
        int boneIndex = (int)bone;
        if ((uint)boneIndex >= (uint)_importedTranslationDofValues.Length
            || (uint)component >= 3u)
            return;

        lock (_muscleValuesLock)
        {
            if (!float.IsFinite(amount))
            {
                _hasInvalidImportedTranslationDof = true;
                return;
            }

            Vector3 value = _importedTranslationDofValues[boneIndex];
            switch (component)
            {
                case 0: value.X = amount; break;
                case 1: value.Y = amount; break;
                case 2: value.Z = amount; break;
            }

            _importedTranslationDofValues[boneIndex] = value;
            _importedTranslationDofMask |= 1u << boneIndex;
        }
    }

    private void ClearImportedTranslationDofState()
    {
        lock (_muscleValuesLock)
        {
            Array.Clear(_importedTranslationDofValues);
            _importedTranslationDofMask = 0u;
            _hasInvalidImportedTranslationDof = false;
        }
    }

    private bool TryEvaluateNativeHumanoidPose(
        CompiledHumanoidAvatarDefinition compiled,
        ReadOnlySpan<float> muscleSnapshot,
        bool includeTranslationDof)
    {
        if (muscleSnapshot.Length < MuscleValueCount)
            return false;

        for (int i = 0; i < MuscleValueCount; i++)
            if (!float.IsFinite(muscleSnapshot[i]))
                return false;

        _nativePoseWorkspace.Begin(compiled);
        StageSemanticMuscleDegrees(compiled, muscleSnapshot);
        if (includeTranslationDof && !TryStageTranslationDof(compiled))
            return false;
        if (!_nativePoseWorkspace.TrySolve(compiled))
            return false;

        return true;
    }

    private void StageSemanticMuscleDegrees(
        CompiledHumanoidAvatarDefinition compiled,
        ReadOnlySpan<float> muscles)
    {
        StageRole(
            compiled,
            muscles,
            EHumanoidAvatarBoneRole.LeftEye,
            null,
            EHumanoidValue.LeftEyeDownUp,
            EHumanoidValue.LeftEyeInOut,
            frontBackSign: -1.0f,
            leftRightSign: 1.0f);
        StageRole(
            compiled,
            muscles,
            EHumanoidAvatarBoneRole.RightEye,
            null,
            EHumanoidValue.RightEyeDownUp,
            EHumanoidValue.RightEyeInOut,
            frontBackSign: -1.0f,
            leftRightSign: -1.0f);
        StageRole(
            compiled,
            muscles,
            EHumanoidAvatarBoneRole.Spine,
            EHumanoidValue.SpineTwistLeftRight,
            EHumanoidValue.SpineFrontBack,
            EHumanoidValue.SpineLeftRight,
            frontBackSign: -1.0f,
            leftRightSign: -1.0f);

        float chestTwist = GetMuscleDegrees(compiled, muscles, EHumanoidValue.ChestTwistLeftRight);
        float chestFrontBack = GetMuscleDegrees(compiled, muscles, EHumanoidValue.ChestFrontBack);
        float chestLeftRight = GetMuscleDegrees(compiled, muscles, EHumanoidValue.ChestLeftRight);
        bool hasUpperChest = compiled.GetNode(EHumanoidAvatarBoneRole.UpperChest) is not null;
        if (!hasUpperChest)
        {
            const float missingUpperChestShare = 0.5f;
            chestTwist += missingUpperChestShare
                * GetMuscleDegrees(compiled, muscles, EHumanoidValue.UpperChestTwistLeftRight);
            chestFrontBack += missingUpperChestShare
                * GetMuscleDegrees(compiled, muscles, EHumanoidValue.UpperChestFrontBack);
            chestLeftRight += missingUpperChestShare
                * GetMuscleDegrees(compiled, muscles, EHumanoidValue.UpperChestLeftRight);
        }
        _nativePoseWorkspace.SetMuscleDegrees(
            EHumanoidAvatarBoneRole.Chest,
            chestTwist,
            -chestFrontBack,
            -chestLeftRight);
        if (hasUpperChest)
            StageRole(
                compiled,
                muscles,
                EHumanoidAvatarBoneRole.UpperChest,
                EHumanoidValue.UpperChestTwistLeftRight,
                EHumanoidValue.UpperChestFrontBack,
                EHumanoidValue.UpperChestLeftRight,
                frontBackSign: -1.0f,
                leftRightSign: -1.0f);

        StageRole(
            compiled,
            muscles,
            EHumanoidAvatarBoneRole.Neck,
            EHumanoidValue.NeckTurnLeftRight,
            EHumanoidValue.NeckNodDownUp,
            EHumanoidValue.NeckTiltLeftRight,
            frontBackSign: -1.0f,
            leftRightSign: -1.0f);
        StageRole(
            compiled,
            muscles,
            EHumanoidAvatarBoneRole.Head,
            EHumanoidValue.HeadTurnLeftRight,
            EHumanoidValue.HeadNodDownUp,
            EHumanoidValue.HeadTiltLeftRight,
            frontBackSign: -1.0f,
            leftRightSign: -1.0f);
        StageRole(
            compiled,
            muscles,
            EHumanoidAvatarBoneRole.Jaw,
            null,
            EHumanoidValue.JawClose,
            EHumanoidValue.JawLeftRight,
            frontBackSign: -1.0f,
            leftRightSign: 1.0f);

        StageLimb(compiled, muscles, isLeft: true);
        StageLimb(compiled, muscles, isLeft: false);
        StageFingers(compiled, muscles, isLeft: true);
        StageFingers(compiled, muscles, isLeft: false);
    }

    private void StageLimb(
        CompiledHumanoidAvatarDefinition compiled,
        ReadOnlySpan<float> muscles,
        bool isLeft)
    {
        float side = isLeft ? 1.0f : -1.0f;
        _nativePoseWorkspace.SetMuscleDegrees(
            isLeft ? EHumanoidAvatarBoneRole.LeftShoulder : EHumanoidAvatarBoneRole.RightShoulder,
            0.0f,
            side * GetMuscleDegrees(compiled, muscles,
                isLeft ? EHumanoidValue.LeftShoulderFrontBack : EHumanoidValue.RightShoulderFrontBack),
            GetMuscleDegrees(compiled, muscles,
                isLeft ? EHumanoidValue.LeftShoulderDownUp : EHumanoidValue.RightShoulderDownUp));
        _nativePoseWorkspace.SetMuscleDegrees(
            isLeft ? EHumanoidAvatarBoneRole.LeftUpperArm : EHumanoidAvatarBoneRole.RightUpperArm,
            side * GetMuscleDegrees(compiled, muscles,
                isLeft ? EHumanoidValue.LeftArmTwistInOut : EHumanoidValue.RightArmTwistInOut),
            side * GetMuscleDegrees(compiled, muscles,
                isLeft ? EHumanoidValue.LeftArmFrontBack : EHumanoidValue.RightArmFrontBack),
            GetMuscleDegrees(compiled, muscles,
                isLeft ? EHumanoidValue.LeftArmDownUp : EHumanoidValue.RightArmDownUp));
        _nativePoseWorkspace.SetMuscleDegrees(
            isLeft ? EHumanoidAvatarBoneRole.LeftLowerArm : EHumanoidAvatarBoneRole.RightLowerArm,
            side * GetMuscleDegrees(compiled, muscles,
                isLeft ? EHumanoidValue.LeftForearmTwistInOut : EHumanoidValue.RightForearmTwistInOut),
            -side * GetMuscleDegrees(compiled, muscles,
                isLeft ? EHumanoidValue.LeftForearmStretch : EHumanoidValue.RightForearmStretch),
            0.0f);
        _nativePoseWorkspace.SetMuscleDegrees(
            isLeft ? EHumanoidAvatarBoneRole.LeftHand : EHumanoidAvatarBoneRole.RightHand,
            0.0f,
            side * GetMuscleDegrees(compiled, muscles,
                isLeft ? EHumanoidValue.LeftHandInOut : EHumanoidValue.RightHandInOut),
            GetMuscleDegrees(compiled, muscles,
                isLeft ? EHumanoidValue.LeftHandDownUp : EHumanoidValue.RightHandDownUp));
        _nativePoseWorkspace.SetMuscleDegrees(
            isLeft ? EHumanoidAvatarBoneRole.LeftUpperLeg : EHumanoidAvatarBoneRole.RightUpperLeg,
            GetMuscleDegrees(compiled, muscles,
                isLeft ? EHumanoidValue.LeftUpperLegTwistInOut : EHumanoidValue.RightUpperLegTwistInOut),
            -GetMuscleDegrees(compiled, muscles,
                isLeft ? EHumanoidValue.LeftUpperLegFrontBack : EHumanoidValue.RightUpperLegFrontBack),
            -side * GetMuscleDegrees(compiled, muscles,
                isLeft ? EHumanoidValue.LeftUpperLegInOut : EHumanoidValue.RightUpperLegInOut));
        _nativePoseWorkspace.SetMuscleDegrees(
            isLeft ? EHumanoidAvatarBoneRole.LeftLowerLeg : EHumanoidAvatarBoneRole.RightLowerLeg,
            GetMuscleDegrees(compiled, muscles,
                isLeft ? EHumanoidValue.LeftLowerLegTwistInOut : EHumanoidValue.RightLowerLegTwistInOut),
            -GetMuscleDegrees(compiled, muscles,
                isLeft ? EHumanoidValue.LeftLowerLegStretch : EHumanoidValue.RightLowerLegStretch),
            0.0f);
        _nativePoseWorkspace.SetMuscleDegrees(
            isLeft ? EHumanoidAvatarBoneRole.LeftFoot : EHumanoidAvatarBoneRole.RightFoot,
            side * GetMuscleDegrees(compiled, muscles,
                isLeft ? EHumanoidValue.LeftFootTwistInOut : EHumanoidValue.RightFootTwistInOut),
            -GetMuscleDegrees(compiled, muscles,
                isLeft ? EHumanoidValue.LeftFootUpDown : EHumanoidValue.RightFootUpDown),
            0.0f);
        StageRole(
            compiled,
            muscles,
            isLeft ? EHumanoidAvatarBoneRole.LeftToes : EHumanoidAvatarBoneRole.RightToes,
            null,
            isLeft ? EHumanoidValue.LeftToesUpDown : EHumanoidValue.RightToesUpDown,
            null,
            frontBackSign: -1.0f);
    }

    private void StageFingers(
        CompiledHumanoidAvatarDefinition compiled,
        ReadOnlySpan<float> muscles,
        bool isLeft)
    {
        StageFinger(
            compiled,
            muscles,
            isLeft,
            isLeft ? EHumanoidAvatarBoneRole.LeftThumbProximal : EHumanoidAvatarBoneRole.RightThumbProximal,
            isLeft ? EHumanoidAvatarBoneRole.LeftThumbIntermediate : EHumanoidAvatarBoneRole.RightThumbIntermediate,
            isLeft ? EHumanoidAvatarBoneRole.LeftThumbDistal : EHumanoidAvatarBoneRole.RightThumbDistal,
            isLeft ? EHumanoidValue.LeftHandThumbSpread : EHumanoidValue.RightHandThumbSpread,
            isLeft ? EHumanoidValue.LeftHandThumb1Stretched : EHumanoidValue.RightHandThumb1Stretched,
            isLeft ? EHumanoidValue.LeftHandThumb2Stretched : EHumanoidValue.RightHandThumb2Stretched,
            isLeft ? EHumanoidValue.LeftHandThumb3Stretched : EHumanoidValue.RightHandThumb3Stretched,
            invertSpread: true);
        StageFinger(
            compiled,
            muscles,
            isLeft,
            isLeft ? EHumanoidAvatarBoneRole.LeftIndexProximal : EHumanoidAvatarBoneRole.RightIndexProximal,
            isLeft ? EHumanoidAvatarBoneRole.LeftIndexIntermediate : EHumanoidAvatarBoneRole.RightIndexIntermediate,
            isLeft ? EHumanoidAvatarBoneRole.LeftIndexDistal : EHumanoidAvatarBoneRole.RightIndexDistal,
            isLeft ? EHumanoidValue.LeftHandIndexSpread : EHumanoidValue.RightHandIndexSpread,
            isLeft ? EHumanoidValue.LeftHandIndex1Stretched : EHumanoidValue.RightHandIndex1Stretched,
            isLeft ? EHumanoidValue.LeftHandIndex2Stretched : EHumanoidValue.RightHandIndex2Stretched,
            isLeft ? EHumanoidValue.LeftHandIndex3Stretched : EHumanoidValue.RightHandIndex3Stretched,
            invertSpread: false);
        StageFinger(
            compiled,
            muscles,
            isLeft,
            isLeft ? EHumanoidAvatarBoneRole.LeftMiddleProximal : EHumanoidAvatarBoneRole.RightMiddleProximal,
            isLeft ? EHumanoidAvatarBoneRole.LeftMiddleIntermediate : EHumanoidAvatarBoneRole.RightMiddleIntermediate,
            isLeft ? EHumanoidAvatarBoneRole.LeftMiddleDistal : EHumanoidAvatarBoneRole.RightMiddleDistal,
            isLeft ? EHumanoidValue.LeftHandMiddleSpread : EHumanoidValue.RightHandMiddleSpread,
            isLeft ? EHumanoidValue.LeftHandMiddle1Stretched : EHumanoidValue.RightHandMiddle1Stretched,
            isLeft ? EHumanoidValue.LeftHandMiddle2Stretched : EHumanoidValue.RightHandMiddle2Stretched,
            isLeft ? EHumanoidValue.LeftHandMiddle3Stretched : EHumanoidValue.RightHandMiddle3Stretched,
            invertSpread: false);
        StageFinger(
            compiled,
            muscles,
            isLeft,
            isLeft ? EHumanoidAvatarBoneRole.LeftRingProximal : EHumanoidAvatarBoneRole.RightRingProximal,
            isLeft ? EHumanoidAvatarBoneRole.LeftRingIntermediate : EHumanoidAvatarBoneRole.RightRingIntermediate,
            isLeft ? EHumanoidAvatarBoneRole.LeftRingDistal : EHumanoidAvatarBoneRole.RightRingDistal,
            isLeft ? EHumanoidValue.LeftHandRingSpread : EHumanoidValue.RightHandRingSpread,
            isLeft ? EHumanoidValue.LeftHandRing1Stretched : EHumanoidValue.RightHandRing1Stretched,
            isLeft ? EHumanoidValue.LeftHandRing2Stretched : EHumanoidValue.RightHandRing2Stretched,
            isLeft ? EHumanoidValue.LeftHandRing3Stretched : EHumanoidValue.RightHandRing3Stretched,
            invertSpread: true);
        StageFinger(
            compiled,
            muscles,
            isLeft,
            isLeft ? EHumanoidAvatarBoneRole.LeftLittleProximal : EHumanoidAvatarBoneRole.RightLittleProximal,
            isLeft ? EHumanoidAvatarBoneRole.LeftLittleIntermediate : EHumanoidAvatarBoneRole.RightLittleIntermediate,
            isLeft ? EHumanoidAvatarBoneRole.LeftLittleDistal : EHumanoidAvatarBoneRole.RightLittleDistal,
            isLeft ? EHumanoidValue.LeftHandLittleSpread : EHumanoidValue.RightHandLittleSpread,
            isLeft ? EHumanoidValue.LeftHandLittle1Stretched : EHumanoidValue.RightHandLittle1Stretched,
            isLeft ? EHumanoidValue.LeftHandLittle2Stretched : EHumanoidValue.RightHandLittle2Stretched,
            isLeft ? EHumanoidValue.LeftHandLittle3Stretched : EHumanoidValue.RightHandLittle3Stretched,
            invertSpread: true);
    }

    private void StageFinger(
        CompiledHumanoidAvatarDefinition compiled,
        ReadOnlySpan<float> muscles,
        bool isLeft,
        EHumanoidAvatarBoneRole proximalRole,
        EHumanoidAvatarBoneRole intermediateRole,
        EHumanoidAvatarBoneRole distalRole,
        EHumanoidValue spread,
        EHumanoidValue proximal,
        EHumanoidValue intermediate,
        EHumanoidValue distal,
        bool invertSpread)
    {
        float sideSign = (isLeft ? 1.0f : -1.0f) * (invertSpread ? -1.0f : 1.0f);
        _nativePoseWorkspace.SetMuscleDegrees(
            proximalRole,
            0.0f,
            GetMuscleDegrees(compiled, muscles, proximal),
            GetMuscleDegrees(compiled, muscles, spread) * sideSign);
        _nativePoseWorkspace.SetMuscleDegrees(
            intermediateRole,
            0.0f,
            GetMuscleDegrees(compiled, muscles, intermediate),
            0.0f);
        _nativePoseWorkspace.SetMuscleDegrees(
            distalRole,
            0.0f,
            GetMuscleDegrees(compiled, muscles, distal),
            0.0f);
    }

    private void StageRole(
        CompiledHumanoidAvatarDefinition compiled,
        ReadOnlySpan<float> muscles,
        EHumanoidAvatarBoneRole role,
        EHumanoidValue? twist,
        EHumanoidValue? frontBack,
        EHumanoidValue? leftRight,
        float twistSign = 1.0f,
        float frontBackSign = 1.0f,
        float leftRightSign = 1.0f)
        => _nativePoseWorkspace.SetMuscleDegrees(
            role,
            twist.HasValue ? GetMuscleDegrees(compiled, muscles, twist.Value) * twistSign : 0.0f,
            frontBack.HasValue ? GetMuscleDegrees(compiled, muscles, frontBack.Value) * frontBackSign : 0.0f,
            leftRight.HasValue ? GetMuscleDegrees(compiled, muscles, leftRight.Value) * leftRightSign : 0.0f);

    private static float GetMuscleDegrees(
        CompiledHumanoidAvatarDefinition compiled,
        ReadOnlySpan<float> muscles,
        EHumanoidValue muscle)
    {
        int index = (int)muscle;
        float normalized = (uint)index < (uint)muscles.Length
            ? muscles[index]
            : 0.0f;
        Vector2 range = compiled.GetMuscleRange(muscle);
        return normalized >= 0.0f
            ? normalized * range.Y
            : -normalized * range.X;
    }

    private bool TryStageTranslationDof(CompiledHumanoidAvatarDefinition compiled)
    {
        lock (_muscleValuesLock)
        {
            if (_hasInvalidImportedTranslationDof)
                return false;

            uint mask = _importedTranslationDofMask;
            while (mask != 0u)
            {
                int index = BitOperations.TrailingZeroCount(mask);
                Vector3 value = _importedTranslationDofValues[index];
                if (!float.IsFinite(value.X)
                    || !float.IsFinite(value.Y)
                    || !float.IsFinite(value.Z))
                    return false;

                EHumanoidAvatarBoneRole role = MapTranslationDofRole((EHumanoidTranslationDofBone)index);
                ref readonly CompiledHumanoidBoneSolvePlan plan = ref compiled.GetBoneSolvePlan(role);
                if (compiled.SolverSettings.HasTranslationDoF && plan.PermitsTranslationDegreesOfFreedom)
                    _nativePoseWorkspace.SetTranslationDof(role, value);
                mask &= mask - 1u;
            }

            return true;
        }
    }

    private static EHumanoidAvatarBoneRole MapTranslationDofRole(EHumanoidTranslationDofBone bone)
        => bone switch
        {
            EHumanoidTranslationDofBone.Spine => EHumanoidAvatarBoneRole.Spine,
            EHumanoidTranslationDofBone.Chest => EHumanoidAvatarBoneRole.Chest,
            EHumanoidTranslationDofBone.UpperChest => EHumanoidAvatarBoneRole.UpperChest,
            EHumanoidTranslationDofBone.Neck => EHumanoidAvatarBoneRole.Neck,
            EHumanoidTranslationDofBone.Head => EHumanoidAvatarBoneRole.Head,
            EHumanoidTranslationDofBone.LeftUpperLeg => EHumanoidAvatarBoneRole.LeftUpperLeg,
            EHumanoidTranslationDofBone.LeftLowerLeg => EHumanoidAvatarBoneRole.LeftLowerLeg,
            EHumanoidTranslationDofBone.LeftFoot => EHumanoidAvatarBoneRole.LeftFoot,
            EHumanoidTranslationDofBone.LeftToes => EHumanoidAvatarBoneRole.LeftToes,
            EHumanoidTranslationDofBone.RightUpperLeg => EHumanoidAvatarBoneRole.RightUpperLeg,
            EHumanoidTranslationDofBone.RightLowerLeg => EHumanoidAvatarBoneRole.RightLowerLeg,
            EHumanoidTranslationDofBone.RightFoot => EHumanoidAvatarBoneRole.RightFoot,
            EHumanoidTranslationDofBone.RightToes => EHumanoidAvatarBoneRole.RightToes,
            EHumanoidTranslationDofBone.LeftShoulder => EHumanoidAvatarBoneRole.LeftShoulder,
            EHumanoidTranslationDofBone.LeftUpperArm => EHumanoidAvatarBoneRole.LeftUpperArm,
            EHumanoidTranslationDofBone.LeftLowerArm => EHumanoidAvatarBoneRole.LeftLowerArm,
            EHumanoidTranslationDofBone.LeftHand => EHumanoidAvatarBoneRole.LeftHand,
            EHumanoidTranslationDofBone.RightShoulder => EHumanoidAvatarBoneRole.RightShoulder,
            EHumanoidTranslationDofBone.RightUpperArm => EHumanoidAvatarBoneRole.RightUpperArm,
            EHumanoidTranslationDofBone.RightLowerArm => EHumanoidAvatarBoneRole.RightLowerArm,
            EHumanoidTranslationDofBone.RightHand => EHumanoidAvatarBoneRole.RightHand,
            _ => throw new ArgumentOutOfRangeException(nameof(bone), bone, "Unknown humanoid translation-DoF role."),
        };
}
