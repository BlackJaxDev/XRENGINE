using System.Numerics;
using XREngine.Components;
using XREngine.Components.Scene.Mesh;
using XREngine.Core.Reflection.Attributes;
using XREngine.Data;
using XREngine.Data.Colors;
using XREngine.Data.Core;
using XREngine.Data.Rendering;
using XREngine.Animation.IK;
using XREngine.Rendering.Info;
using XREngine.Rendering.Models;
using XREngine.Scene;
using XREngine.Scene.Transforms;
using XREngine.Timers;

namespace XREngine.Components.Animation
{
    [XRComponentEditor("XREngine.Editor.ComponentEditors.HumanoidComponentEditor")]
    public class HumanoidComponent : XRComponent, IRenderable
    {
        // Humanoid (muscle) curve state. Values are expected to be normalized in [-1, 1].
        // We store raw values and apply a full pose once per frame.
        private readonly Dictionary<EHumanoidValue, float> _muscleValues = [];
        private readonly Dictionary<EHumanoidValue, float> _rawHumanoidValues = [];
        private readonly object _muscleValuesLock = new();
        private const int MuscleValueCount = (int)EHumanoidValue.RightHandThumb3Stretched + 1;
        private readonly float[] _muscleValueSnapshot = new float[MuscleValueCount];

        protected override void OnComponentActivated()
        {
            base.OnComponentActivated();

            // Apply muscle-driven pose after animation evaluation.
            RegisterTick(ETickGroup.Normal, ETickOrder.Scene, ApplyMusclePose);
        }

        protected override void OnComponentDeactivated()
        {
            base.OnComponentDeactivated();

            UnregisterTick(ETickGroup.Normal, ETickOrder.Scene, ApplyMusclePose);
            ResetRuntimeAnimationDiagnostics();
        }

        private HumanoidSettings _settings = new();
        public HumanoidSettings Settings
        {
            get => _settings;
            set => SetField(ref _settings, value);
        }

        private EHumanoidPosePreviewMode _posePreviewMode;
        public EHumanoidPosePreviewMode PosePreviewMode
        {
            get => _posePreviewMode;
            set => SetField(ref _posePreviewMode, value);
        }

        private (TransformBase? tfm, Matrix4x4 offset) _headIKTarget = HumanoidIKTargetDefaults.Empty;
        public (TransformBase? tfm, Matrix4x4 offset) HeadIKTarget
        {
            get => _headIKTarget;
            set => SetField(ref _headIKTarget, value);
        }

        private (TransformBase? tfm, Matrix4x4 offset) _hipsIKTarget = HumanoidIKTargetDefaults.Empty;
        public (TransformBase? tfm, Matrix4x4 offset) HipsIKTarget
        {
            get => _hipsIKTarget;
            set => SetField(ref _hipsIKTarget, value);
        }

        private (TransformBase? tfm, Matrix4x4 offset) _leftHandIKTarget = HumanoidIKTargetDefaults.Empty;
        public (TransformBase? tfm, Matrix4x4 offset) LeftHandIKTarget
        {
            get => _leftHandIKTarget;
            set => SetField(ref _leftHandIKTarget, value);
        }

        private (TransformBase? tfm, Matrix4x4 offset) _rightHandIKTarget = HumanoidIKTargetDefaults.Empty;
        public (TransformBase? tfm, Matrix4x4 offset) RightHandIKTarget
        {
            get => _rightHandIKTarget;
            set => SetField(ref _rightHandIKTarget, value);
        }

        private (TransformBase? tfm, Matrix4x4 offset) _leftFootIKTarget = HumanoidIKTargetDefaults.Empty;
        public (TransformBase? tfm, Matrix4x4 offset) LeftFootIKTarget
        {
            get => _leftFootIKTarget;
            set => SetField(ref _leftFootIKTarget, value);
        }

        private (TransformBase? tfm, Matrix4x4 offset) _rightFootIKTarget = HumanoidIKTargetDefaults.Empty;
        public (TransformBase? tfm, Matrix4x4 offset) RightFootIKTarget
        {
            get => _rightFootIKTarget;
            set => SetField(ref _rightFootIKTarget, value);
        }

        private (TransformBase? tfm, Matrix4x4 offset) _leftElbowIKTarget = HumanoidIKTargetDefaults.Empty;
        public (TransformBase? tfm, Matrix4x4 offset) LeftElbowIKTarget
        {
            get => _leftElbowIKTarget;
            set => SetField(ref _leftElbowIKTarget, value);
        }

        private (TransformBase? tfm, Matrix4x4 offset) _rightElbowIKTarget = HumanoidIKTargetDefaults.Empty;
        public (TransformBase? tfm, Matrix4x4 offset) RightElbowIKTarget
        {
            get => _rightElbowIKTarget;
            set => SetField(ref _rightElbowIKTarget, value);
        }

        private (TransformBase? tfm, Matrix4x4 offset) _leftKneeIKTarget = HumanoidIKTargetDefaults.Empty;
        public (TransformBase? tfm, Matrix4x4 offset) LeftKneeIKTarget
        {
            get => _leftKneeIKTarget;
            set => SetField(ref _leftKneeIKTarget, value);
        }

        private (TransformBase? tfm, Matrix4x4 offset) _rightKneeIKTarget = HumanoidIKTargetDefaults.Empty;
        public (TransformBase? tfm, Matrix4x4 offset) RightKneeIKTarget
        {
            get => _rightKneeIKTarget;
            set => SetField(ref _rightKneeIKTarget, value);
        }

        private (TransformBase? tfm, Matrix4x4 offset) _chestIKTarget = HumanoidIKTargetDefaults.Empty;
        public (TransformBase? tfm, Matrix4x4 offset) ChestIKTarget
        {
            get => _chestIKTarget;
            set => SetField(ref _chestIKTarget, value);
        }

        private SceneNode? _ikTargetRootNode;

        private EHumanoidNeutralPosePreset _neutralPosePreset = EHumanoidNeutralPosePreset.UnityMecanim;
        public EHumanoidNeutralPosePreset NeutralPosePreset
        {
            get => _neutralPosePreset;
            set => SetField(ref _neutralPosePreset, value);
        }

        public bool IsAnimatedPosePreviewActive
            => PosePreviewMode == EHumanoidPosePreviewMode.AnimatedPose;

        public void SetValue(EHumanoidValue value, float amount)
        {
            lock (_muscleValuesLock)
            {
                _rawHumanoidValues[value] = amount;
                _muscleValues[value] = amount;
            }

            float t = amount * 0.5f + 0.5f;
            switch (value)
            {
                case EHumanoidValue.LeftEyeDownUp:
                    {
                        const float degToRad = MathF.PI / 180.0f;
                        float yaw = Settings.GetValue(EHumanoidValue.LeftEyeInOut);
                        float pitch = Interp.Lerp(Settings.LeftEyeDownUpRange.X, Settings.LeftEyeDownUpRange.Y, t);
                        Settings.SetValue(EHumanoidValue.LeftEyeDownUp, pitch);

                        // LH?RH: negate yaw (Y) and pitch (X) to convert from Unity's left-handed convention.
                        Quaternion q = Quaternion.CreateFromYawPitchRoll(-yaw * degToRad, -pitch * degToRad, 0.0f);
                        ApplyNeutralBindRelativeRotation(Left.Eye.Node, q);
                        break;
                    }
                case EHumanoidValue.LeftEyeInOut:
                    {
                        const float degToRad = MathF.PI / 180.0f;
                        float yaw = Interp.Lerp(Settings.LeftEyeInOutRange.X, Settings.LeftEyeInOutRange.Y, t);
                        float pitch = Settings.GetValue(EHumanoidValue.LeftEyeDownUp);
                        Settings.SetValue(EHumanoidValue.LeftEyeInOut, yaw);

                        Quaternion q = Quaternion.CreateFromYawPitchRoll(-yaw * degToRad, -pitch * degToRad, 0.0f);
                        ApplyNeutralBindRelativeRotation(Left.Eye.Node, q);
                        break;
                    }
                case EHumanoidValue.RightEyeDownUp:
                    {
                        const float degToRad = MathF.PI / 180.0f;
                        float yaw = Settings.GetValue(EHumanoidValue.RightEyeInOut);
                        float pitch = Interp.Lerp(Settings.RightEyeDownUpRange.X, Settings.RightEyeDownUpRange.Y, t);
                        Settings.SetValue(EHumanoidValue.RightEyeDownUp, pitch);

                        Quaternion q = Quaternion.CreateFromYawPitchRoll(-yaw * degToRad, -pitch * degToRad, 0.0f);
                        ApplyNeutralBindRelativeRotation(Right.Eye.Node, q);
                        break;
                    }
                case EHumanoidValue.RightEyeInOut:
                    {
                        const float degToRad = MathF.PI / 180.0f;
                        float yaw = Interp.Lerp(Settings.RightEyeInOutRange.X, Settings.RightEyeInOutRange.Y, t);
                        float pitch = Settings.GetValue(EHumanoidValue.RightEyeDownUp);
                        Settings.SetValue(EHumanoidValue.RightEyeInOut, yaw);

                        Quaternion q = Quaternion.CreateFromYawPitchRoll(-yaw * degToRad, -pitch * degToRad, 0.0f);
                        ApplyNeutralBindRelativeRotation(Right.Eye.Node, q);
                        break;
                    }
                default:
                    break;
            }
        }

        public void SetImportedRawValue(EHumanoidValue value, float amount, bool flipMuscleZ = false)
        {
            float convertedAmount = ConvertImportedHumanoidAmount(value, amount, flipMuscleZ);

            lock (_muscleValuesLock)
            {
                _rawHumanoidValues[value] = amount;
                _muscleValues[value] = convertedAmount;
            }

            ApplyImmediateHumanoidValueEffects(value, convertedAmount);
        }

        public void SetImportedRawValue(int value, float amount, bool flipMuscleZ = false)
            => SetImportedRawValue((EHumanoidValue)value, amount, flipMuscleZ);

        // Int overload for decoupled reflection callers (e.g. Unity animation importer).
        public void SetValue(int value, float amount)
        {
            // Diagnostic: log the first few calls to verify animation is reaching HumanoidComponent
            if (_setValueCallCount < 5)
            {
                _setValueCallCount++;
                Debug.Animation($"[HumanoidComponent] SetValue called: value={(EHumanoidValue)value} ({value}), amount={amount:F4}");
            }
            SetValue((EHumanoidValue)value, amount);
        }
        private int _setValueCallCount = 0;

        /// <summary>
        /// Legacy string-based humanoid setter. String-to-enum conversion is performed by importers.
        /// Values are expected to be normalized in [-1, 1] (muscle space).
        /// </summary>
        [Obsolete("Use SetValue(EHumanoidValue, float). String-based mapping should happen in the Unity animation importer.")]
        public void SetValue(string name, float amount)
        {
            // Intentionally no-op: string->enum conversion should live in the importer.
        }

        private void ApplyImmediateHumanoidValueEffects(EHumanoidValue value, float amount)
        {
            float t = amount * 0.5f + 0.5f;
            switch (value)
            {
                case EHumanoidValue.LeftEyeDownUp:
                    {
                        const float degToRad = MathF.PI / 180.0f;
                        float yaw = Settings.GetValue(EHumanoidValue.LeftEyeInOut);
                        float pitch = Interp.Lerp(Settings.LeftEyeDownUpRange.X, Settings.LeftEyeDownUpRange.Y, t);
                        Settings.SetValue(EHumanoidValue.LeftEyeDownUp, pitch);

                        Quaternion q = Quaternion.CreateFromYawPitchRoll(-yaw * degToRad, -pitch * degToRad, 0.0f);
                        ApplyNeutralBindRelativeRotation(Left.Eye.Node, q);
                        break;
                    }
                case EHumanoidValue.LeftEyeInOut:
                    {
                        const float degToRad = MathF.PI / 180.0f;
                        float yaw = Interp.Lerp(Settings.LeftEyeInOutRange.X, Settings.LeftEyeInOutRange.Y, t);
                        float pitch = Settings.GetValue(EHumanoidValue.LeftEyeDownUp);
                        Settings.SetValue(EHumanoidValue.LeftEyeInOut, yaw);

                        Quaternion q = Quaternion.CreateFromYawPitchRoll(-yaw * degToRad, -pitch * degToRad, 0.0f);
                        ApplyNeutralBindRelativeRotation(Left.Eye.Node, q);
                        break;
                    }
                case EHumanoidValue.RightEyeDownUp:
                    {
                        const float degToRad = MathF.PI / 180.0f;
                        float yaw = Settings.GetValue(EHumanoidValue.RightEyeInOut);
                        float pitch = Interp.Lerp(Settings.RightEyeDownUpRange.X, Settings.RightEyeDownUpRange.Y, t);
                        Settings.SetValue(EHumanoidValue.RightEyeDownUp, pitch);

                        Quaternion q = Quaternion.CreateFromYawPitchRoll(-yaw * degToRad, -pitch * degToRad, 0.0f);
                        ApplyNeutralBindRelativeRotation(Right.Eye.Node, q);
                        break;
                    }
                case EHumanoidValue.RightEyeInOut:
                    {
                        const float degToRad = MathF.PI / 180.0f;
                        float yaw = Interp.Lerp(Settings.RightEyeInOutRange.X, Settings.RightEyeInOutRange.Y, t);
                        float pitch = Settings.GetValue(EHumanoidValue.RightEyeDownUp);
                        Settings.SetValue(EHumanoidValue.RightEyeInOut, yaw);

                        Quaternion q = Quaternion.CreateFromYawPitchRoll(-yaw * degToRad, -pitch * degToRad, 0.0f);
                        ApplyNeutralBindRelativeRotation(Right.Eye.Node, q);
                        break;
                    }
            }
        }

        private static float ConvertImportedHumanoidAmount(EHumanoidValue value, float amount, bool flipMuscleZ)
        {
            if (!flipMuscleZ)
                return amount;

            return GetImportedHumanoidHandednessFamily(value) switch
            {
                ImportedHumanoidHandednessFamily.Pitch => -amount,
                ImportedHumanoidHandednessFamily.Yaw => -amount,
                _ => amount,
            };
        }

