using System.Numerics;
using XREngine.Components;
using XREngine.Components.Scene.Mesh;
using XREngine.Data;
using XREngine.Data.Colors;
using XREngine.Data.Core;
using XREngine.Data.Rendering;
using XREngine.Animation.IK;
using XREngine.Rendering.Info;
using XREngine.Rendering.Models;
using XREngine.Scene;
using XREngine.Scene.Transforms;
using BoneChainItem = XREngine.Components.Animation.InverseKinematics.BoneChainItem;
using BoneIKConstraints = XREngine.Components.Animation.InverseKinematics.BoneIKConstraints;

namespace XREngine.Components.Animation
{
    [XRComponentEditor("XREngine.Editor.ComponentEditors.HumanoidComponentEditor")]
    public class HumanoidComponent : XRComponent, IRenderable
    {
        // Humanoid (muscle) curve state. Values are expected to be normalized in [-1, 1].
        // We store raw values and apply a full pose once per frame.
        private readonly Dictionary<EHumanoidValue, float> _muscleValues = [];
        private readonly object _muscleValuesLock = new();

        protected internal override void OnComponentActivated()
        {
            base.OnComponentActivated();

            // Apply muscle-driven pose after animation evaluation.
            RegisterTick(ETickGroup.Normal, ETickOrder.Scene, ApplyMusclePose);
        }

        protected internal override void OnComponentDeactivated()
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

        public void SetValue(EHumanoidValue value, float amount)
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