        private static ImportedHumanoidHandednessFamily GetImportedHumanoidHandednessFamily(EHumanoidValue value)
            => value switch
            {
                EHumanoidValue.LeftEyeDownUp or
                EHumanoidValue.RightEyeDownUp or
                EHumanoidValue.SpineFrontBack or
                EHumanoidValue.ChestFrontBack or
                EHumanoidValue.UpperChestFrontBack or
                EHumanoidValue.NeckNodDownUp or
                EHumanoidValue.HeadNodDownUp or
                EHumanoidValue.JawClose or
                EHumanoidValue.LeftShoulderDownUp or
                EHumanoidValue.LeftArmDownUp or
                EHumanoidValue.LeftForearmStretch or
                EHumanoidValue.LeftHandDownUp or
                EHumanoidValue.LeftUpperLegFrontBack or
                EHumanoidValue.LeftLowerLegStretch or
                EHumanoidValue.LeftFootUpDown or
                EHumanoidValue.LeftToesUpDown or
                EHumanoidValue.RightShoulderDownUp or
                EHumanoidValue.RightArmDownUp or
                EHumanoidValue.RightForearmStretch or
                EHumanoidValue.RightHandDownUp or
                EHumanoidValue.RightUpperLegFrontBack or
                EHumanoidValue.RightLowerLegStretch or
                EHumanoidValue.RightFootUpDown or
                EHumanoidValue.RightToesUpDown or
                EHumanoidValue.LeftHandIndex1Stretched or
                EHumanoidValue.LeftHandIndex2Stretched or
                EHumanoidValue.LeftHandIndex3Stretched or
                EHumanoidValue.LeftHandMiddle1Stretched or
                EHumanoidValue.LeftHandMiddle2Stretched or
                EHumanoidValue.LeftHandMiddle3Stretched or
                EHumanoidValue.LeftHandRing1Stretched or
                EHumanoidValue.LeftHandRing2Stretched or
                EHumanoidValue.LeftHandRing3Stretched or
                EHumanoidValue.LeftHandLittle1Stretched or
                EHumanoidValue.LeftHandLittle2Stretched or
                EHumanoidValue.LeftHandLittle3Stretched or
                EHumanoidValue.LeftHandThumb1Stretched or
                EHumanoidValue.LeftHandThumb2Stretched or
                EHumanoidValue.LeftHandThumb3Stretched or
                EHumanoidValue.RightHandIndex1Stretched or
                EHumanoidValue.RightHandIndex2Stretched or
                EHumanoidValue.RightHandIndex3Stretched or
                EHumanoidValue.RightHandMiddle1Stretched or
                EHumanoidValue.RightHandMiddle2Stretched or
                EHumanoidValue.RightHandMiddle3Stretched or
                EHumanoidValue.RightHandRing1Stretched or
                EHumanoidValue.RightHandRing2Stretched or
                EHumanoidValue.RightHandRing3Stretched or
                EHumanoidValue.RightHandLittle1Stretched or
                EHumanoidValue.RightHandLittle2Stretched or
                EHumanoidValue.RightHandLittle3Stretched or
                EHumanoidValue.RightHandThumb1Stretched or
                EHumanoidValue.RightHandThumb2Stretched or
                EHumanoidValue.RightHandThumb3Stretched
                    => ImportedHumanoidHandednessFamily.Pitch,

                EHumanoidValue.LeftEyeInOut or
                EHumanoidValue.RightEyeInOut or
                EHumanoidValue.SpineTwistLeftRight or
                EHumanoidValue.ChestTwistLeftRight or
                EHumanoidValue.UpperChestTwistLeftRight or
                EHumanoidValue.NeckTurnLeftRight or
                EHumanoidValue.HeadTurnLeftRight or
                EHumanoidValue.JawLeftRight or
                EHumanoidValue.LeftArmTwistInOut or
                EHumanoidValue.LeftForearmTwistInOut or
                EHumanoidValue.LeftFootTwistInOut or
                EHumanoidValue.LeftUpperLegTwistInOut or
                EHumanoidValue.LeftLowerLegTwistInOut or
                EHumanoidValue.RightArmTwistInOut or
                EHumanoidValue.RightForearmTwistInOut or
                EHumanoidValue.RightFootTwistInOut or
                EHumanoidValue.RightUpperLegTwistInOut or
                EHumanoidValue.RightLowerLegTwistInOut or
                EHumanoidValue.LeftHandIndexSpread or
                EHumanoidValue.LeftHandMiddleSpread or
                EHumanoidValue.LeftHandRingSpread or
                EHumanoidValue.LeftHandLittleSpread or
                EHumanoidValue.LeftHandThumbSpread or
                EHumanoidValue.RightHandIndexSpread or
                EHumanoidValue.RightHandMiddleSpread or
                EHumanoidValue.RightHandRingSpread or
                EHumanoidValue.RightHandLittleSpread or
                EHumanoidValue.RightHandThumbSpread
                    => ImportedHumanoidHandednessFamily.Yaw,

                _ => ImportedHumanoidHandednessFamily.Roll,
            };

        private enum ImportedHumanoidHandednessFamily
        {
            Pitch,
            Yaw,
            Roll,
        }

        private float GetMuscleValue(EHumanoidValue value)
        {
            lock (_muscleValuesLock)
            {
                return _muscleValues.TryGetValue(value, out var v) ? v : 0.0f;
            }
        }

        public bool TryGetMuscleValue(EHumanoidValue value, out float amount)
        {
            lock (_muscleValuesLock)
            {
                return _muscleValues.TryGetValue(value, out amount);
            }
        }

        public bool TryGetRawHumanoidValue(EHumanoidValue value, out float amount)
        {
            lock (_muscleValuesLock)
            {
                return _rawHumanoidValues.TryGetValue(value, out amount);
            }
        }

        private bool TryCaptureMuscleSnapshot(float[] destination)
        {
            lock (_muscleValuesLock)
            {
                if (_muscleValues.Count == 0)
                    return false;

                Array.Clear(destination);
                foreach ((EHumanoidValue value, float amount) in _muscleValues)
                {
                    int index = (int)value;
                    if ((uint)index < (uint)destination.Length)
                        destination[index] = amount;
                }

                return true;
            }
        }

        private static float GetMuscleValue(ReadOnlySpan<float> snapshot, EHumanoidValue value)
        {
            int index = (int)value;
            return (uint)index < (uint)snapshot.Length ? snapshot[index] : 0.0f;
        }

        public void ApplyCurrentMusclePose()
            => ApplyMusclePose();

        public void ReloadNeutralPosePreset()
            => ApplyNeutralPosePreset(NeutralPosePreset);

        public void ClearNeutralPoseOffsets()
        {
            Settings.NeutralPoseBoneRotations.Clear();
            if (PosePreviewMode == EHumanoidPosePreviewMode.NeutralMusclePose)
                ApplyNeutralPosePreview();
        }

        public void ApplyNeutralPosePreset(EHumanoidNeutralPosePreset preset)
            => ApplyNeutralPoseLocalRotations(HumanoidNeutralPosePresets.GetRotations(preset));

        public void ApplyNeutralPoseLocalRotations(IReadOnlyDictionary<string, Quaternion> rotations)
        {
            Settings.NeutralPoseBoneRotations.Clear();
            foreach ((string boneName, Quaternion rotation) in rotations)
            {
                string targetBoneName = ResolveNeutralPoseBoneSettingKey(boneName);
                SceneNode? targetNode = ResolveNeutralPoseMappedNodeByStoredKey(targetBoneName);
                Quaternion bindRelativeRotation = rotation;

                if (targetNode?.GetTransformAs<Transform>(true) is Transform targetTransform)
                    bindRelativeRotation = Quaternion.Normalize(Quaternion.Inverse(targetTransform.BindState.Rotation) * rotation);

                Settings.NeutralPoseBoneRotations[targetBoneName] = Quaternion.Normalize(bindRelativeRotation);
            }

            if (PosePreviewMode == EHumanoidPosePreviewMode.NeutralMusclePose)
                ApplyNeutralPosePreview();
        }

        public void ApplyNeutralPoseRotations(IReadOnlyDictionary<string, Quaternion> rotations)
        {
            Settings.NeutralPoseBoneRotations.Clear();
            foreach ((string boneName, Quaternion rotation) in rotations)
            {
                string targetBoneName = ResolveNeutralPoseBoneSettingKey(boneName);
                Settings.NeutralPoseBoneRotations[targetBoneName] = Quaternion.Normalize(rotation);
            }

            if (PosePreviewMode == EHumanoidPosePreviewMode.NeutralMusclePose)
                ApplyNeutralPosePreview();
        }

        public (TransformBase? tfm, Matrix4x4 offset) GetIKTarget(EHumanoidIKTarget target)
            => target switch
            {
                EHumanoidIKTarget.Head => HeadIKTarget,
                EHumanoidIKTarget.Hips => HipsIKTarget,
                EHumanoidIKTarget.LeftHand => LeftHandIKTarget,
                EHumanoidIKTarget.RightHand => RightHandIKTarget,
                EHumanoidIKTarget.LeftFoot => LeftFootIKTarget,
                EHumanoidIKTarget.RightFoot => RightFootIKTarget,
                EHumanoidIKTarget.LeftElbow => LeftElbowIKTarget,
                EHumanoidIKTarget.RightElbow => RightElbowIKTarget,
                EHumanoidIKTarget.LeftKnee => LeftKneeIKTarget,
                EHumanoidIKTarget.RightKnee => RightKneeIKTarget,
                EHumanoidIKTarget.Chest => ChestIKTarget,
                _ => HumanoidIKTargetDefaults.Empty,
            };

        public TransformBase? GetIKTargetTransform(EHumanoidIKTarget target)
            => GetIKTarget(target).tfm;

        public void SetIKTarget(EHumanoidIKTarget target, TransformBase? tfm, Matrix4x4 offset)
        {
            var binding = (tfm, offset);
            switch (target)
            {
                case EHumanoidIKTarget.Head:
                    HeadIKTarget = binding;
                    break;
                case EHumanoidIKTarget.Hips:
                    HipsIKTarget = binding;
                    break;
                case EHumanoidIKTarget.LeftHand:
                    LeftHandIKTarget = binding;
                    break;
                case EHumanoidIKTarget.RightHand:
                    RightHandIKTarget = binding;
                    break;
                case EHumanoidIKTarget.LeftFoot:
                    LeftFootIKTarget = binding;
                    break;
                case EHumanoidIKTarget.RightFoot:
                    RightFootIKTarget = binding;
                    break;
                case EHumanoidIKTarget.LeftElbow:
                    LeftElbowIKTarget = binding;
                    break;
                case EHumanoidIKTarget.RightElbow:
                    RightElbowIKTarget = binding;
                    break;
                case EHumanoidIKTarget.LeftKnee:
                    LeftKneeIKTarget = binding;
                    break;
                case EHumanoidIKTarget.RightKnee:
                    RightKneeIKTarget = binding;
                    break;
                case EHumanoidIKTarget.Chest:
                    ChestIKTarget = binding;
                    break;
            }
        }

        public void ClearIKTarget(EHumanoidIKTarget target)
            => SetIKTarget(target, null, Matrix4x4.Identity);

        public void ClearIKTargets()
        {
            ClearIKTarget(EHumanoidIKTarget.Head);
            ClearIKTarget(EHumanoidIKTarget.Hips);
            ClearIKTarget(EHumanoidIKTarget.LeftHand);
            ClearIKTarget(EHumanoidIKTarget.RightHand);
            ClearIKTarget(EHumanoidIKTarget.LeftFoot);
            ClearIKTarget(EHumanoidIKTarget.RightFoot);
            ClearIKTarget(EHumanoidIKTarget.LeftElbow);
            ClearIKTarget(EHumanoidIKTarget.RightElbow);
            ClearIKTarget(EHumanoidIKTarget.LeftKnee);
            ClearIKTarget(EHumanoidIKTarget.RightKnee);
            ClearIKTarget(EHumanoidIKTarget.Chest);
        }

        public Transform EnsureOwnedIKTarget(EHumanoidIKTarget target, string? nodeName = null)
        {
            if (GetIKTargetTransform(target) is Transform transform)
                return transform;

            _ikTargetRootNode ??= SceneNode.NewChild("HumanoidIKTargets");
            var targetNode = _ikTargetRootNode.NewChild(nodeName ?? GetDefaultIKTargetNodeName(target));
            transform = targetNode.GetTransformAs<Transform>(true)!;
            SetIKTarget(target, transform, Matrix4x4.Identity);
            return transform;
        }

        public Matrix4x4 GetIKTargetWorldMatrix(EHumanoidIKTarget target)
        {
            var binding = GetIKTarget(target);
            return binding.offset * (binding.tfm?.RenderMatrix ?? Matrix4x4.Identity);
        }

        public void SetIKTargetWorldPosition(EHumanoidIKTarget target, Vector3 position)
        {
            var binding = GetIKTarget(target);
            if (binding.tfm is null)
                return;

            Matrix4x4 current = GetIKTargetWorldMatrix(target);
            current.Translation = position;
            ApplyIKTargetWorldMatrix(binding, current);
        }

        public void SetIKTargetWorldRotation(EHumanoidIKTarget target, Quaternion rotation)
        {
            var binding = GetIKTarget(target);
            if (binding.tfm is null)
                return;

            Matrix4x4 current = GetIKTargetWorldMatrix(target);
            current = Matrix4x4.CreateFromQuaternion(rotation) * Matrix4x4.CreateTranslation(current.Translation);
            ApplyIKTargetWorldMatrix(binding, current);
        }

        public void SetIKTargetWorldPose(EHumanoidIKTarget target, Vector3 position, Quaternion rotation)
        {
            var binding = GetIKTarget(target);
            if (binding.tfm is null)
                return;

            Matrix4x4 desired = Matrix4x4.CreateFromQuaternion(rotation) * Matrix4x4.CreateTranslation(position);
            ApplyIKTargetWorldMatrix(binding, desired);
        }

        private Quaternion GetNeutralPoseBoneRotation(SceneNode? node)
        {
            if (node is null)
                return Quaternion.Identity;

            if (TryGetNeutralPoseStoredRotation(node, out Quaternion rotation))
                return Quaternion.Normalize(rotation);

            return Quaternion.Identity;
        }

        private bool TryGetNeutralPoseStoredRotation(SceneNode node, out Quaternion rotation)
        {
            if (node.Name is not null && Settings.TryGetNeutralPoseBoneRotation(node.Name, out rotation))
                return true;

            string? canonicalName = ResolveNeutralPoseCanonicalName(node);
            if (canonicalName is not null && Settings.TryGetNeutralPoseBoneRotation(canonicalName, out rotation))
                return true;

            rotation = Quaternion.Identity;
            return false;
        }

        private void ApplyNeutralBindRelativeRotation(SceneNode? node, Quaternion deltaRotation)
        {
            if (node?.Transform is null)
                return;

            Quaternion neutralRotation = GetNeutralPoseBoneRotation(node);
            Quaternion effective = Quaternion.Normalize(neutralRotation * deltaRotation);
            node.GetTransformAs<Transform>(true)?.SetBindRelativeRotation(effective);
        }

        private void ApplyMusclePose()
        {
            if (!IsAnimatedPosePreviewActive)
                return;

            if (!TryCaptureMuscleSnapshot(_muscleValueSnapshot))
                return;

            ReadOnlySpan<float> muscleSnapshot = _muscleValueSnapshot;

            EnsureBoneMapping();

            // Log first-playback diagnostic (once).
            LogFirstPlaybackDiagnostic();

            // Torso
            ApplyBindRelativeEulerDegrees(
                Spine.Node,
                yawDeg: MapMuscleToDeg(EHumanoidValue.SpineTwistLeftRight, GetMuscleValue(muscleSnapshot, EHumanoidValue.SpineTwistLeftRight)),
                pitchDeg: MapMuscleToDeg(EHumanoidValue.SpineFrontBack, GetMuscleValue(muscleSnapshot, EHumanoidValue.SpineFrontBack)),
                rollDeg: MapMuscleToDeg(EHumanoidValue.SpineLeftRight, GetMuscleValue(muscleSnapshot, EHumanoidValue.SpineLeftRight)),
                axisMapping: GetBoneAxisMapping(Spine.Node));

            ApplyBindRelativeEulerDegrees(
                Chest.Node,
                yawDeg: MapMuscleToDeg(EHumanoidValue.ChestTwistLeftRight, GetMuscleValue(muscleSnapshot, EHumanoidValue.ChestTwistLeftRight))
                    + (UpperChest.Node is null ? MapMuscleToDeg(EHumanoidValue.UpperChestTwistLeftRight, GetMuscleValue(muscleSnapshot, EHumanoidValue.UpperChestTwistLeftRight)) : 0.0f),
                pitchDeg: MapMuscleToDeg(EHumanoidValue.ChestFrontBack, GetMuscleValue(muscleSnapshot, EHumanoidValue.ChestFrontBack))
                      + (UpperChest.Node is null ? MapMuscleToDeg(EHumanoidValue.UpperChestFrontBack, GetMuscleValue(muscleSnapshot, EHumanoidValue.UpperChestFrontBack)) : 0.0f),
                rollDeg: MapMuscleToDeg(EHumanoidValue.ChestLeftRight, GetMuscleValue(muscleSnapshot, EHumanoidValue.ChestLeftRight))
                     + (UpperChest.Node is null ? MapMuscleToDeg(EHumanoidValue.UpperChestLeftRight, GetMuscleValue(muscleSnapshot, EHumanoidValue.UpperChestLeftRight)) : 0.0f),
                axisMapping: GetBoneAxisMapping(Chest.Node));

            // UpperChest — only applied when a separate UpperChest bone exists.
            if (UpperChest.Node is not null)
            {
                ApplyBindRelativeEulerDegrees(
                    UpperChest.Node,
                    yawDeg: MapMuscleToDeg(EHumanoidValue.UpperChestTwistLeftRight, GetMuscleValue(muscleSnapshot, EHumanoidValue.UpperChestTwistLeftRight)),
                    pitchDeg: MapMuscleToDeg(EHumanoidValue.UpperChestFrontBack, GetMuscleValue(muscleSnapshot, EHumanoidValue.UpperChestFrontBack)),
                    rollDeg: MapMuscleToDeg(EHumanoidValue.UpperChestLeftRight, GetMuscleValue(muscleSnapshot, EHumanoidValue.UpperChestLeftRight)),
                    axisMapping: GetBoneAxisMapping(UpperChest.Node));
            }

            // Neck / Head
            ApplyBindRelativeEulerDegrees(
                Neck.Node,
                yawDeg: MapMuscleToDeg(EHumanoidValue.NeckTurnLeftRight, GetMuscleValue(muscleSnapshot, EHumanoidValue.NeckTurnLeftRight)),
                pitchDeg: -MapMuscleToDeg(EHumanoidValue.NeckNodDownUp, GetMuscleValue(muscleSnapshot, EHumanoidValue.NeckNodDownUp)),
                rollDeg: MapMuscleToDeg(EHumanoidValue.NeckTiltLeftRight, GetMuscleValue(muscleSnapshot, EHumanoidValue.NeckTiltLeftRight)),
                axisMapping: GetBoneAxisMapping(Neck.Node));

            ApplyBindRelativeEulerDegrees(
                Head.Node,
                yawDeg: MapMuscleToDeg(EHumanoidValue.HeadTurnLeftRight, GetMuscleValue(muscleSnapshot, EHumanoidValue.HeadTurnLeftRight)),
                pitchDeg: -MapMuscleToDeg(EHumanoidValue.HeadNodDownUp, GetMuscleValue(muscleSnapshot, EHumanoidValue.HeadNodDownUp)),
                rollDeg: MapMuscleToDeg(EHumanoidValue.HeadTiltLeftRight, GetMuscleValue(muscleSnapshot, EHumanoidValue.HeadTiltLeftRight)),
                axisMapping: GetBoneAxisMapping(Head.Node));

            // Jaw
            ApplyBindRelativeEulerDegrees(
                Jaw.Node,
                yawDeg: MapMuscleToDeg(EHumanoidValue.JawLeftRight, GetMuscleValue(muscleSnapshot, EHumanoidValue.JawLeftRight)),
                pitchDeg: MapMuscleToDeg(EHumanoidValue.JawClose, GetMuscleValue(muscleSnapshot, EHumanoidValue.JawClose)),
                rollDeg: 0.0f);

            // Arms / Legs / Hands / Feet
            GetBindBodyBasis(out Vector3 bodyLeft, out Vector3 bodyUp, out Vector3 bodyForward);
            ApplyLimbMuscles(isLeft: true, bodyLeft, bodyUp, bodyForward, muscleSnapshot);
            ApplyLimbMuscles(isLeft: false, bodyLeft, bodyUp, bodyForward, muscleSnapshot);

            // Fingers
            ApplyFingerMuscles(isLeft: true, muscleSnapshot);
            ApplyFingerMuscles(isLeft: false, muscleSnapshot);

            // One-time diagnostic snapshot of muscle values ? degree values.
            LogMusclePoseSnapshot(_muscleValueSnapshot);
        }

        private bool _boneMappingComplete;
        private long _nextRebindTicks;
        private bool? _lastBoneMappingComplete;

        internal static bool ShouldAttemptRebind(bool boneMappingComplete, long nowTicks, long nextRebindTicks)
            => !boneMappingComplete && nowTicks >= nextRebindTicks;

        private void EnsureBoneMapping()
        {
            long nowTicks = Engine.ElapsedTicks;
            if (!ShouldAttemptRebind(_boneMappingComplete, nowTicks, _nextRebindTicks))
                return;

            _nextRebindTicks = nowTicks + EngineTimer.SecondsToStopwatchTicks(0.5f);
            SetFromNode();
        }

        private void ApplyLimbMuscles(bool isLeft, Vector3 bodyLeft, Vector3 bodyUp, Vector3 bodyForward, ReadOnlySpan<float> muscleSnapshot)
        {
            var side = isLeft ? Left : Right;
            string sideLabel = isLeft ? "L" : "R";
            EHumanoidValue shoulderDownUp = isLeft ? EHumanoidValue.LeftShoulderDownUp : EHumanoidValue.RightShoulderDownUp;
            EHumanoidValue shoulderFrontBack = isLeft ? EHumanoidValue.LeftShoulderFrontBack : EHumanoidValue.RightShoulderFrontBack;
            EHumanoidValue armTwist = isLeft ? EHumanoidValue.LeftArmTwistInOut : EHumanoidValue.RightArmTwistInOut;
            EHumanoidValue armDownUp = isLeft ? EHumanoidValue.LeftArmDownUp : EHumanoidValue.RightArmDownUp;
            EHumanoidValue armFrontBack = isLeft ? EHumanoidValue.LeftArmFrontBack : EHumanoidValue.RightArmFrontBack;
            EHumanoidValue forearmTwist = isLeft ? EHumanoidValue.LeftForearmTwistInOut : EHumanoidValue.RightForearmTwistInOut;
            EHumanoidValue forearmStretch = isLeft ? EHumanoidValue.LeftForearmStretch : EHumanoidValue.RightForearmStretch;
            EHumanoidValue handDownUp = isLeft ? EHumanoidValue.LeftHandDownUp : EHumanoidValue.RightHandDownUp;
            EHumanoidValue handInOut = isLeft ? EHumanoidValue.LeftHandInOut : EHumanoidValue.RightHandInOut;
            EHumanoidValue upperLegTwist = isLeft ? EHumanoidValue.LeftUpperLegTwistInOut : EHumanoidValue.RightUpperLegTwistInOut;
            EHumanoidValue upperLegFrontBack = isLeft ? EHumanoidValue.LeftUpperLegFrontBack : EHumanoidValue.RightUpperLegFrontBack;
            EHumanoidValue upperLegInOut = isLeft ? EHumanoidValue.LeftUpperLegInOut : EHumanoidValue.RightUpperLegInOut;
            EHumanoidValue lowerLegTwist = isLeft ? EHumanoidValue.LeftLowerLegTwistInOut : EHumanoidValue.RightLowerLegTwistInOut;
            EHumanoidValue lowerLegStretch = isLeft ? EHumanoidValue.LeftLowerLegStretch : EHumanoidValue.RightLowerLegStretch;
            EHumanoidValue footTwist = isLeft ? EHumanoidValue.LeftFootTwistInOut : EHumanoidValue.RightFootTwistInOut;
            EHumanoidValue footUpDown = isLeft ? EHumanoidValue.LeftFootUpDown : EHumanoidValue.RightFootUpDown;
            EHumanoidValue toesUpDown = isLeft ? EHumanoidValue.LeftToesUpDown : EHumanoidValue.RightToesUpDown;

            // Stretch channels behave like hinge flexion/extension. Keep the raw clip sign
            // and rely on asymmetric hinge ranges so slight negative values do not produce
            // deep extension while strong positive values still allow a bent elbow/knee.
            float forearmStretchMuscle = GetMuscleValue(muscleSnapshot, forearmStretch);
            float lowerLegStretchMuscle = GetMuscleValue(muscleSnapshot, lowerLegStretch);
            float lowerLegPitchDeg = MapMuscleToDeg(lowerLegStretch, lowerLegStretchMuscle);
            lowerLegPitchDeg = ClampKneeFlexionDeg(sideLabel, lowerLegStretchMuscle, lowerLegPitchDeg);

            Vector3 armPitchAxisWorld = isLeft ? bodyForward : -bodyForward;
            Vector3 armRollAxisWorld = isLeft ? -bodyUp : bodyUp;
            Vector3 legPitchAxisWorld = -bodyLeft;
            Vector3 legRollAxisWorld = isLeft ? -bodyForward : bodyForward;

            Vector3 shoulderTwistAxisWorld = GetBoneToChildAxisWorld(side.Shoulder, side.Arm, isLeft ? bodyLeft : -bodyLeft);
            Vector3 armTwistAxisWorld = GetBoneToChildAxisWorld(side.Arm, side.Elbow, isLeft ? bodyLeft : -bodyLeft);
            Vector3 forearmTwistAxisWorld = GetBoneToChildAxisWorld(side.Elbow, side.Wrist, isLeft ? bodyLeft : -bodyLeft);
            Vector3 upperLegTwistAxisWorld = GetBoneToChildAxisWorld(side.Leg, side.Knee, -bodyUp);
            Vector3 lowerLegTwistAxisWorld = GetBoneToChildAxisWorld(side.Knee, side.Foot, -bodyUp);
            Vector3 footTwistAxisWorld = GetBoneToChildAxisWorld(side.Foot, side.Toes, bodyForward);

            if (isLeft && !_limbBasisLogged)
            {
                LogBodyBasisDiagnostic(
                    bodyLeft,
                    bodyUp,
                    bodyForward,
                    armTwistAxisWorld,
                    upperLegTwistAxisWorld,
                    armPitchAxisWorld,
                    legPitchAxisWorld);
            }

            ApplyLimbBoneRotation(
                side.Shoulder.Node, DebugShoulderSigns,
                yawDeg: 0.0f,
                pitchDeg: MapMuscleToDeg(shoulderDownUp, GetMuscleValue(muscleSnapshot, shoulderDownUp)),
                rollDeg: MapMuscleToDeg(shoulderFrontBack, GetMuscleValue(muscleSnapshot, shoulderFrontBack)),
                twistAxisWorld: shoulderTwistAxisWorld,
                pitchAxisWorld: armPitchAxisWorld,
                rollAxisWorld: armRollAxisWorld,
                preferAxisMapping: true);

            ApplyLimbBoneRotation(
                side.Arm.Node, DebugArmSigns,
                yawDeg: MapMuscleToDeg(armTwist, GetMuscleValue(muscleSnapshot, armTwist)),
                pitchDeg: MapMuscleToDeg(armDownUp, GetMuscleValue(muscleSnapshot, armDownUp)),
                rollDeg: MapMuscleToDeg(armFrontBack, GetMuscleValue(muscleSnapshot, armFrontBack)),
                twistAxisWorld: armTwistAxisWorld,
                pitchAxisWorld: armPitchAxisWorld,
                rollAxisWorld: armRollAxisWorld);

            ApplyLimbBoneRotation(
                side.Elbow.Node, DebugForearmSigns,
                yawDeg: MapMuscleToDeg(forearmTwist, GetMuscleValue(muscleSnapshot, forearmTwist)),
                pitchDeg: MapMuscleToDeg(forearmStretch, forearmStretchMuscle),
                rollDeg: 0.0f,
                twistAxisWorld: forearmTwistAxisWorld,
                pitchAxisWorld: armPitchAxisWorld,
                rollAxisWorld: armRollAxisWorld);

            ApplyLimbBoneRotation(
                side.Wrist.Node, DebugWristSigns,
                yawDeg: 0.0f,
                pitchDeg: MapMuscleToDeg(handDownUp, GetMuscleValue(muscleSnapshot, handDownUp)),
                rollDeg: MapMuscleToDeg(handInOut, GetMuscleValue(muscleSnapshot, handInOut)),
                twistAxisWorld: forearmTwistAxisWorld,
                pitchAxisWorld: armPitchAxisWorld,
                rollAxisWorld: armRollAxisWorld);

            ApplyLimbBoneRotation(
                side.Leg.Node, DebugUpperLegSigns,
                yawDeg: MapMuscleToDeg(upperLegTwist, GetMuscleValue(muscleSnapshot, upperLegTwist)),
                pitchDeg: MapMuscleToDeg(upperLegFrontBack, GetMuscleValue(muscleSnapshot, upperLegFrontBack)),
                rollDeg: MapMuscleToDeg(upperLegInOut, GetMuscleValue(muscleSnapshot, upperLegInOut)),
                twistAxisWorld: upperLegTwistAxisWorld,
                pitchAxisWorld: legPitchAxisWorld,
                rollAxisWorld: legRollAxisWorld,
                preferAxisMapping: true);

            ApplyLimbBoneRotation(
                side.Knee.Node, DebugKneeSigns,
                yawDeg: MapMuscleToDeg(lowerLegTwist, GetMuscleValue(muscleSnapshot, lowerLegTwist)),
                pitchDeg: lowerLegPitchDeg,
                rollDeg: 0.0f,
                twistAxisWorld: lowerLegTwistAxisWorld,
                pitchAxisWorld: legPitchAxisWorld,
                rollAxisWorld: legRollAxisWorld,
                preferAxisMapping: true);

            ApplyLimbBoneRotation(
                side.Foot.Node, DebugFootSigns,
                yawDeg: MapMuscleToDeg(footTwist, GetMuscleValue(muscleSnapshot, footTwist)),
                pitchDeg: MapMuscleToDeg(footUpDown, GetMuscleValue(muscleSnapshot, footUpDown)),
                rollDeg: 0.0f,
                twistAxisWorld: footTwistAxisWorld,
                pitchAxisWorld: legPitchAxisWorld,
                rollAxisWorld: legRollAxisWorld,
                preferAxisMapping: true);

            ApplyLimbBoneRotation(
                side.Toes.Node, DebugToesSigns,
                yawDeg: 0.0f,
                pitchDeg: MapMuscleToDeg(toesUpDown, GetMuscleValue(muscleSnapshot, toesUpDown)),
                rollDeg: 0.0f,
                twistAxisWorld: footTwistAxisWorld,
                pitchAxisWorld: legPitchAxisWorld,
                rollAxisWorld: legRollAxisWorld,
                preferAxisMapping: true);
        }

        private void ApplyFingerMuscles(bool isLeft, ReadOnlySpan<float> muscleSnapshot)
        {
            var side = isLeft ? Left : Right;

            ApplyFinger(isLeft, side.Hand.Thumb, muscleSnapshot,
                spread: isLeft ? EHumanoidValue.LeftHandThumbSpread : EHumanoidValue.RightHandThumbSpread,
                prox: isLeft ? EHumanoidValue.LeftHandThumb1Stretched : EHumanoidValue.RightHandThumb1Stretched,
                mid: isLeft ? EHumanoidValue.LeftHandThumb2Stretched : EHumanoidValue.RightHandThumb2Stretched,
                dist: isLeft ? EHumanoidValue.LeftHandThumb3Stretched : EHumanoidValue.RightHandThumb3Stretched);

            ApplyFinger(isLeft, side.Hand.Index, muscleSnapshot,
                spread: isLeft ? EHumanoidValue.LeftHandIndexSpread : EHumanoidValue.RightHandIndexSpread,
                prox: isLeft ? EHumanoidValue.LeftHandIndex1Stretched : EHumanoidValue.RightHandIndex1Stretched,
                mid: isLeft ? EHumanoidValue.LeftHandIndex2Stretched : EHumanoidValue.RightHandIndex2Stretched,
                dist: isLeft ? EHumanoidValue.LeftHandIndex3Stretched : EHumanoidValue.RightHandIndex3Stretched);

            ApplyFinger(isLeft, side.Hand.Middle, muscleSnapshot,
                spread: isLeft ? EHumanoidValue.LeftHandMiddleSpread : EHumanoidValue.RightHandMiddleSpread,
                prox: isLeft ? EHumanoidValue.LeftHandMiddle1Stretched : EHumanoidValue.RightHandMiddle1Stretched,
                mid: isLeft ? EHumanoidValue.LeftHandMiddle2Stretched : EHumanoidValue.RightHandMiddle2Stretched,
                dist: isLeft ? EHumanoidValue.LeftHandMiddle3Stretched : EHumanoidValue.RightHandMiddle3Stretched);

            ApplyFinger(isLeft, side.Hand.Ring, muscleSnapshot,
                spread: isLeft ? EHumanoidValue.LeftHandRingSpread : EHumanoidValue.RightHandRingSpread,
                prox: isLeft ? EHumanoidValue.LeftHandRing1Stretched : EHumanoidValue.RightHandRing1Stretched,
                mid: isLeft ? EHumanoidValue.LeftHandRing2Stretched : EHumanoidValue.RightHandRing2Stretched,
                dist: isLeft ? EHumanoidValue.LeftHandRing3Stretched : EHumanoidValue.RightHandRing3Stretched);

            ApplyFinger(isLeft, side.Hand.Pinky, muscleSnapshot,
                spread: isLeft ? EHumanoidValue.LeftHandLittleSpread : EHumanoidValue.RightHandLittleSpread,
                prox: isLeft ? EHumanoidValue.LeftHandLittle1Stretched : EHumanoidValue.RightHandLittle1Stretched,
                mid: isLeft ? EHumanoidValue.LeftHandLittle2Stretched : EHumanoidValue.RightHandLittle2Stretched,
                dist: isLeft ? EHumanoidValue.LeftHandLittle3Stretched : EHumanoidValue.RightHandLittle3Stretched);
        }