                        // LH→RH: negate yaw (Y) and pitch (X) to convert from Unity's left-handed convention.
                        Quaternion q = Quaternion.CreateFromYawPitchRoll(-yaw * degToRad, -pitch * degToRad, 0.0f);
                        Left.Eye.Node?.GetTransformAs<Transform>(true)?.SetBindRelativeRotation(q);
                        break;
                    }
                case EHumanoidValue.LeftEyeInOut:
                    {
                        const float degToRad = MathF.PI / 180.0f;
                        float yaw = Interp.Lerp(Settings.LeftEyeInOutRange.X, Settings.LeftEyeInOutRange.Y, t);
                        float pitch = Settings.GetValue(EHumanoidValue.LeftEyeDownUp);
                        Settings.SetValue(EHumanoidValue.LeftEyeInOut, yaw);

                        Quaternion q = Quaternion.CreateFromYawPitchRoll(-yaw * degToRad, -pitch * degToRad, 0.0f);
                        Left.Eye.Node?.GetTransformAs<Transform>(true)?.SetBindRelativeRotation(q);
                        break;
                    }
                case EHumanoidValue.RightEyeDownUp:
                    {
                        const float degToRad = MathF.PI / 180.0f;
                        float yaw = Settings.GetValue(EHumanoidValue.RightEyeInOut);
                        float pitch = Interp.Lerp(Settings.RightEyeDownUpRange.X, Settings.RightEyeDownUpRange.Y, t);
                        Settings.SetValue(EHumanoidValue.RightEyeDownUp, pitch);

                        Quaternion q = Quaternion.CreateFromYawPitchRoll(-yaw * degToRad, -pitch * degToRad, 0.0f);
                        Right.Eye.Node?.GetTransformAs<Transform>(true)?.SetBindRelativeRotation(q);
                        break;
                    }
                case EHumanoidValue.RightEyeInOut:
                    {
                        const float degToRad = MathF.PI / 180.0f;
                        float yaw = Interp.Lerp(Settings.RightEyeInOutRange.X, Settings.RightEyeInOutRange.Y, t);
                        float pitch = Settings.GetValue(EHumanoidValue.RightEyeDownUp);
                        Settings.SetValue(EHumanoidValue.RightEyeInOut, yaw);

                        Quaternion q = Quaternion.CreateFromYawPitchRoll(-yaw * degToRad, -pitch * degToRad, 0.0f);
                        Right.Eye.Node?.GetTransformAs<Transform>(true)?.SetBindRelativeRotation(q);
                        break;
                    }
                default:
                    // Store muscle values; actual application happens once per frame in ApplyMusclePose().
                    lock (_muscleValuesLock)
                    {
                        _muscleValues[value] = amount;
                    }
                    break;
            }
        }

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

        public void ApplyCurrentMusclePose()
            => ApplyMusclePose();

        private void ApplyMusclePose()
        {
            // Skip when no muscle values have been set — avoids overwriting
            // animation-driven bone rotations with bind-pose identity.
            lock (_muscleValuesLock)
            {
                if (_muscleValues.Count == 0)
                    return;
            }

            EnsureBoneMapping();

            // Log first-playback diagnostic (once).
            LogFirstPlaybackDiagnostic();

            // Torso
            ApplyBindRelativeEulerDegrees(
                Spine.Node,
                yawDeg: MapMuscleToDeg(EHumanoidValue.SpineTwistLeftRight, GetMuscleValue(EHumanoidValue.SpineTwistLeftRight)),
                pitchDeg: MapMuscleToDeg(EHumanoidValue.SpineFrontBack, GetMuscleValue(EHumanoidValue.SpineFrontBack)),
                rollDeg: MapMuscleToDeg(EHumanoidValue.SpineLeftRight, GetMuscleValue(EHumanoidValue.SpineLeftRight)),
                axisMapping: GetBoneAxisMapping(Spine.Node));

            ApplyBindRelativeEulerDegrees(
                Chest.Node,
                yawDeg: MapMuscleToDeg(EHumanoidValue.ChestTwistLeftRight, GetMuscleValue(EHumanoidValue.ChestTwistLeftRight))
                    + (UpperChest.Node is null ? MapMuscleToDeg(EHumanoidValue.UpperChestTwistLeftRight, GetMuscleValue(EHumanoidValue.UpperChestTwistLeftRight)) : 0.0f),
                pitchDeg: MapMuscleToDeg(EHumanoidValue.ChestFrontBack, GetMuscleValue(EHumanoidValue.ChestFrontBack))
                      + (UpperChest.Node is null ? MapMuscleToDeg(EHumanoidValue.UpperChestFrontBack, GetMuscleValue(EHumanoidValue.UpperChestFrontBack)) : 0.0f),
                rollDeg: MapMuscleToDeg(EHumanoidValue.ChestLeftRight, GetMuscleValue(EHumanoidValue.ChestLeftRight))
                     + (UpperChest.Node is null ? MapMuscleToDeg(EHumanoidValue.UpperChestLeftRight, GetMuscleValue(EHumanoidValue.UpperChestLeftRight)) : 0.0f),
                axisMapping: GetBoneAxisMapping(Chest.Node));

            // UpperChest — only applied when a separate UpperChest bone exists.
            if (UpperChest.Node is not null)
            {
                ApplyBindRelativeEulerDegrees(
                    UpperChest.Node,
                    yawDeg: MapMuscleToDeg(EHumanoidValue.UpperChestTwistLeftRight, GetMuscleValue(EHumanoidValue.UpperChestTwistLeftRight)),
                    pitchDeg: MapMuscleToDeg(EHumanoidValue.UpperChestFrontBack, GetMuscleValue(EHumanoidValue.UpperChestFrontBack)),
                    rollDeg: MapMuscleToDeg(EHumanoidValue.UpperChestLeftRight, GetMuscleValue(EHumanoidValue.UpperChestLeftRight)),
                    axisMapping: GetBoneAxisMapping(UpperChest.Node));
            }

            // Neck / Head
            ApplyBindRelativeEulerDegrees(
                Neck.Node,
                yawDeg: MapMuscleToDeg(EHumanoidValue.NeckTurnLeftRight, GetMuscleValue(EHumanoidValue.NeckTurnLeftRight)),
                pitchDeg: -MapMuscleToDeg(EHumanoidValue.NeckNodDownUp, GetMuscleValue(EHumanoidValue.NeckNodDownUp)),
                rollDeg: MapMuscleToDeg(EHumanoidValue.NeckTiltLeftRight, GetMuscleValue(EHumanoidValue.NeckTiltLeftRight)),
                axisMapping: GetBoneAxisMapping(Neck.Node));

            ApplyBindRelativeEulerDegrees(
                Head.Node,
                yawDeg: MapMuscleToDeg(EHumanoidValue.HeadTurnLeftRight, GetMuscleValue(EHumanoidValue.HeadTurnLeftRight)),
                pitchDeg: -MapMuscleToDeg(EHumanoidValue.HeadNodDownUp, GetMuscleValue(EHumanoidValue.HeadNodDownUp)),
                rollDeg: MapMuscleToDeg(EHumanoidValue.HeadTiltLeftRight, GetMuscleValue(EHumanoidValue.HeadTiltLeftRight)),
                axisMapping: GetBoneAxisMapping(Head.Node));

            // Jaw
            ApplyBindRelativeEulerDegrees(
                Jaw.Node,
                yawDeg: MapMuscleToDeg(EHumanoidValue.JawLeftRight, GetMuscleValue(EHumanoidValue.JawLeftRight)),
                pitchDeg: MapMuscleToDeg(EHumanoidValue.JawClose, GetMuscleValue(EHumanoidValue.JawClose)),
                rollDeg: 0.0f);

            // Arms / Legs / Hands / Feet
            GetBindBodyBasis(out Vector3 bodyLeft, out Vector3 bodyUp, out Vector3 bodyForward);
            ApplyLimbMuscles(isLeft: true, bodyLeft, bodyUp, bodyForward);
            ApplyLimbMuscles(isLeft: false, bodyLeft, bodyUp, bodyForward);

            // Fingers
            ApplyFingerMuscles(isLeft: true);
            ApplyFingerMuscles(isLeft: false);

            // One-time diagnostic snapshot of muscle values → degree values.
            LogMusclePoseSnapshot();
        }

        private bool _boneMappingComplete;
        private float _nextRebindTime;
        private bool? _lastBoneMappingComplete;

        private void EnsureBoneMapping()
        {
            if (_boneMappingComplete)
                return;

            float now = Engine.ElapsedTime;
            if (now < _nextRebindTime)
                return;

            _nextRebindTime = now + 0.5f;
            SetFromNode();
        }

        private void ApplyLimbMuscles(bool isLeft, Vector3 bodyLeft, Vector3 bodyUp, Vector3 bodyForward)
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
            float forearmStretchMuscle = GetMuscleValue(forearmStretch);
            float lowerLegStretchMuscle = GetMuscleValue(lowerLegStretch);
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

            ApplyBodyBasisLimbRotation(
                side.Shoulder.Node, DebugShoulderSigns,
                yawDeg: 0.0f,
                pitchDeg: MapMuscleToDeg(shoulderDownUp, GetMuscleValue(shoulderDownUp)),
                rollDeg: MapMuscleToDeg(shoulderFrontBack, GetMuscleValue(shoulderFrontBack)),
                twistAxisWorld: shoulderTwistAxisWorld,
                pitchAxisWorld: armPitchAxisWorld,
                rollAxisWorld: armRollAxisWorld);

            ApplyBodyBasisLimbRotation(
                side.Arm.Node, DebugArmSigns,
                yawDeg: MapMuscleToDeg(armTwist, GetMuscleValue(armTwist)),
                pitchDeg: MapMuscleToDeg(armDownUp, GetMuscleValue(armDownUp)),
                rollDeg: MapMuscleToDeg(armFrontBack, GetMuscleValue(armFrontBack)),
                twistAxisWorld: armTwistAxisWorld,
                pitchAxisWorld: armPitchAxisWorld,
                rollAxisWorld: armRollAxisWorld);

            ApplyBodyBasisLimbRotation(
                side.Elbow.Node, DebugForearmSigns,
                yawDeg: MapMuscleToDeg(forearmTwist, GetMuscleValue(forearmTwist)),
                pitchDeg: MapMuscleToDeg(forearmStretch, forearmStretchMuscle),
                rollDeg: 0.0f,
                twistAxisWorld: forearmTwistAxisWorld,
                pitchAxisWorld: armPitchAxisWorld,
                rollAxisWorld: armRollAxisWorld);

            ApplyBodyBasisLimbRotation(
                side.Wrist.Node, DebugWristSigns,
                yawDeg: 0.0f,
                pitchDeg: MapMuscleToDeg(handDownUp, GetMuscleValue(handDownUp)),
                rollDeg: MapMuscleToDeg(handInOut, GetMuscleValue(handInOut)),
                twistAxisWorld: forearmTwistAxisWorld,
                pitchAxisWorld: armPitchAxisWorld,
                rollAxisWorld: armRollAxisWorld);

            ApplyBodyBasisLimbRotation(
                side.Leg.Node, DebugUpperLegSigns,
                yawDeg: MapMuscleToDeg(upperLegTwist, GetMuscleValue(upperLegTwist)),
                pitchDeg: MapMuscleToDeg(upperLegFrontBack, GetMuscleValue(upperLegFrontBack)),
                rollDeg: MapMuscleToDeg(upperLegInOut, GetMuscleValue(upperLegInOut)),
                twistAxisWorld: upperLegTwistAxisWorld,
                pitchAxisWorld: legPitchAxisWorld,
                rollAxisWorld: legRollAxisWorld);

            ApplyBodyBasisLimbRotation(
                side.Knee.Node, DebugKneeSigns,
                yawDeg: MapMuscleToDeg(lowerLegTwist, GetMuscleValue(lowerLegTwist)),
                pitchDeg: lowerLegPitchDeg,
                rollDeg: 0.0f,
                twistAxisWorld: lowerLegTwistAxisWorld,
                pitchAxisWorld: legPitchAxisWorld,
                rollAxisWorld: legRollAxisWorld);

            ApplyBodyBasisLimbRotation(
                side.Foot.Node, DebugFootSigns,
                yawDeg: MapMuscleToDeg(footTwist, GetMuscleValue(footTwist)),
                pitchDeg: MapMuscleToDeg(footUpDown, GetMuscleValue(footUpDown)),
                rollDeg: 0.0f,
                twistAxisWorld: footTwistAxisWorld,
                pitchAxisWorld: legPitchAxisWorld,
                rollAxisWorld: legRollAxisWorld);

            ApplyBodyBasisLimbRotation(
                side.Toes.Node, DebugToesSigns,
                yawDeg: 0.0f,
                pitchDeg: MapMuscleToDeg(toesUpDown, GetMuscleValue(toesUpDown)),
                rollDeg: 0.0f,
                twistAxisWorld: footTwistAxisWorld,
                pitchAxisWorld: legPitchAxisWorld,
                rollAxisWorld: legRollAxisWorld);
        }

        private void ApplyFingerMuscles(bool isLeft)
        {
            var side = isLeft ? Left : Right;

            ApplyFinger(isLeft, side.Hand.Thumb,
                spread: isLeft ? EHumanoidValue.LeftHandThumbSpread : EHumanoidValue.RightHandThumbSpread,
                prox: isLeft ? EHumanoidValue.LeftHandThumb1Stretched : EHumanoidValue.RightHandThumb1Stretched,
                mid: isLeft ? EHumanoidValue.LeftHandThumb2Stretched : EHumanoidValue.RightHandThumb2Stretched,
                dist: isLeft ? EHumanoidValue.LeftHandThumb3Stretched : EHumanoidValue.RightHandThumb3Stretched);

            ApplyFinger(isLeft, side.Hand.Index,
                spread: isLeft ? EHumanoidValue.LeftHandIndexSpread : EHumanoidValue.RightHandIndexSpread,
                prox: isLeft ? EHumanoidValue.LeftHandIndex1Stretched : EHumanoidValue.RightHandIndex1Stretched,
                mid: isLeft ? EHumanoidValue.LeftHandIndex2Stretched : EHumanoidValue.RightHandIndex2Stretched,
                dist: isLeft ? EHumanoidValue.LeftHandIndex3Stretched : EHumanoidValue.RightHandIndex3Stretched);

            ApplyFinger(isLeft, side.Hand.Middle,
                spread: isLeft ? EHumanoidValue.LeftHandMiddleSpread : EHumanoidValue.RightHandMiddleSpread,
                prox: isLeft ? EHumanoidValue.LeftHandMiddle1Stretched : EHumanoidValue.RightHandMiddle1Stretched,
                mid: isLeft ? EHumanoidValue.LeftHandMiddle2Stretched : EHumanoidValue.RightHandMiddle2Stretched,
                dist: isLeft ? EHumanoidValue.LeftHandMiddle3Stretched : EHumanoidValue.RightHandMiddle3Stretched);

            ApplyFinger(isLeft, side.Hand.Ring,
                spread: isLeft ? EHumanoidValue.LeftHandRingSpread : EHumanoidValue.RightHandRingSpread,
                prox: isLeft ? EHumanoidValue.LeftHandRing1Stretched : EHumanoidValue.RightHandRing1Stretched,
                mid: isLeft ? EHumanoidValue.LeftHandRing2Stretched : EHumanoidValue.RightHandRing2Stretched,
                dist: isLeft ? EHumanoidValue.LeftHandRing3Stretched : EHumanoidValue.RightHandRing3Stretched);

            ApplyFinger(isLeft, side.Hand.Pinky,
                spread: isLeft ? EHumanoidValue.LeftHandLittleSpread : EHumanoidValue.RightHandLittleSpread,
                prox: isLeft ? EHumanoidValue.LeftHandLittle1Stretched : EHumanoidValue.RightHandLittle1Stretched,
                mid: isLeft ? EHumanoidValue.LeftHandLittle2Stretched : EHumanoidValue.RightHandLittle2Stretched,
                dist: isLeft ? EHumanoidValue.LeftHandLittle3Stretched : EHumanoidValue.RightHandLittle3Stretched);
        }

        private void ApplyFinger(
            bool isLeft,
            BodySide.Fingers.Finger finger,
            EHumanoidValue spread, EHumanoidValue prox, EHumanoidValue mid, EHumanoidValue dist)
        {
            float sideMirror = isLeft ? 1.0f : -1.0f;
            // Stretched channels map to bending on each phalanx.
            ApplyBindRelativeEulerDegrees(
                finger.Proximal.Node,
                yawDeg: MapMuscleToDeg(spread, GetMuscleValue(spread)) * sideMirror,
                pitchDeg: MapMuscleToDeg(prox, GetMuscleValue(prox)),
                rollDeg: 0.0f,
                axisMapping: GetBoneAxisMapping(finger.Proximal.Node));

            ApplyBindRelativeEulerDegrees(
                finger.Intermediate.Node,
                yawDeg: 0.0f,
                pitchDeg: MapMuscleToDeg(mid, GetMuscleValue(mid)),
                rollDeg: 0.0f,
                axisMapping: GetBoneAxisMapping(finger.Intermediate.Node));

            ApplyBindRelativeEulerDegrees(
                finger.Distal.Node,
                yawDeg: 0.0f,
                pitchDeg: MapMuscleToDeg(dist, GetMuscleValue(dist)),
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

        private static void ApplyBodyBasisLimbRotation(
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

        private float MapMuscleToDeg(EHumanoidValue value, float muscle)
        {
            // Unity humanoid muscle mapping is piecewise-linear through zero:
            //   muscle = -1  → minDeg
            //   muscle =  0  → 0° (bind pose, no rotation)
            //   muscle = +1  → maxDeg
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

        private static void ApplyBindRelativeEulerDegrees(SceneNode? node, float yawDeg, float pitchDeg, float rollDeg)
        {
            ApplyBindRelativeEulerDegrees(node, yawDeg, pitchDeg, rollDeg, null);
        }

        private static void ApplyBindRelativeEulerDegrees(SceneNode? node, float yawDeg, float pitchDeg, float rollDeg, BoneAxisMapping? axisMapping)
        {
            if (node?.Transform is null)
                return;

            const float degToRad = MathF.PI / 180.0f;

            // LH→RH conversion: Unity uses a left-handed coordinate system (Z = forward).
            // Our engine uses right-handed OpenGL (Z = toward viewer).
            // For the Z-flip transform M = diag(1,1,-1), a rotation R(axis, θ) in LH becomes
            // R((-ax,-ay,az), θ) in RH. This means:
            //   Rotation around X axis → negate angle
            //   Rotation around Y axis → negate angle
            //   Rotation around Z axis → angle stays

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

            node.GetTransformAs<Transform>(true)?.SetBindRelativeRotation(q);
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
        private static void ApplyWithDebugOverrides(
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

        private static void ApplyBindRelativeSwingTwistWorldAxes(
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

            // LH→RH conversion: negate each muscle angle unless overridden per-axis.
            float yawSign = skipBlanketNegateYaw ? 1.0f : -1.0f;
            float pitchSign = skipBlanketNegatePitch ? 1.0f : -1.0f;
            float rollSign = skipBlanketNegateRoll ? 1.0f : -1.0f;
            Quaternion twist = Quaternion.CreateFromAxisAngle(twistLocal, yawSign * yawDeg * degToRad);
            Quaternion frontBack = Quaternion.CreateFromAxisAngle(frontBackLocal, pitchSign * pitchDeg * degToRad);
            Quaternion leftRight = Quaternion.CreateFromAxisAngle(leftRightLocal, rollSign * rollDeg * degToRad);

            // Unity ZXY Euler order: twist(Z) innermost, front-back(X), left-right(Y) outermost.
            Quaternion q = leftRight * frontBack * twist;
            node.GetTransformAs<Transform>(true)?.SetBindRelativeRotation(q);
        }

        protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
            => base.OnPropertyChanged(propName, prev, field);

        protected internal override void AddedToSceneNode(SceneNode sceneNode)
        {
            base.AddedToSceneNode(sceneNode);
            SetFromNode();
        }

        private bool _solveIK = false;
        public bool SolveIK
        {
            get => _solveIK;
            set => SetField(ref _solveIK, value);
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

            var hipToHeadChain = GetHipToHeadChain();
            if (hipToHeadChain is not null)
                for (int i = 0; i < hipToHeadChain.Length; i++)
                {
                    BoneChainItem? bone = hipToHeadChain[i];
                    BoneChainItem? nextBone = i + 1 < hipToHeadChain.Length ? hipToHeadChain[i + 1] : null;
                    Engine.Rendering.Debug.RenderPoint(bone.WorldPosSolve, ColorF4.Red);
                    if (nextBone is not null)
                        Engine.Rendering.Debug.RenderLine(bone.WorldPosSolve, nextBone.WorldPosSolve, ColorF4.Red);
                }

            var leftLegToAnkleChain = GetLeftLegToAnkleChain();
            if (leftLegToAnkleChain is not null)
                for (int i = 0; i < leftLegToAnkleChain.Length; i++)
                {
                    BoneChainItem? bone = leftLegToAnkleChain[i];
                    BoneChainItem? nextBone = i + 1 < leftLegToAnkleChain.Length ? leftLegToAnkleChain[i + 1] : null;
                    Engine.Rendering.Debug.RenderPoint(bone.WorldPosSolve, ColorF4.Red);
                    if (nextBone is not null)
                        Engine.Rendering.Debug.RenderLine(bone.WorldPosSolve, nextBone.WorldPosSolve, ColorF4.Red);
                }

            var rightLegToAnkleChain = GetRightLegToAnkleChain();
            if (rightLegToAnkleChain is not null)
                for (int i = 0; i < rightLegToAnkleChain.Length; i++)
                {
                    BoneChainItem? bone = rightLegToAnkleChain[i];
                    BoneChainItem? nextBone = i + 1 < rightLegToAnkleChain.Length ? rightLegToAnkleChain[i + 1] : null;
                    Engine.Rendering.Debug.RenderPoint(bone.WorldPosSolve, ColorF4.Red);
                    if (nextBone is not null)
                        Engine.Rendering.Debug.RenderLine(bone.WorldPosSolve, nextBone.WorldPosSolve, ColorF4.Red);
                }

            var leftShoulderToWristChain = GetLeftShoulderToWristChain();
            if (leftShoulderToWristChain is not null)
                for (int i = 0; i < leftShoulderToWristChain.Length; i++)
                {
                    BoneChainItem? bone = leftShoulderToWristChain[i];
                    BoneChainItem? nextBone = i + 1 < leftShoulderToWristChain.Length ? leftShoulderToWristChain[i + 1] : null;
                    Engine.Rendering.Debug.RenderPoint(bone.WorldPosSolve, ColorF4.Red);
                    if (nextBone is not null)
                        Engine.Rendering.Debug.RenderLine(bone.WorldPosSolve, nextBone.WorldPosSolve, ColorF4.Red);
                }

            var rightShoulderToWristChain = GetRightShoulderToWristChain();
            if (rightShoulderToWristChain is not null)
                for (int i = 0; i < rightShoulderToWristChain.Length; i++)
                {
                    BoneChainItem? bone = rightShoulderToWristChain[i];
                    BoneChainItem? nextBone = i + 1 < rightShoulderToWristChain.Length ? rightShoulderToWristChain[i + 1] : null;
                    Engine.Rendering.Debug.RenderPoint(bone.WorldPosSolve, ColorF4.Red);
                    if (nextBone is not null)
                        Engine.Rendering.Debug.RenderLine(bone.WorldPosSolve, nextBone.WorldPosSolve, ColorF4.Red);
                }
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
            Hips.ResetPose();
            Spine.ResetPose();
            Chest.ResetPose();
            UpperChest.ResetPose();
            Neck.ResetPose();
            Head.ResetPose();
            Jaw.ResetPose();
            EyesTarget.ResetPose();
            Left.ResetPose();
            Right.ResetPose();
        }

        public BoneChainItem[]? _hipToHeadChain = null;
        public BoneChainItem[]? _leftLegToAnkleChain = null;
        public BoneChainItem[]? _rightLegToAnkleChain = null;
        public BoneChainItem[]? _leftShoulderToWristChain = null;
        public BoneChainItem[]? _rightShoulderToWristChain = null;
        private bool _leftArmIKEnabled = true;
        private bool _rightArmIKEnabled = true;
        private bool _leftLegIKEnabled = true;
        private bool _rightLegIKEnabled = true;
        private bool _hipToHeadIKEnabled = true;
        private (TransformBase? tfm, Matrix4x4 offset) _headTarget = (null, Matrix4x4.Identity);
        private (TransformBase? tfm, Matrix4x4 offset) _hipsTarget = (null, Matrix4x4.Identity);
        private (TransformBase? tfm, Matrix4x4 offset) _leftHandTarget = (null, Matrix4x4.Identity);
        private (TransformBase? tfm, Matrix4x4 offset) _rightHandTarget = (null, Matrix4x4.Identity);
        private (TransformBase? tfm, Matrix4x4 offset) _leftFootTarget = (null, Matrix4x4.Identity);
        private (TransformBase? tfm, Matrix4x4 offset) _rightFootTarget = (null, Matrix4x4.Identity);
        private (TransformBase? tfm, Matrix4x4 offset) _leftElbowTarget = (null, Matrix4x4.Identity);
        private (TransformBase? tfm, Matrix4x4 offset) _rightElbowTarget = (null, Matrix4x4.Identity);
        private (TransformBase? tfm, Matrix4x4 offset) _leftKneeTarget = (null, Matrix4x4.Identity);
        private (TransformBase? tfm, Matrix4x4 offset) _rightKneeTarget = (null, Matrix4x4.Identity);
        private (TransformBase? tfm, Matrix4x4 offset) _chestTarget = (null, Matrix4x4.Identity);

        private static BoneChainItem[] Link(BoneDef[] bones)
            => bones.Any(bone => bone.Node is null) 
            ? [] 
            : [.. bones.Select(bone => new BoneChainItem(bone.Node!, bone.Constraints))];

        public bool LeftArmIKEnabled
        {
            get => _leftArmIKEnabled;
            set => SetField(ref _leftArmIKEnabled, value);
        }
        public bool RightArmIKEnabled
        {
            get => _rightArmIKEnabled;
            set => SetField(ref _rightArmIKEnabled, value);
        }
        public bool LeftLegIKEnabled
        {
            get => _leftLegIKEnabled;
            set => SetField(ref _leftLegIKEnabled, value);
        }
        public bool RightLegIKEnabled
        {
            get => _rightLegIKEnabled;
            set => SetField(ref _rightLegIKEnabled, value);
        }
        public bool HipToHeadIKEnabled
        {
            get => _hipToHeadIKEnabled;
            set => SetField(ref _hipToHeadIKEnabled, value);
        }

        public BoneChainItem[]? GetHipToHeadChain()
        {
            if (!_hipToHeadIKEnabled)
                return null;

            return _hipToHeadChain ??= UpperChest.Node is not null 
                ? Link([Hips, Spine, Chest, UpperChest, Neck, Head])
                : Link([Hips, Spine, Chest, Neck, Head]);
        }

        public BoneChainItem[]? GetLeftLegToAnkleChain()
        {
            if (!_leftLegIKEnabled)
                return null;

            return _leftLegToAnkleChain ??= Link([Left.Leg, Left.Knee, Left.Foot]);
        }

        public BoneChainItem[]? GetRightLegToAnkleChain()
        {
            if (!_rightLegIKEnabled)
                return null;

            return _rightLegToAnkleChain ??= Link([Right.Leg, Right.Knee, Right.Foot]);
        }

        public BoneChainItem[]? GetLeftShoulderToWristChain()
        {
            if (!_leftArmIKEnabled)
                return null;

            return _leftShoulderToWristChain ??= Link([Left.Shoulder, Left.Arm, Left.Elbow, Left.Wrist]);
        }

        public BoneChainItem[]? GetRightShoulderToWristChain()
        {
            if (!_rightArmIKEnabled)
                return null;

            return _rightShoulderToWristChain ??= Link([Right.Shoulder, Right.Arm, Right.Elbow, Right.Wrist]);
        }

        public static Matrix4x4 GetMatrixForTarget((TransformBase? tfm, Matrix4x4 offset) target)
            => target.offset * (target.tfm?.RenderMatrix ?? Matrix4x4.Identity);

        public (TransformBase? tfm, Matrix4x4 offset) HeadTarget
        {
            get => _headTarget;
            set => SetField(ref _headTarget, value);
        }
        public (TransformBase? tfm, Matrix4x4 offset) HipsTarget
        {
            get => _hipsTarget;
            set => SetField(ref _hipsTarget, value);
        }
        public (TransformBase? tfm, Matrix4x4 offset) LeftHandTarget
        {
            get => _leftHandTarget;
            set => SetField(ref _leftHandTarget, value);
        }
        public (TransformBase? tfm, Matrix4x4 offset) RightHandTarget
        {
            get => _rightHandTarget;
            set => SetField(ref _rightHandTarget, value);
        }
        public (TransformBase? tfm, Matrix4x4 offset) LeftFootTarget
        {
            get => _leftFootTarget;
            set => SetField(ref _leftFootTarget, value);
        }
        public (TransformBase? tfm, Matrix4x4 offset) RightFootTarget
        {
            get => _rightFootTarget;
            set => SetField(ref _rightFootTarget, value);
        }
        public (TransformBase? tfm, Matrix4x4 offset) LeftElbowTarget
        {
            get => _leftElbowTarget;
            set => SetField(ref _leftElbowTarget, value);
        }
        public (TransformBase? tfm, Matrix4x4 offset) RightElbowTarget
        {
            get => _rightElbowTarget;
            set => SetField(ref _rightElbowTarget, value);
        }
        public (TransformBase? tfm, Matrix4x4 offset) LeftKneeTarget
        {
            get => _leftKneeTarget;
            set => SetField(ref _leftKneeTarget, value);
        }
        public (TransformBase? tfm, Matrix4x4 offset) RightKneeTarget
        {
            get => _rightKneeTarget;
            set => SetField(ref _rightKneeTarget, value);
        }
        public (TransformBase? tfm, Matrix4x4 offset) ChestTarget
        {
            get => _chestTarget;
            set => SetField(ref _chestTarget, value);
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
                    (Left.Shoulder, BySideAwarePosition("Shoulder", isLeft: true, x => x.X < 0.0f)),
                    (Right.Shoulder, BySideAwarePosition("Shoulder", isLeft: false, x => x.X > 0.0f)),
                ]);

            // If UpperChest was found, shoulders/neck/head may be children of UpperChest rather than Chest.
            if (UpperChest.Node is not null)
                FindChildrenFor(UpperChest, [
                    (Neck, ByName("Neck")),
                    (Head, ByName("Head")),
                    (Left.Shoulder, BySideAwarePosition("Shoulder", isLeft: true, x => x.X < 0.0f)),
                    (Right.Shoulder, BySideAwarePosition("Shoulder", isLeft: false, x => x.X > 0.0f)),
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
                    (Left.Arm, ByNameContainsAll("Arm")),
                    (Left.Elbow, ByNameContainsAnyAndDoesNotContain(ElbowNameMatches, TwistBoneMismatch)),
                    (Left.Wrist, ByNameContainsAnyAndDoesNotContain(HandNameMatches, TwistBoneMismatch)),
                ]);

            if (Right.Shoulder.Node is not null)
            {
                FindChildrenFor(Right.Shoulder, [
                    (Right.Arm, ByNameContainsAll("Arm")),
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

            //Left.Knee.Constraints = KneeConstraints();
            //Right.Knee.Constraints = KneeConstraints();
            //Left.Elbow.Constraints = ElbowConstraints();
            //Right.Elbow.Constraints = ElbowConstraints();

            //Assign initial solve targets to current bone positions

            //Center
            HipsTarget = (Hips.Node?.Transform, Matrix4x4.Identity);
            ChestTarget = (Chest.Node?.Transform, Matrix4x4.Identity);
            HeadTarget = (Head.Node?.Transform, Matrix4x4.Identity);

            //Hands
            LeftHandTarget = (Left.Wrist.Node?.Transform, Matrix4x4.Identity);
            RightHandTarget = (Right.Wrist.Node?.Transform, Matrix4x4.Identity);

            //Feet
            LeftFootTarget = (Left.Foot.Node?.Transform, Matrix4x4.Identity);
            RightFootTarget = (Right.Foot.Node?.Transform, Matrix4x4.Identity);

            //Elbows
            LeftElbowTarget = (Left.Elbow.Node?.Transform, Matrix4x4.Identity);
            RightElbowTarget = (Right.Elbow.Node?.Transform, Matrix4x4.Identity);

            //Knees
            LeftKneeTarget = (Left.Knee.Node?.Transform, Matrix4x4.Identity);
            RightKneeTarget = (Right.Knee.Node?.Transform, Matrix4x4.Identity);

            // Log diagnostic information about bone mapping results
            LogBoneMappingDiagnostics();

            // ── Build avatar profile (replaces the old ComputeAutoAxisMappings) ──
            // This derives per-bone axis mappings with confidence scoring,
            // sets IsIKCalibrated based on overall confidence, and applies
            // conservative fallback when confidence is low.
            var profileResult = AvatarHumanoidProfileBuilder.BuildProfile(this);
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
        private void LogMusclePoseSnapshot()
        {
            if (_musclePoseLoggedOnce) return;
            _musclePoseLoggedOnce = true;

            void LogMuscle(string label, EHumanoidValue val)
            {
                Vector2 range = Settings.GetResolvedMuscleRotationDegRange(val);
                float raw = GetMuscleValue(val);
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
        ///   bodyLeft    ≈ ( 1, 0,  0)  (avatar left, camera right)
        ///   bodyUp      ≈ ( 0, 1,  0)
        ///   bodyForward ≈ ( 0, 0,  1)  (avatar facing camera)
        ///   armTwist    ≈ (±1, 0,  0)  (arm points sideways in T-pose)
        ///   legTwist    ≈ ( 0,-1,  0)  (leg points downward)
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
            Debug.Animation($"[BodyBasis]  bodyLeft         {V(bodyLeft)}   expected ≈ ( 1, 0, 0)");
            Debug.Animation($"[BodyBasis]  bodyUp           {V(bodyUp)}   expected ≈ ( 0, 1, 0)");
            Debug.Animation($"[BodyBasis]  bodyForward      {V(bodyForward)}   expected ≈ ( 0, 0, 1)");
            Debug.Animation($"[BodyBasis]  armTwistWorld    {V(armTwistAxisWorld)}   expected ≈ (±1, 0, 0)");
            Debug.Animation($"[BodyBasis]  legTwistWorld    {V(legTwistAxisWorld)}   expected ≈ ( 0,-1, 0)");
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

        private static BoneIKConstraints KneeConstraints()
        {
            return new BoneIKConstraints()
            {
                MaxPitch = 90.0f,
                MinPitch = -90.0f,
                MaxRoll = 0.0f,
                MinRoll = 0.0f,
                MaxYaw = 0.0f,
                MinYaw = 0.0f,
            };
        }

        private static BoneIKConstraints ElbowConstraints()
        {
            return new BoneIKConstraints()
            {
                MaxPitch = 90.0f,
                MinPitch = -90.0f,
                MaxRoll = 0.0f,
                MinRoll = 0.0f,
                MaxYaw = 90.0f,
                MinYaw = -90.0f,
            };
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
        private static readonly string[] FingerBoneMismatch = ["metacarp"];

        public void SetFootPositionX(float x, bool leftFoot)
        {
            if (leftFoot)
                LeftFootTarget = (null, Matrix4x4.CreateTranslation(new Vector3(x, LeftFootTarget.offset.Translation.Y, LeftFootTarget.offset.Translation.Z)));
            else
                RightFootTarget = (null, Matrix4x4.CreateTranslation(new Vector3(x, RightFootTarget.offset.Translation.Y, RightFootTarget.offset.Translation.Z)));
        }
        public void SetFootPositionY(float y, bool leftFoot)
        {
            if (leftFoot)
                LeftFootTarget = (null, Matrix4x4.CreateTranslation(new Vector3(LeftFootTarget.offset.Translation.X, y, LeftFootTarget.offset.Translation.Z)));
            else
                RightFootTarget = (null, Matrix4x4.CreateTranslation(new Vector3(RightFootTarget.offset.Translation.X, y, RightFootTarget.offset.Translation.Z)));
        }
        public void SetFootPositionZ(float z, bool leftFoot)
        {
            if (leftFoot)
                LeftFootTarget = (null, Matrix4x4.CreateTranslation(new Vector3(LeftFootTarget.offset.Translation.X, LeftFootTarget.offset.Translation.Y, z)));
            else
                RightFootTarget = (null, Matrix4x4.CreateTranslation(new Vector3(RightFootTarget.offset.Translation.X, RightFootTarget.offset.Translation.Y, z)));
        }
        public void SetFootPosition(Vector3 position, bool leftFoot)
        {
            if (leftFoot)
                LeftFootTarget = (null, Matrix4x4.CreateTranslation(position));
            else
                RightFootTarget = (null, Matrix4x4.CreateTranslation(position));
        }
        public void SetFootRotation(Quaternion rotation, bool leftFoot)
        {
            if (leftFoot)
                LeftFootTarget = (null, Matrix4x4.CreateFromQuaternion(rotation) * Matrix4x4.CreateTranslation(LeftFootTarget.offset.Translation));
            else
                RightFootTarget = (null, Matrix4x4.CreateFromQuaternion(rotation) * Matrix4x4.CreateTranslation(RightFootTarget.offset.Translation));
        }
        public void SetHandPosition(Vector3 position, bool leftHand)
        {
            if (leftHand)
                LeftHandTarget = (null, Matrix4x4.CreateTranslation(position));
            else
                RightHandTarget = (null, Matrix4x4.CreateTranslation(position));
        }
        public void SetHandRotation(Quaternion rotation, bool leftHand)
        {
            if (leftHand)
                LeftHandTarget = (null, Matrix4x4.CreateFromQuaternion(rotation) * Matrix4x4.CreateTranslation(LeftHandTarget.offset.Translation));
            else
                RightHandTarget = (null, Matrix4x4.CreateFromQuaternion(rotation) * Matrix4x4.CreateTranslation(RightHandTarget.offset.Translation));
        }
        public void SetFootPositionAndRotation(Vector3 position, Quaternion rotation, bool leftFoot)
        {
            if (leftFoot)
                LeftFootTarget = (null, Matrix4x4.CreateFromQuaternion(rotation) * Matrix4x4.CreateTranslation(position));
            else
                RightFootTarget = (null, Matrix4x4.CreateFromQuaternion(rotation) * Matrix4x4.CreateTranslation(position));
        }
        public void SetHandPositionAndRotation(Vector3 position, Quaternion rotation, bool leftHand)
        {
            if (leftHand)
                LeftHandTarget = (null, Matrix4x4.CreateFromQuaternion(rotation) * Matrix4x4.CreateTranslation(position));
            else
                RightHandTarget = (null, Matrix4x4.CreateFromQuaternion(rotation) * Matrix4x4.CreateTranslation(position));
        }

        // Legacy animation-driven IK gateways.
        // New Unity humanoid imports target HumanoidIKSolverComponent directly, but
        // these wrappers keep older imported clips functioning by forwarding to it.

        public void SetAnimatedFootPosition(Vector3 position, bool leftFoot)
        {
            EnsureAnimationIKSolver()?.SetAnimatedIKPosition(
                leftFoot ? ELimbEndEffector.LeftFoot : ELimbEndEffector.RightFoot,
                position);
        }

        public void SetAnimatedFootRotation(Quaternion rotation, bool leftFoot)
        {
            EnsureAnimationIKSolver()?.SetAnimatedIKRotation(
                leftFoot ? ELimbEndEffector.LeftFoot : ELimbEndEffector.RightFoot,
                rotation);
        }

        public void SetAnimatedHandPosition(Vector3 position, bool leftHand)
        {
            EnsureAnimationIKSolver()?.SetAnimatedIKPosition(
                leftHand ? ELimbEndEffector.LeftHand : ELimbEndEffector.RightHand,
                position);
        }

        public void SetAnimatedHandRotation(Quaternion rotation, bool leftHand)
        {
            EnsureAnimationIKSolver()?.SetAnimatedIKRotation(
                leftHand ? ELimbEndEffector.LeftHand : ELimbEndEffector.RightHand,
                rotation);
        }

        public HumanoidIKSolverComponent? EnsureAnimationIKSolver()
        {
            if (TryGetSiblingComponent<VRIKSolverComponent>(out var vrik) && vrik is not null)
                return null;

            // Only return an existing solver — never auto-create or auto-reconfigure one.
            // Silent reconfiguration here clobbers user-authored IK tuning during playback.
            var solver = GetSiblingComponent<HumanoidIKSolverComponent>(false);
            return solver is { IsActive: true } ? solver : null;
        }

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
        /// in absolute body space (e.g. RootT.y ≈ 1.0 = hip height above ground).
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

        // ── Runtime debug overrides for muscle→rotation sign tuning ─────────
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
            /// <summary>When true, negate the blanket LH→RH angle negation for the yaw (twist) axis.</summary>
            public bool SkipBlanketNegateYaw;
            /// <summary>When true, negate the blanket LH→RH angle negation for the pitch (front/back) axis.</summary>
            public bool SkipBlanketNegatePitch;
            /// <summary>When true, negate the blanket LH→RH angle negation for the roll (left/right) axis.</summary>
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

        // ── End debug overrides ─────────────────────────────────────────────

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
        /// Removes all IK targets, effectively disabling IK.
        /// </summary>
        public void ClearIKTargets()
        {
            HeadTarget = (null, Matrix4x4.Identity);
            LeftHandTarget = (null, Matrix4x4.Identity);
            RightHandTarget = (null, Matrix4x4.Identity);
            HipsTarget = (null, Matrix4x4.Identity);
            LeftFootTarget = (null, Matrix4x4.Identity);
            RightFootTarget = (null, Matrix4x4.Identity);
            ChestTarget = (null, Matrix4x4.Identity);
            LeftElbowTarget = (null, Matrix4x4.Identity);
            RightElbowTarget = (null, Matrix4x4.Identity);
            LeftKneeTarget = (null, Matrix4x4.Identity);
            RightKneeTarget = (null, Matrix4x4.Identity);
        }

        /// <summary>
        /// Resets the pose of the humanoid to the default pose (T-pose).
        /// </summary>
        public void ResetPose()
        {
            ResetRuntimeAnimationDiagnostics();
            Head.ResetPose();
            Jaw.ResetPose();
            Neck.ResetPose();
            UpperChest.ResetPose();
            Chest.ResetPose();
            Spine.ResetPose();
            Hips.ResetPose();

            Left.ResetPose();
            Right.ResetPose();
        }

        private void ResetRuntimeAnimationDiagnostics()
        {
            ResetRootMotionBaseline();
            _leftKneeClampLogged = false;
            _rightKneeClampLogged = false;
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