        private void ApplyFinger(
            bool isLeft,
            BodySide.Fingers.Finger finger,
            ReadOnlySpan<float> muscleSnapshot,
            EHumanoidValue spread, EHumanoidValue prox, EHumanoidValue mid, EHumanoidValue dist)
        {
            float sideMirror = isLeft ? 1.0f : -1.0f;
            // Stretched channels map to bending on each phalanx.
            ApplyBindRelativeEulerDegrees(
                finger.Proximal.Node,
                yawDeg: MapMuscleToDeg(spread, GetMuscleValue(muscleSnapshot, spread)) * sideMirror,
                pitchDeg: MapMuscleToDeg(prox, GetMuscleValue(muscleSnapshot, prox)),
                rollDeg: 0.0f,
                axisMapping: GetBoneAxisMapping(finger.Proximal.Node));

            ApplyBindRelativeEulerDegrees(
                finger.Intermediate.Node,
                yawDeg: 0.0f,
                pitchDeg: MapMuscleToDeg(mid, GetMuscleValue(muscleSnapshot, mid)),
                rollDeg: 0.0f,
                axisMapping: GetBoneAxisMapping(finger.Intermediate.Node));

            ApplyBindRelativeEulerDegrees(
                finger.Distal.Node,
                yawDeg: 0.0f,
                pitchDeg: MapMuscleToDeg(dist, GetMuscleValue(muscleSnapshot, dist)),
                rollDeg: 0.0f,
                axisMapping: GetBoneAxisMapping(finger.Distal.Node));
        }

        private BoneAxisMapping? GetBoneAxisMapping(SceneNode? node)
        {
            if (node?.Name is null)
                return null;

            return Settings.TryGetBoneAxisMapping(node.Name, out var mapping)
                ? mapping
                : null;
        }

        private void ApplyBodyBasisLimbRotation(
            SceneNode? node,
            LimbSignOverrides overrides,
            float yawDeg,
            float pitchDeg,
            float rollDeg,
            Vector3 twistAxisWorld,
            Vector3 pitchAxisWorld,
            Vector3 rollAxisWorld)
            => ApplyWithDebugOverrides(
                node,
                overrides,
                yawDeg,
                pitchDeg,
                rollDeg,
                twistAxisWorld,
                pitchAxisWorld,
                rollAxisWorld);

        private void ApplyLimbBoneRotation(
            SceneNode? node,
            LimbSignOverrides overrides,
            float yawDeg,
            float pitchDeg,
            float rollDeg,
            Vector3 twistAxisWorld,
            Vector3 pitchAxisWorld,
            Vector3 rollAxisWorld,
            bool preferAxisMapping = false)
        {
            BoneAxisMapping? axisMapping = preferAxisMapping ? GetBoneAxisMapping(node) : null;
            if (axisMapping.HasValue)
            {
                ApplyAxisMappedLimbRotation(node, overrides, yawDeg, pitchDeg, rollDeg, axisMapping.Value);
                return;
            }

            ApplyBodyBasisLimbRotation(
                node,
                overrides,
                yawDeg,
                pitchDeg,
                rollDeg,
                twistAxisWorld,
                pitchAxisWorld,
                rollAxisWorld);
        }

        private void ApplyAxisMappedLimbRotation(
            SceneNode? node,
            LimbSignOverrides overrides,
            float yawDeg,
            float pitchDeg,
            float rollDeg,
            BoneAxisMapping axisMapping)
        {
            yawDeg *= overrides.YawSign;
            pitchDeg *= overrides.PitchSign;
            rollDeg *= overrides.RollSign;

            if (overrides.SkipBlanketNegateYaw)
                yawDeg = -yawDeg;
            if (overrides.SkipBlanketNegatePitch)
                pitchDeg = -pitchDeg;
            if (overrides.SkipBlanketNegateRoll)
                rollDeg = -rollDeg;

            if (overrides.SwapPitchRollAxes)
                (pitchDeg, rollDeg) = (rollDeg, pitchDeg);

            ApplyBindRelativeEulerDegrees(node, yawDeg, pitchDeg, rollDeg, axisMapping);
        }

        private float MapMuscleToDeg(EHumanoidValue value, float muscle)
        {
            // Unity humanoid muscle mapping is piecewise-linear through zero:
            //   muscle = -1  ? minDeg
            //   muscle =  0  ? 0° (bind pose, no rotation)
            //   muscle = +1  ? maxDeg
            // This ensures asymmetric ranges (e.g. knee: -10°..130°) don't
            // produce a non-zero rotation at rest (muscle=0).
            //const float maxMuscleMagnitude = 1.0f;
            //float m = System.Math.Clamp(muscle, -maxMuscleMagnitude, maxMuscleMagnitude);
            float m = muscle;
            m *= Settings.MuscleInputScale;
            //m = System.Math.Clamp(m, -maxMuscleMagnitude, maxMuscleMagnitude);

            Vector2 range = Settings.GetResolvedMuscleRotationDegRange(value);

            // Piecewise: negative muscle scales toward min, positive toward max.
            // range.X is the negative limit (e.g. -10°), range.Y is the positive limit (e.g. 130°).
            return m >= 0.0f
                ? m * range.Y
                : -m * range.X;   // range.X is negative, so result is negative
        }

        private bool _leftKneeClampLogged;
        private bool _rightKneeClampLogged;

        private float ClampKneeFlexionDeg(string sideLabel, float rawMuscle, float pitchDeg)
        {
            if (pitchDeg >= 0.0f)
                return pitchDeg;

            ref bool logged = ref sideLabel == "L" ? ref _leftKneeClampLogged : ref _rightKneeClampLogged;
            if (!logged)
            {
                logged = true;
                Debug.Animation($"[KneeClamp] {sideLabel}.Knee negative flexion clamped rawMuscle={rawMuscle:F4} deg={pitchDeg:F2} -> 0.00");
            }

            return 0.0f;
        }

        private static Vector3 RotateWorldDirection(Quaternion rotation, Vector3 direction)
        {
            Vector3 rotated = Vector3.Transform(direction, rotation);
            float lenSq = rotated.LengthSquared();
            return lenSq > 1e-8f ? rotated / MathF.Sqrt(lenSq) : direction;
        }

        private void GetBindBodyBasis(out Vector3 bindBodyLeft, out Vector3 bindBodyUp, out Vector3 bindBodyForward)
        {
            Vector3 hipsPos = Hips.WorldBindPose.Translation;
            Vector3 spinePos = Spine.Node is not null ? Spine.WorldBindPose.Translation : hipsPos + Vector3.UnitY;
            bindBodyUp = NormalizeOrFallback(spinePos - hipsPos, Vector3.UnitY);

            Vector3 sideSum =
                GetBindSideDelta(Left.Shoulder, Right.Shoulder) +
                GetBindSideDelta(Left.Arm, Right.Arm) +
                GetBindSideDelta(Left.Wrist, Right.Wrist) +
                GetBindSideDelta(Left.Leg, Right.Leg) +
                GetBindSideDelta(Left.Foot, Right.Foot) +
                GetBindSideDelta(Left.Eye, Right.Eye);

            bindBodyLeft = NormalizeOrFallback(RejectAxis(sideSum, bindBodyUp), RejectAxis(Vector3.UnitX, bindBodyUp));
            bindBodyForward = NormalizeOrFallback(Vector3.Cross(bindBodyLeft, bindBodyUp), RejectAxis(Vector3.UnitZ, bindBodyUp));
            bindBodyLeft = NormalizeOrFallback(Vector3.Cross(bindBodyUp, bindBodyForward), bindBodyLeft);
        }

        [Obsolete("Stretch muscles are now applied as rotation (pitch) on the joint bone. Retained for potential non-humanoid uses.")]
        private void ApplyBindRelativeStretchScale(SceneNode? node, EHumanoidValue value, float muscle, Vector2 defaultScaleRange)
        {
            if (node?.Transform is null)
                return;

            float m = System.Math.Clamp(muscle, -1.0f, 1.0f);
            m *= Settings.MuscleInputScale;
            m = System.Math.Clamp(m, -1.0f, 1.0f);
            float t = m * 0.5f + 0.5f;

            Vector2 range = defaultScaleRange;
            if (Settings.TryGetMuscleScaleRange(value, out var configuredRange))
                range = configuredRange;

            float s = Interp.Lerp(range.X, range.Y, t);
            var tfm = node.GetTransformAs<Transform>(true);
            if (tfm is null)
                return;

            // Scale along the primary bone axis (Y) by default.
            var bind = tfm.BindState.Scale;
            tfm.Scale = new Vector3(bind.X, bind.Y * s, bind.Z);
        }

        private void ApplyBindRelativeEulerDegrees(SceneNode? node, float yawDeg, float pitchDeg, float rollDeg)
        {
            ApplyBindRelativeEulerDegrees(node, yawDeg, pitchDeg, rollDeg, null);
        }

        private void ApplyBindRelativeEulerDegrees(SceneNode? node, float yawDeg, float pitchDeg, float rollDeg, BoneAxisMapping? axisMapping)
        {
            if (node?.Transform is null)
                return;

            const float degToRad = MathF.PI / 180.0f;

            // LH?RH conversion: Unity uses a left-handed coordinate system (Z = forward).
            // Our engine uses right-handed OpenGL (Z = toward viewer).
            // For the Z-flip transform M = diag(1,1,-1), a rotation R(axis, ?) in LH becomes
            // R((-ax,-ay,az), ?) in RH. This means:
            //   Rotation around X axis ? negate angle
            //   Rotation around Y axis ? negate angle
            //   Rotation around Z axis ? angle stays

            Quaternion q;
            if (axisMapping.HasValue)
            {
                var m = axisMapping.Value;
                Vector3 twistVec = AxisIndexToVector(m.TwistAxis);
                Vector3 fbVec = AxisIndexToVector(m.FrontBackAxis);
                Vector3 lrVec = AxisIndexToVector(m.LeftRightAxis);

                // Negate angle for X/Y local axes; keep for Z.
                float twistHandednessSign = m.TwistAxis == 2 ? 1f : -1f;
                float fbHandednessSign = m.FrontBackAxis == 2 ? 1f : -1f;
                float lrHandednessSign = m.LeftRightAxis == 2 ? 1f : -1f;

                // Per-bone axis polarity from avatar profiling/manual overrides.
                // Treat 0 as +1 for backward compatibility with older serialized mappings.
                float twistAxisSign = NormalizeAxisSign(m.TwistSign);
                float fbAxisSign = NormalizeAxisSign(m.FrontBackSign);
                float lrAxisSign = NormalizeAxisSign(m.LeftRightSign);

                Quaternion twist = Quaternion.CreateFromAxisAngle(twistVec, twistHandednessSign * twistAxisSign * yawDeg * degToRad);
                Quaternion frontBack = Quaternion.CreateFromAxisAngle(fbVec, fbHandednessSign * fbAxisSign * pitchDeg * degToRad);
                Quaternion leftRight = Quaternion.CreateFromAxisAngle(lrVec, lrHandednessSign * lrAxisSign * rollDeg * degToRad);

                // Unity ZXY Euler order: twist(Z) innermost, front-back(X), left-right(Y) outermost.
                q = leftRight * frontBack * twist;
            }
            else
            {
                // CreateFromYawPitchRoll: yaw=Y, pitch=X, roll=Z.
                // Negate yaw (Y) and pitch (X); keep roll (Z).
                q = Quaternion.CreateFromYawPitchRoll(-yawDeg * degToRad, -pitchDeg * degToRad, rollDeg * degToRad);
            }

            ApplyNeutralBindRelativeRotation(node, q);
        }

        private static Vector3 AxisIndexToVector(int axis) => axis switch
        {
            0 => Vector3.UnitX,
            1 => Vector3.UnitY,
            2 => Vector3.UnitZ,
            _ => Vector3.UnitY,
        };

        private static float NormalizeAxisSign(int sign)
            => sign < 0 ? -1f : 1f;

        private static Vector3 RejectAxis(Vector3 vector, Vector3 normal)
            => vector - Vector3.Dot(vector, normal) * normal;

        private static Vector3 NormalizeOrFallback(Vector3 vector, Vector3 fallback)
        {
            float lenSq = vector.LengthSquared();
            return lenSq > 1e-8f ? vector / MathF.Sqrt(lenSq) : fallback;
        }

        private static Vector3 GetBindSideDelta(BoneDef left, BoneDef right)
        {
            if (left.Node is null || right.Node is null)
                return Vector3.Zero;

            return left.WorldBindPose.Translation - right.WorldBindPose.Translation;
        }

        private static Vector3 GetBoneToChildAxisWorld(BoneDef bone, BoneDef child, Vector3 fallbackWorldAxis)
        {
            if (bone.Node is null || child.Node is null)
                return fallbackWorldAxis;

            Vector3 from = bone.WorldBindPose.Translation;
            Vector3 to = child.WorldBindPose.Translation;
            Vector3 dir = to - from;
            float lenSq = dir.LengthSquared();
            return lenSq > 1e-8f ? dir / MathF.Sqrt(lenSq) : fallbackWorldAxis;
        }

        private static Vector3 TransformWorldAxisToBoneLocal(SceneNode node, Vector3 worldAxis)
        {
            if (!Matrix4x4.Invert(node.Transform.BindMatrix, out Matrix4x4 invBind))
                return worldAxis;

            Vector3 local = Vector3.TransformNormal(worldAxis, invBind);
            float lenSq = local.LengthSquared();
            return lenSq > 1e-8f ? local / MathF.Sqrt(lenSq) : worldAxis;
        }

        /// <summary>
        /// Helper that applies debug sign overrides before delegating to the world-axis rotation function.
        /// </summary>
        private void ApplyWithDebugOverrides(
            SceneNode? node,
            LimbSignOverrides overrides,
            float yawDeg, float pitchDeg, float rollDeg,
            Vector3 twistAxisWorld, Vector3 pitchAxisWorld, Vector3 rollAxisWorld)
        {
            yawDeg *= overrides.YawSign;
            pitchDeg *= overrides.PitchSign;
            rollDeg *= overrides.RollSign;

            if (overrides.SwapPitchRollAxes)
                (pitchAxisWorld, rollAxisWorld) = (rollAxisWorld, pitchAxisWorld);

            ApplyBindRelativeSwingTwistWorldAxes(
                node, yawDeg, pitchDeg, rollDeg,
                twistAxisWorld, pitchAxisWorld, rollAxisWorld,
                overrides.SkipBlanketNegateYaw,
                overrides.SkipBlanketNegatePitch,
                overrides.SkipBlanketNegateRoll);
        }

        private void ApplyBindRelativeSwingTwistWorldAxes(
            SceneNode? node,
            float yawDeg,
            float pitchDeg,
            float rollDeg,
            Vector3 twistAxisWorld,
            Vector3 pitchAxisWorld,
            Vector3 rollAxisWorld,
            bool skipBlanketNegateYaw = false,
            bool skipBlanketNegatePitch = false,
            bool skipBlanketNegateRoll = false)
        {
            if (node?.Transform is null)
                return;

            const float degToRad = MathF.PI / 180.0f;

            Vector3 twistLocal = TransformWorldAxisToBoneLocal(node, twistAxisWorld);
            Vector3 frontBackLocal = TransformWorldAxisToBoneLocal(node, pitchAxisWorld);
            Vector3 leftRightLocal = TransformWorldAxisToBoneLocal(node, rollAxisWorld);

            // Orthogonalize swing axes against the twist axis via Gram-Schmidt.
            frontBackLocal -= Vector3.Dot(frontBackLocal, twistLocal) * twistLocal;
            float fbLen = frontBackLocal.LengthSquared();
            if (fbLen > 1e-8f)
            {
                frontBackLocal /= MathF.Sqrt(fbLen);
                leftRightLocal = Vector3.Cross(twistLocal, frontBackLocal);
            }
            else
            {
                leftRightLocal -= Vector3.Dot(leftRightLocal, twistLocal) * twistLocal;
                float lrLen = leftRightLocal.LengthSquared();
                if (lrLen > 1e-8f)
                {
                    leftRightLocal /= MathF.Sqrt(lrLen);
                    frontBackLocal = Vector3.Cross(leftRightLocal, twistLocal);
                }
            }

            // LH?RH conversion: negate each muscle angle unless overridden per-axis.
            float yawSign = skipBlanketNegateYaw ? 1.0f : -1.0f;
            float pitchSign = skipBlanketNegatePitch ? 1.0f : -1.0f;
            float rollSign = skipBlanketNegateRoll ? 1.0f : -1.0f;
            Quaternion twist = Quaternion.CreateFromAxisAngle(twistLocal, yawSign * yawDeg * degToRad);
            Quaternion frontBack = Quaternion.CreateFromAxisAngle(frontBackLocal, pitchSign * pitchDeg * degToRad);
            Quaternion leftRight = Quaternion.CreateFromAxisAngle(leftRightLocal, rollSign * rollDeg * degToRad);

            // Unity ZXY Euler order: twist(Z) innermost, front-back(X), left-right(Y) outermost.
            Quaternion q = leftRight * frontBack * twist;
            ApplyNeutralBindRelativeRotation(node, q);
        }

        protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
        {
            base.OnPropertyChanged(propName, prev, field);

            if (propName == nameof(NeutralPosePreset))
            {
                ReloadNeutralPosePreset();
                ApplyPosePreviewMode();
            }

            if (propName == nameof(PosePreviewMode))
                ApplyPosePreviewMode();
        }

        protected override void AddedToSceneNode(SceneNode sceneNode)
        {
            base.AddedToSceneNode(sceneNode);
            SetFromNode();
            ReloadNeutralPosePreset();
            ApplyPosePreviewMode();
        }

        public HumanoidComponent() 
        {
            RenderedObjects = [RenderInfo = RenderInfo3D.New(this, EDefaultRenderPass.OpaqueForward, Render)];
            RenderInfo.IsVisible = false;
        }

        public RenderInfo3D RenderInfo { get; }

        private void Render()
        {
            if (Engine.Rendering.State.IsShadowPass)
                return;

            RenderBoneLink(Hips, Spine);
            RenderBoneLink(Spine, Chest);
            RenderBoneLink(Chest, UpperChest.Node is not null ? UpperChest : Neck);
            if (UpperChest.Node is not null)
                RenderBoneLink(UpperChest, Neck);
            RenderBoneLink(Neck, Head);
            RenderBoneLink(Head, Jaw);

            RenderBodySide(Left);
            RenderBodySide(Right);
        }

        private void RenderBodySide(BodySide side)
        {
            RenderBoneLink(side.Shoulder, side.Arm);
            RenderBoneLink(side.Arm, side.Elbow);
            RenderBoneLink(side.Elbow, side.Wrist);
            RenderFinger(side.Hand.Thumb);
            RenderFinger(side.Hand.Index);
            RenderFinger(side.Hand.Middle);
            RenderFinger(side.Hand.Ring);
            RenderFinger(side.Hand.Pinky);

            RenderBoneLink(side.Leg, side.Knee);
            RenderBoneLink(side.Knee, side.Foot);
            RenderBoneLink(side.Foot, side.Toes);
        }

        private void RenderFinger(BodySide.Fingers.Finger finger)
        {
            RenderBoneLink(finger.Proximal, finger.Intermediate);
            RenderBoneLink(finger.Intermediate, finger.Distal);
        }

        private static void RenderBoneLink(BoneDef start, BoneDef end)
        {
            if (start.Node?.Transform is null || end.Node?.Transform is null)
                return;

            Vector3 startPos = start.Node.Transform.WorldTranslation;
            Vector3 endPos = end.Node.Transform.WorldTranslation;
            Engine.Rendering.Debug.RenderPoint(startPos, ColorF4.Red);
            Engine.Rendering.Debug.RenderPoint(endPos, ColorF4.Red);
            Engine.Rendering.Debug.RenderLine(startPos, endPos, ColorF4.Red);
        }

        public class BoneDef : XRBase
        {
            private SceneNode? _node;
            public SceneNode? Node
            {
                get => _node;
                set => SetField(ref _node, value);
            }

            private Matrix4x4 _localBindPose = Matrix4x4.Identity;
            public Matrix4x4 LocalBindPose
            {
                get => _localBindPose;
                set => SetField(ref _localBindPose, value);
            }

            private Matrix4x4 _worldBindPose = Matrix4x4.Identity;
            public Matrix4x4 WorldBindPose
            {
                get => _worldBindPose;
                set => SetField(ref _worldBindPose, value);
            }

            private IKRotationConstraintComponent? _constraints;
            public IKRotationConstraintComponent? Constraints
            {
                get => _constraints;
                set => SetField(ref _constraints, value);
            }

            protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
            {
                base.OnPropertyChanged(propName, prev, field);
                switch (propName)
                {
                    case nameof(Node):
                        if (Node is not null)
                        {
                            LocalBindPose = Node.Transform.LocalMatrix;
                            WorldBindPose = Node.Transform.BindMatrix;
                        }
                        break;
                }
            }

            public void ResetPose()
                => Node?.Transform?.ResetPose();
        }

        public BoneDef Hips { get; } = new();
        public BoneDef Spine { get; } = new();
        public BoneDef Chest { get; } = new();
        public BoneDef UpperChest { get; } = new();
        public BoneDef Neck { get; } = new();
        public BoneDef Head { get; } = new();
        public BoneDef Jaw { get; } = new();
        /// <summary>
        /// Position in space where the eyes are looking at
        /// </summary>
        public BoneDef EyesTarget { get; } = new();

        public class BodySide : XRBase
        {
            public class Fingers : XRBase
            {
                public class Finger : XRBase
                {
                    /// <summary>
                    /// First bone
                    /// </summary>
                    public BoneDef Proximal { get; } = new();
                    /// <summary>
                    /// Second bone
                    /// </summary>
                    public BoneDef Intermediate { get; } = new();
                    /// <summary>
                    /// Last bone
                    /// </summary>
                    public BoneDef Distal { get; } = new();

                    public void ResetPose()
                    {
                        Proximal.ResetPose();
                        Intermediate.ResetPose();
                        Distal.ResetPose();
                    }
                }

                public Finger Pinky { get; } = new();
                public Finger Ring { get; } = new();
                public Finger Middle { get; } = new();
                public Finger Index { get; } = new();
                public Finger Thumb { get; } = new();

                public void ResetPose()
                {
                    Pinky.ResetPose();
                    Ring.ResetPose();
                    Middle.ResetPose();
                    Index.ResetPose();
                    Thumb.ResetPose();
                }
            }

            public BoneDef Shoulder { get; } = new();
            public BoneDef Arm { get; } = new();
            public BoneDef Elbow { get; } = new();
            public BoneDef Wrist { get; } = new();
            public Fingers Hand { get; } = new();
            public BoneDef Leg { get; } = new();
            public BoneDef Knee { get; } = new();
            public BoneDef Foot { get; } = new();
            public BoneDef Toes { get; } = new();
            public BoneDef Eye { get; } = new();

            public void ResetPose()
            {
                Shoulder.ResetPose();
                Arm.ResetPose();
                Elbow.ResetPose();
                Wrist.ResetPose();
                Hand.ResetPose();
                Leg.ResetPose();
                Knee.ResetPose();
                Foot.ResetPose();
                Toes.ResetPose();
                Eye.ResetPose();
            }
        }

        public BodySide Left { get; } = new();
        public BodySide Right { get; } = new();

        public void ResetAllTransformsToBindPose()
        {
            ResetRuntimeAnimationDiagnostics();
            ClearRuntimeMuscleState();
            GetSiblingComponent<HumanoidIKSolverComponent>(false)?.ClearAnimatedIKGoals();
            if (SceneNode is not null)
                ResetBindPoseRecursive(SceneNode);

            SyncPreviewRenderMatrices();
        }

        private static void ResetBindPoseRecursive(SceneNode node)
        {
            node.Transform.ResetPose();
            foreach (TransformBase child in node.Transform.Children)
            {
                if (child.SceneNode is not null)
                    ResetBindPoseRecursive(child.SceneNode);
            }
        }

        private void ClearRuntimeMuscleState()
        {
            lock (_muscleValuesLock)
            {
                _muscleValues.Clear();
                _rawHumanoidValues.Clear();
            }

            Settings.CurrentValues.Clear();
        }

        public RenderInfo[] RenderedObjects { get; }

        //public static void AdjustRootConstraints(BoneChainItem rootBone, Vector3 overallMovement)
        //{
        //    // Example: Allow more movement if the target is far away
        //    float distance = overallMovement.Length();

        //    rootBone.Def.MaxPositionOffset = new Vector3(distance * 0.1f, distance * 0.1f, distance * 0.1f);
        //    rootBone.Def.MinPositionOffset = -rootBone.Def.MaxPositionOffset;
        //}

        public void SetFromNode()
        {
            //Debug.Out(SceneNode.PrintTree());

            //Start at the hips
            Hips.Node = SceneNode.FindDescendantByName("Hips", StringComparison.InvariantCultureIgnoreCase);

            //Find middle bones
            FindChildrenFor(Hips, [
                (Spine, ByName("Spine")),
                (Chest, ByName("Chest")),
                (Left.Leg, BySideAwarePosition(LegNameContains, isLeft: true, x => x.X < 0.0f)),
                (Right.Leg, BySideAwarePosition(LegNameContains, isLeft: false, x => x.X > 0.0f)),
            ]);

            if (Spine.Node is not null && Chest.Node is null)
                FindChildrenFor(Spine, [
                    (Chest, ByName("Chest")),
                ]);

            if (Chest.Node is not null)
                FindChildrenFor(Chest, [
                    (UpperChest, ByNameContainsAny("UpperChest", "Upper_Chest")),
                    (Neck, ByName("Neck")),
                    (Head, ByName("Head")),
                    (Left.Shoulder, BySideAwarePositionAndDoesNotContain("Shoulder", TwistBoneMismatch, isLeft: true, x => x.X < 0.0f)),
                    (Right.Shoulder, BySideAwarePositionAndDoesNotContain("Shoulder", TwistBoneMismatch, isLeft: false, x => x.X > 0.0f)),
                ]);

            // If UpperChest was found, shoulders/neck/head may be children of UpperChest rather than Chest.
            if (UpperChest.Node is not null)
                FindChildrenFor(UpperChest, [
                    (Neck, ByName("Neck")),
                    (Head, ByName("Head")),
                    (Left.Shoulder, BySideAwarePositionAndDoesNotContain("Shoulder", TwistBoneMismatch, isLeft: true, x => x.X < 0.0f)),
                    (Right.Shoulder, BySideAwarePositionAndDoesNotContain("Shoulder", TwistBoneMismatch, isLeft: false, x => x.X > 0.0f)),
                ]);

            if (Neck.Node is not null && Head.Node is null)
                FindChildrenFor(Neck, [
                    (Head, ByName("Head")),
                ]);

            //Find eye bones
            if (Head.Node is not null)
            {
                Jaw.Node = Head.Node.FindDescendantByName("Jaw", StringComparison.InvariantCultureIgnoreCase);
                FindChildrenFor(Head, [
                    (Left.Eye, BySideAwarePosition("Eye", isLeft: true, x => x.X < 0.0f)),
                    (Right.Eye, BySideAwarePosition("Eye", isLeft: false, x => x.X > 0.0f)),
                ]);
            }

            //Find shoulder bones
            if (Left.Shoulder.Node is not null)
                FindChildrenFor(Left.Shoulder, [
                    (Left.Arm, ByNameContainsAllAndDoesNotContain(TwistBoneMismatch, "Arm")),
                    (Left.Elbow, ByNameContainsAnyAndDoesNotContain(ElbowNameMatches, TwistBoneMismatch)),
                    (Left.Wrist, ByNameContainsAnyAndDoesNotContain(HandNameMatches, TwistBoneMismatch)),
                ]);

            if (Right.Shoulder.Node is not null)
            {
                FindChildrenFor(Right.Shoulder, [
                    (Right.Arm, ByNameContainsAllAndDoesNotContain(TwistBoneMismatch, "Arm")),
                    (Right.Elbow, ByNameContainsAnyAndDoesNotContain(ElbowNameMatches, TwistBoneMismatch)),
                    (Right.Wrist, ByNameContainsAnyAndDoesNotContain(HandNameMatches, TwistBoneMismatch)),
                ]);
            }

            if (Left.Arm.Node is not null && Left.Elbow.Node is null)
                FindChildrenFor(Left.Arm, [
                    (Left.Elbow, ByNameContainsAnyAndDoesNotContain(ElbowNameMatches, TwistBoneMismatch)),
                    (Left.Wrist, ByNameContainsAnyAndDoesNotContain(HandNameMatches, TwistBoneMismatch)),
                ]);

            if (Right.Arm.Node is not null && Right.Elbow.Node is null)
                FindChildrenFor(Right.Arm, [
                    (Right.Elbow, ByNameContainsAnyAndDoesNotContain(ElbowNameMatches, TwistBoneMismatch)),
                    (Right.Wrist, ByNameContainsAnyAndDoesNotContain(HandNameMatches, TwistBoneMismatch)),
                ]);

            if (Left.Elbow.Node is not null && Left.Wrist.Node is null)
                FindChildrenFor(Left.Elbow, [
                    (Left.Wrist, ByNameContainsAnyAndDoesNotContain(HandNameMatches, TwistBoneMismatch)),
                ]);

            if (Right.Elbow.Node is not null && Right.Wrist.Node is null)
                FindChildrenFor(Right.Elbow, [
                    (Right.Wrist, ByNameContainsAnyAndDoesNotContain(HandNameMatches, TwistBoneMismatch)),
                ]);

            //Find finger bones
            if (Left.Wrist.Node is not null)
                FindFingerChainsForWrist(Left.Wrist.Node, Left.Hand);

            if (Right.Wrist.Node is not null)
                FindFingerChainsForWrist(Right.Wrist.Node, Right.Hand);

            //Find leg bones
            if (Left.Leg.Node is not null)
                FindChildrenFor(Left.Leg, [
                    (Left.Knee, ByNameContainsAll(KneeNameContains)),
                    (Left.Foot, ByNameContainsAnyAndDoesNotContain(FootNameMatches, TwistBoneMismatch)),
                    (Left.Toes, ByNameContainsAll(ToeNameContains)),
                ]);

            if (Right.Leg.Node is not null)
                FindChildrenFor(Right.Leg, [
                    (Right.Knee, ByNameContainsAll(KneeNameContains)),
                    (Right.Foot, ByNameContainsAnyAndDoesNotContain(FootNameMatches,TwistBoneMismatch)),
                    (Right.Toes, ByNameContainsAll(ToeNameContains)),
                ]);

            if (Left.Knee.Node is not null && Left.Foot.Node is null)
                FindChildrenFor(Left.Knee, [
                    (Left.Foot, ByNameContainsAnyAndDoesNotContain(FootNameMatches, TwistBoneMismatch)),
                    (Left.Toes, ByNameContainsAll(ToeNameContains)),
                ]);

            if (Right.Knee.Node is not null && Right.Foot.Node is null)
                FindChildrenFor(Right.Knee, [
                    (Right.Foot, ByNameContainsAnyAndDoesNotContain(FootNameMatches, TwistBoneMismatch)),
                    (Right.Toes, ByNameContainsAll(ToeNameContains)),
                ]);

            if (Left.Foot.Node is not null && Left.Toes.Node is null)
                FindChildrenFor(Left.Foot, [
                    (Left.Toes, ByNameContainsAll(ToeNameContains)),
                ]);

            if (Right.Foot.Node is not null && Right.Toes.Node is null)
                FindChildrenFor(Right.Foot, [
                    (Right.Toes, ByNameContainsAll(ToeNameContains)),
                ]);

            // Log diagnostic information about bone mapping results
            LogBoneMappingDiagnostics();

            bool shouldAutoProfile = string.IsNullOrWhiteSpace(Settings.ProfileSource)
                || string.Equals(Settings.ProfileSource, "auto-generated", StringComparison.OrdinalIgnoreCase);

            if (shouldAutoProfile)
            {
                Settings.BoneAxisMappings.Clear();
            }

            // -- Build avatar profile (replaces the old ComputeAutoAxisMappings) --
            // This derives per-bone axis mappings with confidence scoring,
            // sets IsIKCalibrated based on overall confidence, and applies
            // conservative fallback when confidence is low.
            var profileResult = AvatarHumanoidProfileBuilder.BuildProfile(this);
            if (shouldAutoProfile)
                Settings.ProfileSource = "auto-generated";

            string avatarName = SceneNode?.Name ?? "(unknown)";
            AvatarHumanoidProfileBuilder.LogProfileSummary(profileResult, avatarName);

            // Apply confidence-based fallback
            ApplyConfidenceFallback(profileResult);

            // Log bind-pose snapshot for each mapped bone.
            LogBindPoseSnapshot();
        }

        private void LogBindPoseSnapshot()
        {
            void LogBone(string label, BoneDef def)
            {
                if (def.Node is null) return;
                var tfm = def.Node.GetTransformAs<Transform>(false);
                if (tfm is null) return;
                var bs = tfm.BindState;
                var axMap = GetBoneAxisMapping(def.Node);
                string axStr = axMap.HasValue
                    ? $"twist={axMap.Value.TwistAxis} fb={axMap.Value.FrontBackAxis} lr={axMap.Value.LeftRightAxis}"
                    : "default(Y/X/Z)";
                Debug.Animation(
                    $"[BindPose] {label,-20} node='{def.Node.Name}' " +
                    $"bindRot=({bs.Rotation.X:F4},{bs.Rotation.Y:F4},{bs.Rotation.Z:F4},{bs.Rotation.W:F4}) " +
                    $"bindPos=({bs.Translation.X:F3},{bs.Translation.Y:F3},{bs.Translation.Z:F3}) " +
                    $"axes={axStr}");
            }

            LogBone("Hips", Hips);
            LogBone("Spine", Spine);
            LogBone("Chest", Chest);
            LogBone("UpperChest", UpperChest);
            LogBone("Neck", Neck);
            LogBone("Head", Head);
            LogBone("L.Shoulder", Left.Shoulder);
            LogBone("L.Arm", Left.Arm);
            LogBone("L.Elbow", Left.Elbow);
            LogBone("L.Wrist", Left.Wrist);
            LogBone("L.Leg", Left.Leg);
            LogBone("L.Knee", Left.Knee);
            LogBone("L.Foot", Left.Foot);
            LogBone("R.Shoulder", Right.Shoulder);
            LogBone("R.Arm", Right.Arm);
            LogBone("R.Elbow", Right.Elbow);
            LogBone("R.Wrist", Right.Wrist);
            LogBone("R.Leg", Right.Leg);
            LogBone("R.Knee", Right.Knee);
            LogBone("R.Foot", Right.Foot);
        }

        private bool _musclePoseLoggedOnce;
        private bool _limbBasisLogged;

        /// <summary>
        /// Logs a one-time snapshot of live muscle values and the resulting degree
        /// values for key bones. Written to the Animation log category.
        /// </summary>
        private void LogMusclePoseSnapshot(float[] muscleSnapshot)
        {
            if (_musclePoseLoggedOnce) return;
            _musclePoseLoggedOnce = true;

            void LogMuscle(string label, EHumanoidValue val)
            {
                Vector2 range = Settings.GetResolvedMuscleRotationDegRange(val);
                float raw = GetMuscleValue(muscleSnapshot, val);
                float deg = MapMuscleToDeg(val, raw);
                Debug.Animation($"[MusclePose] {label,-30} muscle={raw,8:F4}  deg={deg,8:F2}  range=({range.X:F1},{range.Y:F1})");
            }

            Debug.Animation("[MusclePose] === One-time muscle snapshot ===");

            // Spine
            LogMuscle("SpineFrontBack", EHumanoidValue.SpineFrontBack);
            LogMuscle("SpineTwist", EHumanoidValue.SpineTwistLeftRight);
            LogMuscle("NeckNod", EHumanoidValue.NeckNodDownUp);
            LogMuscle("HeadNod", EHumanoidValue.HeadNodDownUp);

            // Left arm (ranges match ApplyLimbMuscles)
            LogMuscle("L.ShoulderDownUp", EHumanoidValue.LeftShoulderDownUp);
            LogMuscle("L.ShoulderFrontBack", EHumanoidValue.LeftShoulderFrontBack);
            LogMuscle("L.ArmDownUp", EHumanoidValue.LeftArmDownUp);
            LogMuscle("L.ArmFrontBack", EHumanoidValue.LeftArmFrontBack);
            LogMuscle("L.ArmTwist", EHumanoidValue.LeftArmTwistInOut);
            LogMuscle("L.ForearmStretch", EHumanoidValue.LeftForearmStretch);
            LogMuscle("L.ForearmTwist", EHumanoidValue.LeftForearmTwistInOut);

            // Right arm
            LogMuscle("R.ShoulderDownUp", EHumanoidValue.RightShoulderDownUp);
            LogMuscle("R.ShoulderFrontBack", EHumanoidValue.RightShoulderFrontBack);
            LogMuscle("R.ArmDownUp", EHumanoidValue.RightArmDownUp);
            LogMuscle("R.ArmFrontBack", EHumanoidValue.RightArmFrontBack);
            LogMuscle("R.ForearmStretch", EHumanoidValue.RightForearmStretch);

            // Left leg (ranges match ApplyLimbMuscles)
            LogMuscle("L.UpperLegFrontBack", EHumanoidValue.LeftUpperLegFrontBack);
            LogMuscle("L.UpperLegInOut", EHumanoidValue.LeftUpperLegInOut);
            LogMuscle("L.LowerLegStretch", EHumanoidValue.LeftLowerLegStretch);
            LogMuscle("L.FootUpDown", EHumanoidValue.LeftFootUpDown);

            // Right leg
            LogMuscle("R.UpperLegFrontBack", EHumanoidValue.RightUpperLegFrontBack);
            LogMuscle("R.LowerLegStretch", EHumanoidValue.RightLowerLegStretch);

            Debug.Animation("[MusclePose] === End snapshot ===");
        }

        /// <summary>
        /// Logs a one-time snapshot of the body-space basis axes derived from the avatar bind pose,
        /// the limb twist axes, and the swing axes passed into ApplyBindRelativeSwingTwistWorldAxes.
        /// Expected values for a well-formed T-pose import:
        ///   bodyLeft    ˜ ( 1, 0,  0)  (avatar left, camera right)
        ///   bodyUp      ˜ ( 0, 1,  0)
        ///   bodyForward ˜ ( 0, 0,  1)  (avatar facing camera)
        ///   armTwist    ˜ (±1, 0,  0)  (arm points sideways in T-pose)
        ///   legTwist    ˜ ( 0,-1,  0)  (leg points downward)
        /// </summary>
        private void LogBodyBasisDiagnostic(
            Vector3 bodyLeft,
            Vector3 bodyUp,
            Vector3 bodyForward,
            Vector3 armTwistAxisWorld,
            Vector3 legTwistAxisWorld,
            Vector3 armPitchAxisWorld,
            Vector3 legPitchAxisWorld)
        {
            if (_limbBasisLogged) return;
            _limbBasisLogged = true;

            static string V(Vector3 v) => $"({v.X,6:F3},{v.Y,6:F3},{v.Z,6:F3})";

            Debug.Animation("[BodyBasis] === One-time body-basis snapshot ===");
            Debug.Animation($"[BodyBasis]  bodyLeft         {V(bodyLeft)}   expected ˜ ( 1, 0, 0)");
            Debug.Animation($"[BodyBasis]  bodyUp           {V(bodyUp)}   expected ˜ ( 0, 1, 0)");
            Debug.Animation($"[BodyBasis]  bodyForward      {V(bodyForward)}   expected ˜ ( 0, 0, 1)");
            Debug.Animation($"[BodyBasis]  armTwistWorld    {V(armTwistAxisWorld)}   expected ˜ (±1, 0, 0)");
            Debug.Animation($"[BodyBasis]  legTwistWorld    {V(legTwistAxisWorld)}   expected ˜ ( 0,-1, 0)");
            Debug.Animation($"[BodyBasis]  armPitchAxis     {V(armPitchAxisWorld)}   (Down-Up axis for left arm-side limbs)");
            Debug.Animation($"[BodyBasis]  legPitchAxis     {V(legPitchAxisWorld)}   (Front-Back axis for both legs)");
            Debug.Animation("[BodyBasis] === End body-basis snapshot ===");
        }

        private void LogBoneMappingDiagnostics()
        {
            var missing = new System.Collections.Generic.List<string>();

            if (Hips.Node is null) missing.Add("Hips");
            if (Spine.Node is null) missing.Add("Spine");
            if (Chest.Node is null) missing.Add("Chest");
            if (Neck.Node is null) missing.Add("Neck");
            if (Head.Node is null) missing.Add("Head");
            if (Left.Shoulder.Node is null) missing.Add("LeftShoulder");
            if (Left.Arm.Node is null) missing.Add("LeftArm");
            if (Left.Elbow.Node is null) missing.Add("LeftElbow");
            if (Left.Wrist.Node is null) missing.Add("LeftWrist");
            if (Right.Shoulder.Node is null) missing.Add("RightShoulder");
            if (Right.Arm.Node is null) missing.Add("RightArm");
            if (Right.Elbow.Node is null) missing.Add("RightElbow");
            if (Right.Wrist.Node is null) missing.Add("RightWrist");
            if (Left.Leg.Node is null) missing.Add("LeftLeg");
            if (Left.Knee.Node is null) missing.Add("LeftKnee");
            if (Left.Foot.Node is null) missing.Add("LeftFoot");
            if (Right.Leg.Node is null) missing.Add("RightLeg");
            if (Right.Knee.Node is null) missing.Add("RightKnee");
            if (Right.Foot.Node is null) missing.Add("RightFoot");

            bool complete = missing.Count == 0;
            _boneMappingComplete = complete;

            // Only log when the mapping state changes (prevents spam).
            if (_lastBoneMappingComplete.HasValue && _lastBoneMappingComplete.Value == complete)
                return;

            _lastBoneMappingComplete = complete;

            if (!complete)
            {
                Debug.Animation($"[HumanoidComponent] Bone mapping incomplete on '{SceneNode.Name}'. Missing bones: {string.Join(", ", missing)}");
                Debug.Animation($"[HumanoidComponent] Humanoid animations targeting missing bones will have no visible effect.");
            }
            else
            {
                Debug.Animation($"[HumanoidComponent] Bone mapping complete on '{SceneNode.Name}'. All required bones found.");
            }
        }

        /// <summary>
        /// Applies conservative fallback behavior when profile confidence is low.
        /// - IK goals are disabled (policy set to Ignore).
        /// - A diagnostic warning is emitted.
        /// Muscle channels and root motion still play regardless of confidence.
        /// </summary>
        private void ApplyConfidenceFallback(AvatarHumanoidProfileBuilder.ProfileResult profileResult)
        {
            const float lowConfidenceThreshold = 0.6f;
            if (profileResult.OverallConfidence >= lowConfidenceThreshold)
                return;

            // Force conservative IK behavior when confidence is low
            if (Settings.IKGoalPolicy != EHumanoidIKGoalPolicy.Ignore)
            {
                Debug.Animation(
                    $"[HumanoidComponent] Low profile confidence ({profileResult.OverallConfidence:P0}) — " +
                    $"overriding IK goal policy to Ignore for safety. " +
                    $"Set IKGoalPolicy = AlwaysApply to override.");
                Settings.IKGoalPolicy = EHumanoidIKGoalPolicy.Ignore;
            }

            // Emit compact diagnostic with suggestions
            Debug.Animation(
                $"[HumanoidComponent] Low confidence avatar profile ({profileResult.OverallConfidence:P0}). " +
                $"Muscle channels and root motion will still play normally. " +
                $"{profileResult.FallbackBoneCount}/{profileResult.ProfiledBoneCount} bones used default/inherited axis mappings. " +
                $"Suggestions: (1) Check that the model has a proper T-pose bind pose. " +
                $"(2) Verify bone naming matches standard humanoid conventions. " +
                $"(3) Manually override axis mappings via Settings.BoneAxisMappings for problem bones.");
        }

        private bool _firstPlaybackLogged;

        /// <summary>
        /// Logs a one-time compact diagnostic on first playback of a humanoid clip.
        /// Called from <see cref="ApplyMusclePose"/> on the first frame where muscle data exists.
        /// </summary>
        private void LogFirstPlaybackDiagnostic()
        {
            if (_firstPlaybackLogged)
                return;
            _firstPlaybackLogged = true;

            string avatarName = SceneNode?.Name ?? "(unknown)";
            string source = Settings.ProfileSource ?? "unknown";
            float conf = Settings.ProfileConfidence;
            string ikPolicy = Settings.IKGoalPolicy.ToString();
            bool ikCalibrated = Settings.IsIKCalibrated;
            int axisCount = Settings.BoneAxisMappings.Count;

            Debug.Animation(
                $"[HumanoidComponent] First playback on '{avatarName}': " +
                $"profile={source} confidence={conf:P0} " +
                $"axisMappings={axisCount} " +
                $"IK={ikPolicy} calibrated={ikCalibrated}");
        }

        private const string LegNameContains = "Leg";
        private const string ToeNameContains = "Toe";
        private const string KneeNameContains = "Knee";
        private static readonly string[] FootNameMatches = ["Foot", "Ankle"];
        private static readonly string[] TwistBoneMismatch = ["Twist"];
        private static readonly string[] ElbowNameMatches = ["Elbow"];
        private static readonly string[] HandNameMatches = ["Wrist", "Hand"];
        private static readonly string[] PinkyFingerNameMatches = ["pinky", "pinkie", "little"];
        private static readonly string[] RingFingerNameMatches = ["ring"];
        private static readonly string[] MiddleFingerNameMatches = ["middle"];
        private static readonly string[] IndexFingerNameMatches = ["index"];
        private static readonly string[] ThumbFingerNameMatches = ["thumb"];
        private static readonly string[] ProximalFingerSegmentMatches = ["1", "01", "prox", "proximal"];
        private static readonly string[] IntermediateFingerSegmentMatches = ["2", "02", "inter", "intermediate"];
        private static readonly string[] DistalFingerSegmentMatches = ["3", "03", "dist", "distal", "tip"];
        private static readonly string[] FingerBoneMismatch = ["metacarp", "twist"];

        /// <summary>
        /// Estimates the avatar-space motion scale used by imported Unity humanoid
        /// root-motion and IK goal curves. This matches the bind-pose leg-length
        /// heuristic used by the animation-driven IK path.
        /// </summary>
        public float EstimateAnimatedMotionScale()
        {
            Vector3 hips = Hips.WorldBindPose.Translation;
            float total = 0.0f;
            int count = 0;

            if (Left.Foot.Node is not null)
            {
                float left = Vector3.Distance(hips, Left.Foot.WorldBindPose.Translation);
                if (left > 0.0001f)
                {
                    total += left;
                    count++;
                }
            }

            if (Right.Foot.Node is not null)
            {
                float right = Vector3.Distance(hips, Right.Foot.WorldBindPose.Translation);
                if (right > 0.0001f)
                {
                    total += right;
                    count++;
                }
            }

            if (count > 0)
                return total / count;

            float fallback = SceneNode.Transform.LossyWorldScale.Y;
            return fallback > 0.0001f ? fallback : 1.0f;
        }

        /// <summary>
        /// Applies root motion position as a bind-relative offset on the Hips bone.
        /// In Unity humanoid clips, RootT represents the body center (hips) position
        /// in absolute body space (e.g. RootT.y ˜ 1.0 = hip height above ground).
        /// We capture the first frame as a baseline and apply only the delta so that
        /// the bind-pose translation isn't double-counted.
        /// </summary>
        public void SetRootPosition(Vector3 position)
        {
            _currentRawRootPosition = position;
            ApplyRootPosition(_currentRawRootPosition);
        }

        public void SetRootPositionX(float x)
            => _currentRawRootPosition.X = x;

        public void SetRootPositionY(float y)
            => _currentRawRootPosition.Y = y;

        public void SetRootPositionZ(float z)
        {
            _currentRawRootPosition.Z = z;
            ApplyRootPosition(_currentRawRootPosition);
        }

        private void ApplyRootPosition(Vector3 position)
        {
            var hipsNode = Hips.Node;
            if (hipsNode is null)
                return;

            var tfm = hipsNode.GetTransformAs<Transform>(true);
            if (tfm is null)
                return;

            if (!_rootPositionBaseline.HasValue)
            {
                _rootPositionBaseline = position;
                Debug.Animation($"[RootMotion] Captured RootT baseline=({position.X:F4},{position.Y:F4},{position.Z:F4})");
            }

            Vector3 delta = (position - _rootPositionBaseline.Value) * EstimateAnimatedMotionScale();
            tfm.Translation = tfm.BindState.Translation + delta;
        }

        /// <summary>
        /// Resets the root motion baselines so that the next call to
        /// <see cref="SetRootPosition"/> / <see cref="SetRootRotation"/> recaptures
        /// a fresh baseline from the new animation clip's first frame.
        /// </summary>
        public void ResetRootMotionBaseline()
        {
            _rootPositionBaseline = null;
            _rootRotationBaseline = null;
            _rootRotationBaselineLogged = false;
            _currentBodyRotation = Quaternion.Identity;
            _currentRawRootPosition = Vector3.Zero;
            _currentRawRootRotation = Quaternion.Identity;
        }

        private Vector3? _rootPositionBaseline;
        private Quaternion? _rootRotationBaseline;
        private bool _rootRotationBaselineLogged;
        private Quaternion _currentBodyRotation = Quaternion.Identity;
        private Vector3 _currentRawRootPosition = Vector3.Zero;
        private Quaternion _currentRawRootRotation = Quaternion.Identity;

        public Vector3 CurrentRawBodyPosition => _currentRawRootPosition;
        public Quaternion CurrentRawBodyRotation => Quaternion.Normalize(_currentRawRootRotation);

        // -- Runtime debug overrides for muscle?rotation sign tuning ---------
        // These are NOT serialized. They let you flip axis signs at runtime
        // from the editor UI to quickly diagnose and fix retargeting issues.

        /// <summary>
        /// Per-bone-group sign overrides for the three rotation channels
        /// (yaw/twist, pitch/frontBack, roll/leftRight). 
        /// Multiply into the degree values before passing to ApplyBindRelativeSwingTwistWorldAxes.
        /// </summary>
        public struct LimbSignOverrides
        {
            public float YawSign;
            public float PitchSign;
            public float RollSign;
            /// <summary>When true, negate the blanket LH?RH angle negation for the yaw (twist) axis.</summary>
            public bool SkipBlanketNegateYaw;
            /// <summary>When true, negate the blanket LH?RH angle negation for the pitch (front/back) axis.</summary>
            public bool SkipBlanketNegatePitch;
            /// <summary>When true, negate the blanket LH?RH angle negation for the roll (left/right) axis.</summary>
            public bool SkipBlanketNegateRoll;
            /// <summary>When true, swap the frontBack and leftRight world axes.</summary>
            public bool SwapPitchRollAxes;

            public static LimbSignOverrides Default => new()
            {
                YawSign = 1.0f,
                PitchSign = 1.0f,
                RollSign = 1.0f,
                SkipBlanketNegateYaw = false,
                SkipBlanketNegatePitch = false,
                SkipBlanketNegateRoll = false,
                SwapPitchRollAxes = false,
            };
        }

        public LimbSignOverrides DebugArmSigns = LimbSignOverrides.Default;
        public LimbSignOverrides DebugForearmSigns = LimbSignOverrides.Default;
        public LimbSignOverrides DebugWristSigns = LimbSignOverrides.Default;
        public LimbSignOverrides DebugUpperLegSigns = LimbSignOverrides.Default;
        public LimbSignOverrides DebugKneeSigns = LimbSignOverrides.Default;
        public LimbSignOverrides DebugFootSigns = LimbSignOverrides.Default;
        public LimbSignOverrides DebugToesSigns = LimbSignOverrides.Default;
        public LimbSignOverrides DebugShoulderSigns = LimbSignOverrides.Default;

        // -- End debug overrides ---------------------------------------------

        /// <summary>
        /// Applies root motion rotation as a bind-relative rotation on the Hips bone.
        /// In Unity humanoid clips, RootQ represents the body center (hips) orientation.
        /// </summary>
        public void SetRootRotation(Quaternion rotation)
        {
            _currentRawRootRotation = rotation;
            ApplyRootRotation(_currentRawRootRotation);
        }

        public void SetRootRotationX(float x)
            => _currentRawRootRotation.X = x;

        public void SetRootRotationY(float y)
            => _currentRawRootRotation.Y = y;

        public void SetRootRotationZ(float z)
            => _currentRawRootRotation.Z = z;

        public void SetRootRotationW(float w)
        {
            _currentRawRootRotation.W = w;
            ApplyRootRotation(_currentRawRootRotation);
        }

        private void ApplyRootRotation(Quaternion rotation)
        {
            var hipsNode = Hips.Node;
            if (hipsNode is null)
                return;

            Quaternion raw = Quaternion.Normalize(rotation);
            if (!_rootRotationBaseline.HasValue)
            {
                _rootRotationBaseline = raw;
                if (!_rootRotationBaselineLogged)
                {
                    _rootRotationBaselineLogged = true;
                    Debug.Animation($"[RootMotion] Captured RootQ baseline raw=({raw.X:F4},{raw.Y:F4},{raw.Z:F4},{raw.W:F4})");
                }
            }

            Quaternion baseline = _rootRotationBaseline.Value;
            Quaternion effective = Quaternion.Normalize(Quaternion.Inverse(baseline) * raw);
            _currentBodyRotation = effective;

            if (_rootRotationBaselineLogged)
            {
                Debug.Animation($"[RootMotion] Applying RootQ raw=({raw.X:F4},{raw.Y:F4},{raw.Z:F4},{raw.W:F4}) effective=({effective.X:F4},{effective.Y:F4},{effective.Z:F4},{effective.W:F4})");
                _rootRotationBaselineLogged = false;
            }

            hipsNode.GetTransformAs<Transform>(true)?.SetBindRelativeRotation(effective);
        }

        private static Func<SceneNode, bool> ByNameContainsAny(params string[] names)
            => node => names.Any(name => node.Name?.Contains(name, StringComparison.InvariantCultureIgnoreCase) ?? false);

        private static Func<SceneNode, bool> ByNameContainsAnyAndDoesNotContain(string[] any, string[] none)
            => node =>
            any.Any(name => node.Name?.Contains(name, StringComparison.InvariantCultureIgnoreCase) ?? false) &&
            none.All(name => !(node.Name?.Contains(name, StringComparison.InvariantCultureIgnoreCase) ?? false));

        private static Func<SceneNode, bool> ByNameContainsAll(params string[] names)
            => node => names.All(name => node.Name?.Contains(name, StringComparison.InvariantCultureIgnoreCase) ?? false);

        private static Func<SceneNode, bool> ByNameContainsAllAndDoesNotContain(string[] none, params string[] names)
            => node =>
            names.All(name => node.Name?.Contains(name, StringComparison.InvariantCultureIgnoreCase) ?? false)
            && none.All(name => !(node.Name?.Contains(name, StringComparison.InvariantCultureIgnoreCase) ?? false));

        private static Func<SceneNode, bool> ByNameContainsAny(StringComparison comp, params string[] names)
            => node => names.Any(name => node.Name?.Contains(name, comp) ?? false);

        private static Func<SceneNode, bool> ByNameContainsAll(StringComparison comp, params string[] names)
            => node => names.All(name => node.Name?.Contains(name, comp) ?? false);
        
        private static Func<SceneNode, bool> ByPosition(string nameContains, Func<Vector3, bool> posMatch, StringComparison comp = StringComparison.InvariantCultureIgnoreCase)
            => node => (nameContains is null || (node.Name?.Contains(nameContains, comp) ?? false)) && posMatch(node.Transform.LocalTranslation);

        private enum ESideHint
        {
            Unknown,
            Left,
            Right,
        }

        private static Func<SceneNode, bool> BySideAwarePosition(string nameContains, bool isLeft, Func<Vector3, bool> posMatch, StringComparison comp = StringComparison.InvariantCultureIgnoreCase)
            => node =>
            {
                if (nameContains is not null && !(node.Name?.Contains(nameContains, comp) ?? false))
                    return false;

                return GetSideHint(node.Name) switch
                {
                    ESideHint.Left => isLeft,
                    ESideHint.Right => !isLeft,
                    _ => posMatch(node.Transform.LocalTranslation),
                };
            };

        private static Func<SceneNode, bool> BySideAwarePositionAndDoesNotContain(string nameContains, string[] none, bool isLeft, Func<Vector3, bool> posMatch, StringComparison comp = StringComparison.InvariantCultureIgnoreCase)
            => node =>
            {
                if (!BySideAwarePosition(nameContains, isLeft, posMatch, comp)(node))
                    return false;

                return none.All(name => !(node.Name?.Contains(name, comp) ?? false));
            };

        private static Func<SceneNode, bool> ByName(string name, StringComparison comp = StringComparison.InvariantCultureIgnoreCase)
            => node => node.Name?.Equals(name, comp) ?? false;

        private static ESideHint GetSideHint(string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return ESideHint.Unknown;

            if (name.Contains("Left", StringComparison.InvariantCultureIgnoreCase))
                return ESideHint.Left;

            if (name.Contains("Right", StringComparison.InvariantCultureIgnoreCase))
                return ESideHint.Right;

            if (HasStandaloneSideToken(name, 'L'))
                return ESideHint.Left;

            if (HasStandaloneSideToken(name, 'R'))
                return ESideHint.Right;

            return ESideHint.Unknown;
        }

        private static bool HasStandaloneSideToken(string name, char token)
        {
            for (int i = 0; i < name.Length; i++)
            {
                if (char.ToUpperInvariant(name[i]) != token)
                    continue;

                bool startsToken = i == 0 || !char.IsLetterOrDigit(name[i - 1]);
                bool endsToken = i == name.Length - 1 || !char.IsLetterOrDigit(name[i + 1]);
                if (startsToken && endsToken)
                    return true;
            }

            return false;
        }

        private static void FindFingerChainsForWrist(SceneNode wrist, BodySide.Fingers hand)
        {
            FindFingerChain(wrist, hand.Pinky, PinkyFingerNameMatches);
            FindFingerChain(wrist, hand.Ring, RingFingerNameMatches);
            FindFingerChain(wrist, hand.Middle, MiddleFingerNameMatches);
            FindFingerChain(wrist, hand.Index, IndexFingerNameMatches);
            FindFingerChain(wrist, hand.Thumb, ThumbFingerNameMatches);
        }

        private static void FindFingerChain(SceneNode wrist, BodySide.Fingers.Finger finger, string[] aliases)
        {
            finger.Proximal.Node ??= FindFingerBone(wrist, aliases, ProximalFingerSegmentMatches);

            var intermediateRoot = finger.Proximal.Node ?? wrist;
            finger.Intermediate.Node ??= FindFingerBone(intermediateRoot, aliases, IntermediateFingerSegmentMatches);

            var distalRoot = finger.Intermediate.Node ?? finger.Proximal.Node ?? wrist;
            finger.Distal.Node ??= FindFingerBone(distalRoot, aliases, DistalFingerSegmentMatches);
        }

        private static SceneNode? FindFingerBone(SceneNode searchRoot, string[] aliases, string[] segmentTokens)
            => searchRoot.FindDescendant(transform =>
            {
                var node = transform.SceneNode;
                if (node is null || ReferenceEquals(node, searchRoot))
                    return false;

                string? name = node.Name;
                return NameContainsAny(name, aliases)
                    && NameContainsAny(name, segmentTokens)
                    && !NameContainsAny(name, FingerBoneMismatch);
            });

        private static bool NameContainsAny(string? name, string[] terms, StringComparison comparison = StringComparison.InvariantCultureIgnoreCase)
            => name is not null && terms.Any(term => name.Contains(term, comparison));

        private static void FindChildrenFor(BoneDef def, (BoneDef, Func<SceneNode, bool>)[] childSearch)
        {
            var children = def?.Node?.Transform.Children;
            if (children is not null)
                foreach (TransformBase child in children)
                    SetNodeRefs(child, childSearch);
        }

        private static void SetNodeRefs(TransformBase child, (BoneDef, Func<SceneNode, bool>)[] values)
        {
            var node = child.SceneNode;
            if (node is null)
                return;

            foreach ((BoneDef def, var func) in values)
                if (func(node))
                    def.Node = node;
        }

        /// <summary>
        /// Resets the pose of the humanoid to the default pose (T-pose).
        /// </summary>
        public void ResetPose()
        {
            ResetRuntimeAnimationDiagnostics();
            ClearRuntimeMuscleState();
            GetSiblingComponent<HumanoidIKSolverComponent>(false)?.ClearAnimatedIKGoals();
            ResetMappedTransformsToBindPose(includeEyesTarget: false);

            SyncPreviewRenderMatrices();
        }

        public void ApplyPosePreviewMode()
        {
            switch (PosePreviewMode)
            {
                case EHumanoidPosePreviewMode.MeshBindPose:
                    ResetAllTransformsToBindPose();
                    break;
                case EHumanoidPosePreviewMode.TPose:
                    ResetPose();
                    break;
                case EHumanoidPosePreviewMode.NeutralMusclePose:
                    ApplyNeutralPosePreview();
                    break;
                case EHumanoidPosePreviewMode.AnimatedPose:
                default:
                    ClearRuntimeMuscleState();
                    break;
            }
        }

        private void ResetMappedTransformsToBindPose(bool includeEyesTarget)
        {
            Hips.ResetPose();
            Spine.ResetPose();
            Chest.ResetPose();
            UpperChest.ResetPose();
            Neck.ResetPose();
            Head.ResetPose();
            Jaw.ResetPose();
            if (includeEyesTarget)
                EyesTarget.ResetPose();
            Left.ResetPose();
            Right.ResetPose();
        }

        private void ResetRuntimeAnimationDiagnostics()
        {
            ResetRootMotionBaseline();
            _leftKneeClampLogged = false;
            _rightKneeClampLogged = false;
        }

        private void ApplyNeutralPosePreview()
        {
            ResetAllTransformsToBindPose();
            if (SceneNode is null)
                return;

            foreach (string boneName in Settings.NeutralPoseBoneRotations.Keys)
                ApplyNeutralPoseBoneRotation(ResolveNeutralPoseMappedNodeByStoredKey(boneName));

            SyncPreviewRenderMatrices();
        }

        private void SyncPreviewRenderMatrices()
        {
            TransformBase? rootTransform = SceneNode?.Transform;
            if (rootTransform is null)
                return;

            rootTransform.RecalculateMatrixHierarchy(
                forceWorldRecalc: true,
                setRenderMatrixNow: true,
                childRecalcType: Engine.Rendering.Settings.RecalcChildMatricesLoopType).Wait();
        }

        private void ApplyNeutralPoseBoneRotation(SceneNode? node)
        {
            if (node?.Transform is null)
                return;

            Quaternion rotation = GetNeutralPoseBoneRotation(node);
            node.GetTransformAs<Transform>(true)?.SetBindRelativeRotation(rotation);
        }

        private void ApplyIKTargetWorldMatrix((TransformBase? tfm, Matrix4x4 offset) binding, Matrix4x4 desiredWorldMatrix)
        {
            if (binding.tfm is null)
                return;

            Matrix4x4 actualWorldMatrix = desiredWorldMatrix;
            if (Matrix4x4.Invert(binding.offset, out Matrix4x4 inverseOffset))
                actualWorldMatrix = inverseOffset * desiredWorldMatrix;

            if (binding.tfm is Transform concrete)
            {
                if (!Matrix4x4.Decompose(actualWorldMatrix, out _, out Quaternion rotation, out Vector3 translation))
                {
                    translation = actualWorldMatrix.Translation;
                    rotation = Quaternion.Identity;
                }

                concrete.SetWorldTranslationRotation(translation, Quaternion.Normalize(rotation));
                concrete.RecalculateMatrices(forceWorldRecalc: true);
                return;
            }

            binding.tfm.DeriveWorldMatrix(actualWorldMatrix);
        }

        private static string GetDefaultIKTargetNodeName(EHumanoidIKTarget target)
            => target switch
            {
                EHumanoidIKTarget.Head => "HeadTarget",
                EHumanoidIKTarget.Hips => "HipsTarget",
                EHumanoidIKTarget.LeftHand => "LeftHandTarget",
                EHumanoidIKTarget.RightHand => "RightHandTarget",
                EHumanoidIKTarget.LeftFoot => "LeftFootTarget",
                EHumanoidIKTarget.RightFoot => "RightFootTarget",
                EHumanoidIKTarget.LeftElbow => "LeftElbowTarget",
                EHumanoidIKTarget.RightElbow => "RightElbowTarget",
                EHumanoidIKTarget.LeftKnee => "LeftKneeTarget",
                EHumanoidIKTarget.RightKnee => "RightKneeTarget",
                EHumanoidIKTarget.Chest => "ChestTarget",
                _ => "IKTarget",
            };

        private string ResolveNeutralPoseBoneSettingKey(string boneName)
        {
            if (ResolveNeutralPoseCanonicalNode(boneName) is not null)
                return boneName;

            SceneNode? resolvedNode = SceneNode?.FindDescendantByName(boneName, StringComparison.InvariantCultureIgnoreCase);
            return ResolveNeutralPoseCanonicalName(resolvedNode) ?? boneName;
        }

        private string? ResolveNeutralPoseCanonicalName(SceneNode? node)
        {
            if (node is null)
                return null;

            return ReferenceEquals(Hips.Node, node) ? "Hips" :
                ReferenceEquals(Spine.Node, node) ? "Spine" :
                ReferenceEquals(Chest.Node, node) ? "Chest" :
                ReferenceEquals(UpperChest.Node, node) ? "UpperChest" :
                ReferenceEquals(Neck.Node, node) ? "Neck" :
                ReferenceEquals(Head.Node, node) ? "Head" :
                ReferenceEquals(Jaw.Node, node) ? "Jaw" :
                ReferenceEquals(Left.Eye.Node, node) ? "LeftEye" :
                ReferenceEquals(Right.Eye.Node, node) ? "RightEye" :
                ReferenceEquals(Left.Shoulder.Node, node) ? "LeftShoulder" :
                ReferenceEquals(Right.Shoulder.Node, node) ? "RightShoulder" :
                ReferenceEquals(Left.Arm.Node, node) ? "LeftUpperArm" :
                ReferenceEquals(Right.Arm.Node, node) ? "RightUpperArm" :
                ReferenceEquals(Left.Elbow.Node, node) ? "LeftLowerArm" :
                ReferenceEquals(Right.Elbow.Node, node) ? "RightLowerArm" :
                ReferenceEquals(Left.Wrist.Node, node) ? "LeftHand" :
                ReferenceEquals(Right.Wrist.Node, node) ? "RightHand" :
                ReferenceEquals(Left.Leg.Node, node) ? "LeftUpperLeg" :
                ReferenceEquals(Right.Leg.Node, node) ? "RightUpperLeg" :
                ReferenceEquals(Left.Knee.Node, node) ? "LeftLowerLeg" :
                ReferenceEquals(Right.Knee.Node, node) ? "RightLowerLeg" :
                ReferenceEquals(Left.Foot.Node, node) ? "LeftFoot" :
                ReferenceEquals(Right.Foot.Node, node) ? "RightFoot" :
                ReferenceEquals(Left.Toes.Node, node) ? "LeftToes" :
                ReferenceEquals(Right.Toes.Node, node) ? "RightToes" :
                ReferenceEquals(Left.Hand.Thumb.Proximal.Node, node) ? "LeftThumbProximal" :
                ReferenceEquals(Left.Hand.Thumb.Intermediate.Node, node) ? "LeftThumbIntermediate" :
                ReferenceEquals(Left.Hand.Thumb.Distal.Node, node) ? "LeftThumbDistal" :
                ReferenceEquals(Right.Hand.Thumb.Proximal.Node, node) ? "RightThumbProximal" :
                ReferenceEquals(Right.Hand.Thumb.Intermediate.Node, node) ? "RightThumbIntermediate" :
                ReferenceEquals(Right.Hand.Thumb.Distal.Node, node) ? "RightThumbDistal" :
                ReferenceEquals(Left.Hand.Index.Proximal.Node, node) ? "LeftIndexProximal" :
                ReferenceEquals(Left.Hand.Index.Intermediate.Node, node) ? "LeftIndexIntermediate" :
                ReferenceEquals(Left.Hand.Index.Distal.Node, node) ? "LeftIndexDistal" :
                ReferenceEquals(Right.Hand.Index.Proximal.Node, node) ? "RightIndexProximal" :
                ReferenceEquals(Right.Hand.Index.Intermediate.Node, node) ? "RightIndexIntermediate" :
                ReferenceEquals(Right.Hand.Index.Distal.Node, node) ? "RightIndexDistal" :
                ReferenceEquals(Left.Hand.Middle.Proximal.Node, node) ? "LeftMiddleProximal" :
                ReferenceEquals(Left.Hand.Middle.Intermediate.Node, node) ? "LeftMiddleIntermediate" :
                ReferenceEquals(Left.Hand.Middle.Distal.Node, node) ? "LeftMiddleDistal" :
                ReferenceEquals(Right.Hand.Middle.Proximal.Node, node) ? "RightMiddleProximal" :
                ReferenceEquals(Right.Hand.Middle.Intermediate.Node, node) ? "RightMiddleIntermediate" :
                ReferenceEquals(Right.Hand.Middle.Distal.Node, node) ? "RightMiddleDistal" :
                ReferenceEquals(Left.Hand.Ring.Proximal.Node, node) ? "LeftRingProximal" :
                ReferenceEquals(Left.Hand.Ring.Intermediate.Node, node) ? "LeftRingIntermediate" :
                ReferenceEquals(Left.Hand.Ring.Distal.Node, node) ? "LeftRingDistal" :
                ReferenceEquals(Right.Hand.Ring.Proximal.Node, node) ? "RightRingProximal" :
                ReferenceEquals(Right.Hand.Ring.Intermediate.Node, node) ? "RightRingIntermediate" :
                ReferenceEquals(Right.Hand.Ring.Distal.Node, node) ? "RightRingDistal" :
                ReferenceEquals(Left.Hand.Pinky.Proximal.Node, node) ? "LeftLittleProximal" :
                ReferenceEquals(Left.Hand.Pinky.Intermediate.Node, node) ? "LeftLittleIntermediate" :
                ReferenceEquals(Left.Hand.Pinky.Distal.Node, node) ? "LeftLittleDistal" :
                ReferenceEquals(Right.Hand.Pinky.Proximal.Node, node) ? "RightLittleProximal" :
                ReferenceEquals(Right.Hand.Pinky.Intermediate.Node, node) ? "RightLittleIntermediate" :
                ReferenceEquals(Right.Hand.Pinky.Distal.Node, node) ? "RightLittleDistal" :
                null;
        }

        private SceneNode? ResolveNeutralPoseMappedNodeByStoredKey(string boneName)
            => ResolveNeutralPoseCanonicalNode(boneName)
            ?? SceneNode?.FindDescendantByName(boneName, StringComparison.InvariantCultureIgnoreCase);

        private SceneNode? ResolveNeutralPoseCanonicalNode(string boneName)
        {
            return boneName switch
            {
                "Hips" => Hips.Node,
                "Spine" => Spine.Node,
                "Chest" => Chest.Node,
                "UpperChest" => UpperChest.Node,
                "Neck" => Neck.Node,
                "Head" => Head.Node,
                "Jaw" => Jaw.Node,
                "LeftEye" => Left.Eye.Node,
                "RightEye" => Right.Eye.Node,
                "LeftShoulder" => Left.Shoulder.Node,
                "RightShoulder" => Right.Shoulder.Node,
                "LeftUpperArm" => Left.Arm.Node,
                "RightUpperArm" => Right.Arm.Node,
                "LeftLowerArm" => Left.Elbow.Node,
                "RightLowerArm" => Right.Elbow.Node,
                "LeftHand" => Left.Wrist.Node,
                "RightHand" => Right.Wrist.Node,
                "LeftUpperLeg" => Left.Leg.Node,
                "RightUpperLeg" => Right.Leg.Node,
                "LeftLowerLeg" => Left.Knee.Node,
                "RightLowerLeg" => Right.Knee.Node,
                "LeftFoot" => Left.Foot.Node,
                "RightFoot" => Right.Foot.Node,
                "LeftToes" => Left.Toes.Node,
                "RightToes" => Right.Toes.Node,
                "LeftThumbProximal" => Left.Hand.Thumb.Proximal.Node,
                "LeftThumbIntermediate" => Left.Hand.Thumb.Intermediate.Node,
                "LeftThumbDistal" => Left.Hand.Thumb.Distal.Node,
                "RightThumbProximal" => Right.Hand.Thumb.Proximal.Node,
                "RightThumbIntermediate" => Right.Hand.Thumb.Intermediate.Node,
                "RightThumbDistal" => Right.Hand.Thumb.Distal.Node,
                "LeftIndexProximal" => Left.Hand.Index.Proximal.Node,
                "LeftIndexIntermediate" => Left.Hand.Index.Intermediate.Node,
                "LeftIndexDistal" => Left.Hand.Index.Distal.Node,
                "RightIndexProximal" => Right.Hand.Index.Proximal.Node,
                "RightIndexIntermediate" => Right.Hand.Index.Intermediate.Node,
                "RightIndexDistal" => Right.Hand.Index.Distal.Node,
                "LeftMiddleProximal" => Left.Hand.Middle.Proximal.Node,
                "LeftMiddleIntermediate" => Left.Hand.Middle.Intermediate.Node,
                "LeftMiddleDistal" => Left.Hand.Middle.Distal.Node,
                "RightMiddleProximal" => Right.Hand.Middle.Proximal.Node,
                "RightMiddleIntermediate" => Right.Hand.Middle.Intermediate.Node,
                "RightMiddleDistal" => Right.Hand.Middle.Distal.Node,
                "LeftRingProximal" => Left.Hand.Ring.Proximal.Node,
                "LeftRingIntermediate" => Left.Hand.Ring.Intermediate.Node,
                "LeftRingDistal" => Left.Hand.Ring.Distal.Node,
                "RightRingProximal" => Right.Hand.Ring.Proximal.Node,
                "RightRingIntermediate" => Right.Hand.Ring.Intermediate.Node,
                "RightRingDistal" => Right.Hand.Ring.Distal.Node,
                "LeftLittleProximal" => Left.Hand.Pinky.Proximal.Node,
                "LeftLittleIntermediate" => Left.Hand.Pinky.Intermediate.Node,
                "LeftLittleDistal" => Left.Hand.Pinky.Distal.Node,
                "RightLittleProximal" => Right.Hand.Pinky.Proximal.Node,
                "RightLittleIntermediate" => Right.Hand.Pinky.Intermediate.Node,
                "RightLittleDistal" => Right.Hand.Pinky.Distal.Node,
                _ => null,
            };
        }

        /// <summary>
        /// Sets the position and rotation of the root node of the model.
        /// </summary>
        /// <param name="boneName"></param>
        /// <param name="position"></param>
        /// <param name="rotation"></param>
        /// <param name="worldSpace"></param>
        /// <param name="forceConvertTransform"></param>
        public void SetRootPositionAndRotation(Vector3 position, Quaternion rotation, bool worldSpace, bool forceConvertTransform = false)
        {
            SceneNode bone = SceneNode;

            var tfm = bone.GetTransformAs<Transform>(forceConvertTransform);
            if (tfm is null)
                return;

            if (worldSpace)
            {
                tfm.SetWorldTranslation(position);
                tfm.SetWorldRotation(rotation);
            }
            else
            {
                tfm.Translation = position;
                tfm.Rotation = rotation;
            }
        }

        /// <summary>
        /// Sets the position and rotation of a bone in the model.
        /// </summary>
        /// <param name="boneName"></param>
        /// <param name="position"></param>
        /// <param name="rotation"></param>
        /// <param name="worldSpacePosition"></param>
        /// <param name="forceConvertTransform"></param>
        public void SetBonePositionAndRotation(string boneName, Vector3 position, Quaternion rotation, bool worldSpacePosition, bool worldSpaceRotation, bool forceConvertTransform = false)
        {
            SceneNode? bone = SceneNode.FindDescendantByName(boneName, StringComparison.InvariantCultureIgnoreCase);
            if (bone is null)
                return;

            var tfm = bone.GetTransformAs<Transform>(forceConvertTransform);
            if (tfm is null)
                return;

            if (worldSpacePosition)
                tfm.SetWorldTranslation(position);
            else
                tfm.Translation = position;

            if (worldSpaceRotation)
                tfm.SetWorldRotation(rotation);
            else
                tfm.Rotation = rotation;
        }

        /// <summary>
        /// Sets the value of a blendshape on all meshes in the model.
        /// </summary>
        /// <param name="blendshapeName"></param>
        /// <param name="weight"></param>
        public void SetBlendshapeValue(string blendshapeName, float weight, bool normalizedWeight = true)
        {
            //For all model components, find meshes with a matching blendshape name and set the value
            void SetWeight(ModelComponent comp)
            {
                if (normalizedWeight)
                    comp.SetBlendShapeWeightNormalized(blendshapeName, weight);
                else
                    comp.SetBlendShapeWeight(blendshapeName, weight);

            }
            SceneNode.IterateComponents<ModelComponent>(SetWeight, true);
        }

        public void SetEyesLookat(Vector3 worldTargetPosition)
        {
            SetEyeLookat(worldTargetPosition, Left);
            SetEyeLookat(worldTargetPosition, Right);
        }
        public void SetEyeLookat(Vector3 worldTargetPosition, bool leftEye)
            => SetEyeLookat(worldTargetPosition, leftEye ? Left : Right);

        private static void SetEyeLookat(Vector3 worldTargetPosition, BodySide side)
        {
            var tfm = side.Eye.Node?.GetTransformAs<Transform>(true)!;
            Matrix4x4 leftLookat = Matrix4x4.CreateLookAt(tfm.WorldTranslation, worldTargetPosition, Globals.Up);
            tfm.SetWorldRotation(Quaternion.CreateFromRotationMatrix(leftLookat));
        }

        public Transform? GetBoneByName(string name)
            => SceneNode.FindDescendantByName(name, StringComparison.InvariantCulture)?.GetTransformAs<Transform>(true);
    }
}
