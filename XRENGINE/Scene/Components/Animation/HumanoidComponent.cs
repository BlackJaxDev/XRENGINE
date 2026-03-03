using System.Numerics;
using XREngine.Components;
using XREngine.Components.Scene.Mesh;
using XREngine.Data;
using XREngine.Data.Colors;
using XREngine.Data.Core;
using XREngine.Data.Rendering;
using XREngine.Rendering.Info;
using XREngine.Rendering.Models;
using XREngine.Scene;
using XREngine.Scene.Transforms;
using static XREngine.Components.Animation.InverseKinematics;

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

            // Apply muscle-driven pose after animation evaluation (before Late IK solve).
            RegisterTick(ETickGroup.Normal, ETickOrder.Scene, ApplyMusclePose);

            if (SolveIK)
                RegisterTick(ETickGroup.Late, ETickOrder.Scene, SolveFullBodyIK);
        }

        protected internal override void OnComponentDeactivated()
        {
            base.OnComponentDeactivated();

            UnregisterTick(ETickGroup.Normal, ETickOrder.Scene, ApplyMusclePose);

            if (SolveIK)
                UnregisterTick(ETickGroup.Late, ETickOrder.Scene, SolveFullBodyIK);
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
                        float yaw = Settings.GetValue(EHumanoidValue.LeftEyeInOut);
                        float pitch = Interp.Lerp(Settings.LeftEyeDownUpRange.X, Settings.LeftEyeDownUpRange.Y, t);
                        Settings.SetValue(EHumanoidValue.LeftEyeDownUp, pitch);

                        Quaternion q = Quaternion.CreateFromYawPitchRoll(yaw, pitch, 0.0f);
                        Left.Eye.Node?.GetTransformAs<Transform>(true)?.SetBindRelativeRotation(q);
                        break;
                    }
                case EHumanoidValue.LeftEyeInOut:
                    {
                        float yaw = Interp.Lerp(Settings.LeftEyeInOutRange.X, Settings.LeftEyeInOutRange.Y, t);
                        float pitch = Settings.GetValue(EHumanoidValue.LeftEyeDownUp);
                        Settings.SetValue(EHumanoidValue.LeftEyeInOut, yaw);

                        Quaternion q = Quaternion.CreateFromYawPitchRoll(yaw, pitch, 0.0f);
                        Left.Eye.Node?.GetTransformAs<Transform>(true)?.SetBindRelativeRotation(q);
                        break;
                    }
                case EHumanoidValue.RightEyeDownUp:
                    {
                        float yaw = Settings.GetValue(EHumanoidValue.RightEyeInOut);
                        float pitch = Interp.Lerp(Settings.RightEyeDownUpRange.X, Settings.RightEyeDownUpRange.Y, t);
                        Settings.SetValue(EHumanoidValue.RightEyeDownUp, pitch);

                        Quaternion q = Quaternion.CreateFromYawPitchRoll(yaw, pitch, 0.0f);
                        Right.Eye.Node?.GetTransformAs<Transform>(true)?.SetBindRelativeRotation(q);
                        break;
                    }
                case EHumanoidValue.RightEyeInOut:
                    {
                        float yaw = Interp.Lerp(Settings.RightEyeInOutRange.X, Settings.RightEyeInOutRange.Y, t);
                        float pitch = Settings.GetValue(EHumanoidValue.RightEyeDownUp);
                        Settings.SetValue(EHumanoidValue.RightEyeInOut, yaw);

                        Quaternion q = Quaternion.CreateFromYawPitchRoll(yaw, pitch, 0.0f);
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

            // Torso
            ApplyBindRelativeEulerDegrees(
                Spine.Node,
                yawDeg: MapMuscleToDeg(EHumanoidValue.SpineTwistLeftRight, GetMuscleValue(EHumanoidValue.SpineTwistLeftRight), Settings.SpineTwistLeftRightDegRange),
                pitchDeg: MapMuscleToDeg(EHumanoidValue.SpineFrontBack, GetMuscleValue(EHumanoidValue.SpineFrontBack), Settings.SpineFrontBackDegRange),
                rollDeg: MapMuscleToDeg(EHumanoidValue.SpineLeftRight, GetMuscleValue(EHumanoidValue.SpineLeftRight), Settings.SpineLeftRightDegRange));

            ApplyBindRelativeEulerDegrees(
                Chest.Node,
                yawDeg: MapMuscleToDeg(EHumanoidValue.ChestTwistLeftRight, GetMuscleValue(EHumanoidValue.ChestTwistLeftRight), Settings.ChestTwistLeftRightDegRange)
                    + (UpperChest.Node is null ? MapMuscleToDeg(EHumanoidValue.UpperChestTwistLeftRight, GetMuscleValue(EHumanoidValue.UpperChestTwistLeftRight), Settings.UpperChestTwistLeftRightDegRange) : 0.0f),
                pitchDeg: MapMuscleToDeg(EHumanoidValue.ChestFrontBack, GetMuscleValue(EHumanoidValue.ChestFrontBack), Settings.ChestFrontBackDegRange)
                      + (UpperChest.Node is null ? MapMuscleToDeg(EHumanoidValue.UpperChestFrontBack, GetMuscleValue(EHumanoidValue.UpperChestFrontBack), Settings.UpperChestFrontBackDegRange) : 0.0f),
                rollDeg: MapMuscleToDeg(EHumanoidValue.ChestLeftRight, GetMuscleValue(EHumanoidValue.ChestLeftRight), Settings.ChestLeftRightDegRange)
                     + (UpperChest.Node is null ? MapMuscleToDeg(EHumanoidValue.UpperChestLeftRight, GetMuscleValue(EHumanoidValue.UpperChestLeftRight), Settings.UpperChestLeftRightDegRange) : 0.0f));

            // UpperChest — only applied when a separate UpperChest bone exists.
            if (UpperChest.Node is not null)
            {
                ApplyBindRelativeEulerDegrees(
                    UpperChest.Node,
                    yawDeg: MapMuscleToDeg(EHumanoidValue.UpperChestTwistLeftRight, GetMuscleValue(EHumanoidValue.UpperChestTwistLeftRight), Settings.UpperChestTwistLeftRightDegRange),
                    pitchDeg: MapMuscleToDeg(EHumanoidValue.UpperChestFrontBack, GetMuscleValue(EHumanoidValue.UpperChestFrontBack), Settings.UpperChestFrontBackDegRange),
                    rollDeg: MapMuscleToDeg(EHumanoidValue.UpperChestLeftRight, GetMuscleValue(EHumanoidValue.UpperChestLeftRight), Settings.UpperChestLeftRightDegRange));
            }

            // Neck / Head
            ApplyBindRelativeEulerDegrees(
                Neck.Node,
                yawDeg: MapMuscleToDeg(EHumanoidValue.NeckTurnLeftRight, GetMuscleValue(EHumanoidValue.NeckTurnLeftRight), new Vector2(-30.0f, 30.0f)),
                pitchDeg: MapMuscleToDeg(EHumanoidValue.NeckNodDownUp, GetMuscleValue(EHumanoidValue.NeckNodDownUp), Settings.NeckNodDownUpDegRange),
                rollDeg: MapMuscleToDeg(EHumanoidValue.NeckTiltLeftRight, GetMuscleValue(EHumanoidValue.NeckTiltLeftRight), new Vector2(-20.0f, 20.0f)));

            ApplyBindRelativeEulerDegrees(
                Head.Node,
                yawDeg: MapMuscleToDeg(EHumanoidValue.HeadTurnLeftRight, GetMuscleValue(EHumanoidValue.HeadTurnLeftRight), new Vector2(-60.0f, 60.0f)),
                pitchDeg: MapMuscleToDeg(EHumanoidValue.HeadNodDownUp, GetMuscleValue(EHumanoidValue.HeadNodDownUp), Settings.HeadNodDownUpDegRange),
                rollDeg: MapMuscleToDeg(EHumanoidValue.HeadTiltLeftRight, GetMuscleValue(EHumanoidValue.HeadTiltLeftRight), new Vector2(-30.0f, 30.0f)));

            // Jaw
            ApplyBindRelativeEulerDegrees(
                Jaw.Node,
                yawDeg: MapMuscleToDeg(EHumanoidValue.JawLeftRight, GetMuscleValue(EHumanoidValue.JawLeftRight), new Vector2(-15.0f, 15.0f)),
                pitchDeg: MapMuscleToDeg(EHumanoidValue.JawClose, GetMuscleValue(EHumanoidValue.JawClose), new Vector2(-5.0f, 25.0f)),
                rollDeg: 0.0f);

            // Arms / Legs / Hands / Feet
            ApplyLimbMuscles(isLeft: true);
            ApplyLimbMuscles(isLeft: false);

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

        private void ApplyLimbMuscles(bool isLeft)
        {
            var side = isLeft ? Left : Right;
            float sideMirror = isLeft ? 1.0f : -1.0f;
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

            // Unity "Stretch" channels for forearm/lower-leg are opposite to our prior assumption:
            // positive values trend toward extension (straighter limb), not deeper flexion.
            // Invert these two channels before mapping to joint pitch.
            float forearmStretchMuscle = -GetMuscleValue(forearmStretch);
            float lowerLegStretchMuscle = -GetMuscleValue(lowerLegStretch);

            // Body-space basis from hips bind pose (Unity humanoid muscles are avatar/body-space driven).
            Vector3 bodyRight = GetBodyAxisWorld(Vector3.UnitX);
            Vector3 bodyForward = GetBodyAxisWorld(Vector3.UnitZ);

            // Limb twist axis from bind-pose bone direction (parent -> child).
            Vector3 armTwistAxisWorld = GetBoneToChildAxisWorld(side.Arm, side.Elbow, GetBodyAxisWorld(Vector3.UnitY));
            Vector3 legTwistAxisWorld = GetBoneToChildAxisWorld(side.Leg, side.Knee, -GetBodyAxisWorld(Vector3.UnitY));

            // Swing axes in body space.
            Vector3 sideAxisWorld = Vector3.Normalize(bodyForward * sideMirror);
            Vector3 frontBackAxisWorld = bodyRight;

            // Shoulder
            ApplyBindRelativeSwingTwistWorldAxes(
                side.Shoulder.Node,
                yawDeg: 0.0f,
                pitchDeg: MapMuscleToDeg(shoulderDownUp, GetMuscleValue(shoulderDownUp), new Vector2(-35.0f, 35.0f)),
                rollDeg: MapMuscleToDeg(shoulderFrontBack, GetMuscleValue(shoulderFrontBack), new Vector2(-25.0f, 25.0f)),
                twistAxisWorld: armTwistAxisWorld,
                frontBackAxisWorld: sideAxisWorld,
                leftRightAxisWorld: frontBackAxisWorld);

            // Upper arm
            ApplyBindRelativeSwingTwistWorldAxes(
                side.Arm.Node,
                yawDeg: MapMuscleToDeg(armTwist, GetMuscleValue(armTwist), new Vector2(-40.0f, 40.0f)) * sideMirror,
                pitchDeg: MapMuscleToDeg(armDownUp, GetMuscleValue(armDownUp), new Vector2(-90.0f, 90.0f)),
                rollDeg: MapMuscleToDeg(armFrontBack, GetMuscleValue(armFrontBack), new Vector2(-60.0f, 60.0f)),
                twistAxisWorld: armTwistAxisWorld,
                frontBackAxisWorld: sideAxisWorld,
                leftRightAxisWorld: frontBackAxisWorld);

            // Forearm (elbow) — Forearm Stretch is elbow flexion (pitch), not a bone scale.
            ApplyBindRelativeEulerDegrees(
                side.Elbow.Node,
                yawDeg: MapMuscleToDeg(forearmTwist, GetMuscleValue(forearmTwist), new Vector2(-60.0f, 60.0f)) * sideMirror,
                pitchDeg: MapMuscleToDeg(forearmStretch, forearmStretchMuscle, Settings.ForearmStretchDegRange),
                rollDeg: 0.0f,
                axisMapping: GetBoneAxisMapping(side.Elbow.Node));

            // Wrist
            ApplyBindRelativeEulerDegrees(
                side.Wrist.Node,
                yawDeg: 0.0f,
                pitchDeg: MapMuscleToDeg(handDownUp, GetMuscleValue(handDownUp), new Vector2(-60.0f, 60.0f)),
                rollDeg: MapMuscleToDeg(handInOut, GetMuscleValue(handInOut), new Vector2(-45.0f, 45.0f)) * sideMirror,
                axisMapping: GetBoneAxisMapping(side.Wrist.Node));

            // Upper leg
            ApplyBindRelativeSwingTwistWorldAxes(
                side.Leg.Node,
                yawDeg: MapMuscleToDeg(upperLegTwist, GetMuscleValue(upperLegTwist), new Vector2(-45.0f, 45.0f)) * sideMirror,
                pitchDeg: MapMuscleToDeg(upperLegFrontBack, GetMuscleValue(upperLegFrontBack), new Vector2(-90.0f, 90.0f)),
                rollDeg: MapMuscleToDeg(upperLegInOut, GetMuscleValue(upperLegInOut), new Vector2(-35.0f, 35.0f)),
                twistAxisWorld: legTwistAxisWorld,
                frontBackAxisWorld: frontBackAxisWorld,
                leftRightAxisWorld: sideAxisWorld);

            // Lower leg (knee) — Lower Leg Stretch is knee flexion (pitch), not a bone scale.
            ApplyBindRelativeEulerDegrees(
                side.Knee.Node,
                yawDeg: MapMuscleToDeg(lowerLegTwist, GetMuscleValue(lowerLegTwist), new Vector2(-30.0f, 30.0f)) * sideMirror,
                pitchDeg: MapMuscleToDeg(lowerLegStretch, lowerLegStretchMuscle, Settings.LowerLegStretchDegRange),
                rollDeg: 0.0f,
                axisMapping: GetBoneAxisMapping(side.Knee.Node));

            // Foot / Toes
            ApplyBindRelativeEulerDegrees(
                side.Foot.Node,
                yawDeg: MapMuscleToDeg(footTwist, GetMuscleValue(footTwist), new Vector2(-30.0f, 30.0f)) * sideMirror,
                pitchDeg: MapMuscleToDeg(footUpDown, GetMuscleValue(footUpDown), new Vector2(-45.0f, 45.0f)),
                rollDeg: 0.0f,
                axisMapping: GetBoneAxisMapping(side.Foot.Node));

            ApplyBindRelativeEulerDegrees(
                side.Toes.Node,
                yawDeg: 0.0f,
                pitchDeg: MapMuscleToDeg(toesUpDown, GetMuscleValue(toesUpDown), new Vector2(-35.0f, 35.0f)),
                rollDeg: 0.0f,
                axisMapping: GetBoneAxisMapping(side.Toes.Node));
        }

        private void ApplyFingerMuscles(bool isLeft)
        {
            var side = isLeft ? Left : Right;

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

            ApplyFinger(isLeft, side.Hand.Thumb,
                spread: isLeft ? EHumanoidValue.LeftHandThumbSpread : EHumanoidValue.RightHandThumbSpread,
                prox: isLeft ? EHumanoidValue.LeftHandThumb1Stretched : EHumanoidValue.RightHandThumb1Stretched,
                mid: isLeft ? EHumanoidValue.LeftHandThumb2Stretched : EHumanoidValue.RightHandThumb2Stretched,
                dist: isLeft ? EHumanoidValue.LeftHandThumb3Stretched : EHumanoidValue.RightHandThumb3Stretched);
        }

        private void ApplyFinger(bool isLeft, BodySide.Fingers.Finger finger, EHumanoidValue spread, EHumanoidValue prox, EHumanoidValue mid, EHumanoidValue dist)
        {
            float sideMirror = isLeft ? 1.0f : -1.0f;
            // Stretched channels map to bending on each phalanx.
            ApplyBindRelativeEulerDegrees(
                finger.Proximal.Node,
                yawDeg: MapMuscleToDeg(spread, GetMuscleValue(spread), new Vector2(-15.0f, 15.0f)) * sideMirror,
                pitchDeg: MapMuscleToDeg(prox, GetMuscleValue(prox), new Vector2(-45.0f, 45.0f)),
                rollDeg: 0.0f,
                axisMapping: GetBoneAxisMapping(finger.Proximal.Node));

            ApplyBindRelativeEulerDegrees(
                finger.Intermediate.Node,
                yawDeg: 0.0f,
                pitchDeg: MapMuscleToDeg(mid, GetMuscleValue(mid), new Vector2(-45.0f, 45.0f)),
                rollDeg: 0.0f,
                axisMapping: GetBoneAxisMapping(finger.Intermediate.Node));

            ApplyBindRelativeEulerDegrees(
                finger.Distal.Node,
                yawDeg: 0.0f,
                pitchDeg: MapMuscleToDeg(dist, GetMuscleValue(dist), new Vector2(-45.0f, 45.0f)),
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

        private float MapMuscleToDeg(EHumanoidValue value, float muscle, Vector2 defaultDegreeRange)
        {
            // Unity humanoid muscle mapping is piecewise-linear through zero:
            //   muscle = -1  → minDeg
            //   muscle =  0  → 0° (bind pose, no rotation)
            //   muscle = +1  → maxDeg
            // This ensures asymmetric ranges (e.g. knee: -10°..130°) don't
            // produce a non-zero rotation at rest (muscle=0).
            const float maxMuscleMagnitude = 1.5f;
            float m = System.Math.Clamp(muscle, -maxMuscleMagnitude, maxMuscleMagnitude);
            m *= Settings.MuscleInputScale;
            m = System.Math.Clamp(m, -maxMuscleMagnitude, maxMuscleMagnitude);

            Vector2 range = defaultDegreeRange;
            if (Settings.TryGetMuscleRotationDegRange(value, out var configuredRange))
                range = configuredRange;

            // Piecewise: negative muscle scales toward min, positive toward max.
            // range.X is the negative limit (e.g. -10°), range.Y is the positive limit (e.g. 130°).
            return m >= 0.0f
                ? m * range.Y
                : -m * range.X;   // range.X is negative, so result is negative
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

            Quaternion q;
            if (axisMapping.HasValue)
            {
                var m = axisMapping.Value;
                // Build rotation using axis-angle per DOF with proper swing-twist order.
                // The twist (yawDeg) is applied innermost (first), then the two swing
                // rotations are applied on top. This matches the swing-twist decomposition
                // used by Unity's humanoid muscle system.
                Vector3 twistVec = AxisIndexToVector(m.TwistAxis);
                Vector3 fbVec = AxisIndexToVector(m.FrontBackAxis);
                Vector3 lrVec = AxisIndexToVector(m.LeftRightAxis);

                Quaternion twist = Quaternion.CreateFromAxisAngle(twistVec, yawDeg * degToRad);
                Quaternion frontBack = Quaternion.CreateFromAxisAngle(fbVec, pitchDeg * degToRad);
                Quaternion leftRight = Quaternion.CreateFromAxisAngle(lrVec, rollDeg * degToRad);

                // Composition: swing * twist  (twist applied first, swings on top)
                q = frontBack * leftRight * twist;
            }
            else
            {
                q = Quaternion.CreateFromYawPitchRoll(yawDeg * degToRad, pitchDeg * degToRad, rollDeg * degToRad);
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

        private Vector3 GetBodyAxisWorld(Vector3 localAxis)
        {
            Matrix4x4 bodyBind = Hips.Node?.Transform?.BindMatrix ?? Matrix4x4.Identity;
            Vector3 axisWorld = Vector3.TransformNormal(localAxis, bodyBind);
            float lenSq = axisWorld.LengthSquared();
            return lenSq > 1e-8f ? axisWorld / MathF.Sqrt(lenSq) : localAxis;
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

        private static void ApplyBindRelativeSwingTwistWorldAxes(
            SceneNode? node,
            float yawDeg,
            float pitchDeg,
            float rollDeg,
            Vector3 twistAxisWorld,
            Vector3 frontBackAxisWorld,
            Vector3 leftRightAxisWorld)
        {
            if (node?.Transform is null)
                return;

            const float degToRad = MathF.PI / 180.0f;

            Vector3 twistLocal = TransformWorldAxisToBoneLocal(node, twistAxisWorld);
            Vector3 frontBackLocal = TransformWorldAxisToBoneLocal(node, frontBackAxisWorld);
            Vector3 leftRightLocal = TransformWorldAxisToBoneLocal(node, leftRightAxisWorld);

            Quaternion twist = Quaternion.CreateFromAxisAngle(twistLocal, yawDeg * degToRad);
            Quaternion frontBack = Quaternion.CreateFromAxisAngle(frontBackLocal, pitchDeg * degToRad);
            Quaternion leftRight = Quaternion.CreateFromAxisAngle(leftRightLocal, rollDeg * degToRad);

            Quaternion q = frontBack * leftRight * twist;
            node.GetTransformAs<Transform>(true)?.SetBindRelativeRotation(q);
        }

        protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
        {
            base.OnPropertyChanged(propName, prev, field);
            switch (propName)
            {
                case nameof(SolveIK):
                    if (IsActive)
                    {
                        if (SolveIK)
                            RegisterTick(ETickGroup.Late, ETickOrder.Scene, SolveFullBodyIK);
                        else
                            UnregisterTick(ETickGroup.Late, ETickOrder.Scene, SolveFullBodyIK);
                    }
                    break;
            }
        }

        private void SolveFullBodyIK()
        {
            if (!HasActiveIKTargetOverrides())
                return;

            InverseKinematics.SolveFullBodyIK(
                GetHipToHeadChain(),
                GetLeftLegToAnkleChain(),
                GetRightLegToAnkleChain(),
                GetLeftShoulderToWristChain(),
                GetRightShoulderToWristChain(),
                HeadTarget,
                HipsTarget,
                LeftHandTarget,
                RightHandTarget,
                LeftFootTarget,
                RightFootTarget,
                10);
        }

        private bool HasActiveIKTargetOverrides()
            => IsTargetOverridden(HeadTarget, Head.Node?.Transform)
            || IsTargetOverridden(HipsTarget, Hips.Node?.Transform)
            || IsTargetOverridden(LeftHandTarget, Left.Wrist.Node?.Transform)
            || IsTargetOverridden(RightHandTarget, Right.Wrist.Node?.Transform)
            || IsTargetOverridden(LeftFootTarget, Left.Foot.Node?.Transform)
            || IsTargetOverridden(RightFootTarget, Right.Foot.Node?.Transform)
            || IsTargetOverridden(ChestTarget, Chest.Node?.Transform)
            || IsTargetOverridden(LeftElbowTarget, Left.Elbow.Node?.Transform)
            || IsTargetOverridden(RightElbowTarget, Right.Elbow.Node?.Transform)
            || IsTargetOverridden(LeftKneeTarget, Left.Knee.Node?.Transform)
            || IsTargetOverridden(RightKneeTarget, Right.Knee.Node?.Transform);

        private static bool IsTargetOverridden((TransformBase? tfm, Matrix4x4 offset) target, TransformBase? defaultTransform)
            => !ReferenceEquals(target.tfm, defaultTransform)
            || target.offset != Matrix4x4.Identity;

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

            Engine.Rendering.Debug.RenderPoint(GetMatrixForTarget(HeadTarget).Translation, ColorF4.Green);
            Engine.Rendering.Debug.RenderPoint(GetMatrixForTarget(HipsTarget).Translation, ColorF4.Green);
            Engine.Rendering.Debug.RenderPoint(GetMatrixForTarget(LeftHandTarget).Translation, ColorF4.Green);
            Engine.Rendering.Debug.RenderPoint(GetMatrixForTarget(RightHandTarget).Translation, ColorF4.Green);
            Engine.Rendering.Debug.RenderPoint(GetMatrixForTarget(LeftFootTarget).Translation, ColorF4.Green);
            Engine.Rendering.Debug.RenderPoint(GetMatrixForTarget(RightFootTarget).Translation, ColorF4.Green);
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
                (Left.Leg, ByPosition(LegNameContains, x => x.X > 0.0f)),
                (Right.Leg, ByPosition(LegNameContains, x => x.X < 0.0f)),
            ]);

            if (Spine.Node is not null && Chest.Node is null)
                FindChildrenFor(Spine, [
                    (Chest, ByName("Chest")),
                ]);

            if (Chest.Node is not null)
                FindChildrenFor(Chest, [
                    (UpperChest, ByNameContainsAll("UpperChest", "Upper_Chest")),
                    (Neck, ByName("Neck")),
                    (Head, ByName("Head")),
                    (Left.Shoulder, ByPosition("Shoulder", x => x.X > 0.0f)),
                    (Right.Shoulder, ByPosition("Shoulder", x => x.X < 0.0f)),
                ]);

            // If UpperChest was found, shoulders/neck/head may be children of UpperChest rather than Chest.
            if (UpperChest.Node is not null)
                FindChildrenFor(UpperChest, [
                    (Neck, ByName("Neck")),
                    (Head, ByName("Head")),
                    (Left.Shoulder, ByPosition("Shoulder", x => x.X > 0.0f)),
                    (Right.Shoulder, ByPosition("Shoulder", x => x.X < 0.0f)),
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
                    (Left.Eye, ByPosition("Eye", x => x.X > 0.0f)),
                    (Right.Eye, ByPosition("Eye", x => x.X < 0.0f)),
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
                FindChildrenFor(Left.Wrist, [
                    (Left.Hand.Pinky.Proximal, ByNameContainsAll("pinky", "1")),
                    (Left.Hand.Pinky.Intermediate, ByNameContainsAll("pinky", "2")),
                    (Left.Hand.Pinky.Distal, ByNameContainsAll("pinky", "3")),
                    (Left.Hand.Ring.Proximal, ByNameContainsAll("ring", "1")),
                    (Left.Hand.Ring.Intermediate, ByNameContainsAll("ring", "2")),
                    (Left.Hand.Ring.Distal, ByNameContainsAll("ring", "3")),
                    (Left.Hand.Middle.Proximal, ByNameContainsAll("middle", "1")),
                    (Left.Hand.Middle.Intermediate, ByNameContainsAll("middle", "2")),
                    (Left.Hand.Middle.Distal, ByNameContainsAll("middle", "3")),
                    (Left.Hand.Index.Proximal, ByNameContainsAll("index", "1")),
                    (Left.Hand.Index.Intermediate, ByNameContainsAll("index", "2")),
                    (Left.Hand.Index.Distal, ByNameContainsAll("index", "3")),
                    (Left.Hand.Thumb.Proximal, ByNameContainsAll("thumb", "1")),
                    (Left.Hand.Thumb.Intermediate, ByNameContainsAll("thumb", "2")),
                    (Left.Hand.Thumb.Distal, ByNameContainsAll("thumb", "3")),
                ]);

            if (Right.Wrist.Node is not null)
                FindChildrenFor(Right.Wrist, [
                    (Right.Hand.Pinky.Proximal, ByNameContainsAll("pinky", "1")),
                    (Right.Hand.Pinky.Intermediate, ByNameContainsAll("pinky", "2")),
                    (Right.Hand.Pinky.Distal, ByNameContainsAll("pinky", "3")),
                    (Right.Hand.Ring.Proximal, ByNameContainsAll("ring", "1")),
                    (Right.Hand.Ring.Intermediate, ByNameContainsAll("ring", "2")),
                    (Right.Hand.Ring.Distal, ByNameContainsAll("ring", "3")),
                    (Right.Hand.Middle.Proximal, ByNameContainsAll("middle", "1")),
                    (Right.Hand.Middle.Intermediate, ByNameContainsAll("middle", "2")),
                    (Right.Hand.Middle.Distal, ByNameContainsAll("middle", "3")),
                    (Right.Hand.Index.Proximal, ByNameContainsAll("index", "1")),
                    (Right.Hand.Index.Intermediate, ByNameContainsAll("index", "2")),
                    (Right.Hand.Index.Distal, ByNameContainsAll("index", "3")),
                    (Right.Hand.Thumb.Proximal, ByNameContainsAll("thumb", "1")),
                    (Right.Hand.Thumb.Intermediate, ByNameContainsAll("thumb", "2")),
                    (Right.Hand.Thumb.Distal, ByNameContainsAll("thumb", "3")),
                ]);

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

            // Auto-detect per-bone axis mappings from the skeleton geometry.
            // This determines each bone's twist axis by looking at the direction
            // to the next bone in the chain in the bone's own local space.
            ComputeAutoAxisMappings();

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

        /// <summary>
        /// Logs a one-time snapshot of live muscle values and the resulting degree
        /// values for key bones. Written to the Animation log category.
        /// </summary>
        private void LogMusclePoseSnapshot()
        {
            if (_musclePoseLoggedOnce) return;
            _musclePoseLoggedOnce = true;

            void LogMuscle(string label, EHumanoidValue val, Vector2 range)
            {
                float raw = GetMuscleValue(val);
                float deg = MapMuscleToDeg(val, raw, range);
                Debug.Animation($"[MusclePose] {label,-30} muscle={raw,8:F4}  deg={deg,8:F2}  range=({range.X:F1},{range.Y:F1})");
            }

            void LogInvertedMuscle(string label, EHumanoidValue val, Vector2 range)
            {
                float raw = GetMuscleValue(val);
                float effective = -raw;
                float deg = MapMuscleToDeg(val, effective, range);
                Debug.Animation($"[MusclePose] {label,-30} raw={raw,8:F4}  effective={effective,8:F4}  deg={deg,8:F2}  range=({range.X:F1},{range.Y:F1})");
            }

            Debug.Animation("[MusclePose] === One-time muscle snapshot ===");

            // Spine
            LogMuscle("SpineFrontBack", EHumanoidValue.SpineFrontBack, Settings.SpineFrontBackDegRange);
            LogMuscle("SpineTwist", EHumanoidValue.SpineTwistLeftRight, Settings.SpineTwistLeftRightDegRange);

            // Left arm
            LogMuscle("L.ArmDownUp", EHumanoidValue.LeftArmDownUp, new(-90, 90));
            LogMuscle("L.ArmFrontBack", EHumanoidValue.LeftArmFrontBack, new(-60, 60));
            LogMuscle("L.ArmTwist", EHumanoidValue.LeftArmTwistInOut, new(-40, 40));
            LogInvertedMuscle("L.ForearmStretch(inv)", EHumanoidValue.LeftForearmStretch, Settings.ForearmStretchDegRange);
            LogMuscle("L.ForearmTwist", EHumanoidValue.LeftForearmTwistInOut, new(-60, 60));

            // Right arm
            LogMuscle("R.ArmDownUp", EHumanoidValue.RightArmDownUp, new(-90, 90));
            LogMuscle("R.ArmFrontBack", EHumanoidValue.RightArmFrontBack, new(-60, 60));
            LogInvertedMuscle("R.ForearmStretch(inv)", EHumanoidValue.RightForearmStretch, Settings.ForearmStretchDegRange);

            // Left leg
            LogMuscle("L.UpperLegFrontBack", EHumanoidValue.LeftUpperLegFrontBack, new(-90, 90));
            LogMuscle("L.UpperLegInOut", EHumanoidValue.LeftUpperLegInOut, new(-35, 35));
            LogInvertedMuscle("L.LowerLegStretch(inv)", EHumanoidValue.LeftLowerLegStretch, Settings.LowerLegStretchDegRange);
            LogMuscle("L.FootUpDown", EHumanoidValue.LeftFootUpDown, new(-45, 45));

            // Right leg
            LogMuscle("R.UpperLegFrontBack", EHumanoidValue.RightUpperLegFrontBack, new(-90, 90));
            LogInvertedMuscle("R.LowerLegStretch(inv)", EHumanoidValue.RightLowerLegStretch, Settings.LowerLegStretchDegRange);

            Debug.Animation("[MusclePose] === End snapshot ===");
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
        /// Auto-detects per-bone axis mappings from the actual skeleton geometry.
        /// For each bone pair (parent → child in the humanoid chain), computes
        /// which local axis the bone primarily extends along (the twist axis),
        /// and assigns the remaining two axes as front-back and left-right.
        /// Only overrides when the detected twist axis differs from the default (Y).
        /// User-configured mappings in Settings.BoneAxisMappings are never overwritten.
        /// </summary>
        private void ComputeAutoAxisMappings()
        {
            // Spine chain
            AutoMapBoneAxis(Hips, Spine);
            AutoMapBoneAxis(Spine, Chest);
            AutoMapBoneAxis(Chest, UpperChest.Node is not null ? UpperChest : Neck);
            if (UpperChest.Node is not null)
                AutoMapBoneAxis(UpperChest, Neck);
            AutoMapBoneAxis(Neck, Head);

            // Left side
            AutoMapBoneAxis(Left.Shoulder, Left.Arm);
            AutoMapBoneAxis(Left.Arm, Left.Elbow);
            AutoMapBoneAxis(Left.Elbow, Left.Wrist);
            AutoMapBoneAxis(Left.Leg, Left.Knee);
            AutoMapBoneAxis(Left.Knee, Left.Foot);
            AutoMapBoneAxis(Left.Foot, Left.Toes);

            // Right side
            AutoMapBoneAxis(Right.Shoulder, Right.Arm);
            AutoMapBoneAxis(Right.Arm, Right.Elbow);
            AutoMapBoneAxis(Right.Elbow, Right.Wrist);
            AutoMapBoneAxis(Right.Leg, Right.Knee);
            AutoMapBoneAxis(Right.Knee, Right.Foot);
            AutoMapBoneAxis(Right.Foot, Right.Toes);
        }

        /// <summary>
        /// Computes the bone axis mapping for <paramref name="bone"/> based on
        /// the direction toward <paramref name="childBone"/> in the bone's local space.
        /// The dominant axis becomes the twist axis; the remaining two axes are assigned
        /// to front-back and left-right in their natural index order.
        /// </summary>
        private void AutoMapBoneAxis(BoneDef bone, BoneDef childBone)
        {
            if (bone.Node?.Name is null || childBone.Node is null)
                return;

            // Don't override user-configured mappings.
            if (Settings.TryGetBoneAxisMapping(bone.Node.Name, out _))
                return;

            // Compute direction from this bone to its child using world-space bind poses.
            // Using world positions is more robust than local translations when there are
            // intermediate nodes between the humanoid bones in the scene graph.
            Vector3 boneWorldPos = bone.WorldBindPose.Translation;
            Vector3 childWorldPos = childBone.WorldBindPose.Translation;
            Vector3 dirWorld = childWorldPos - boneWorldPos;

            if (dirWorld.LengthSquared() < 1e-8f)
                return;

            dirWorld = Vector3.Normalize(dirWorld);

            // Transform direction into the bone's local (post-bind) coordinate frame.
            if (!Matrix4x4.Invert(bone.WorldBindPose, out Matrix4x4 invBind))
                return;

            Vector3 dirLocal = Vector3.Normalize(Vector3.TransformNormal(dirWorld, invBind));

            float ax = MathF.Abs(dirLocal.X);
            float ay = MathF.Abs(dirLocal.Y);
            float az = MathF.Abs(dirLocal.Z);

            int twistAxis, frontBackAxis, leftRightAxis;

            if (ay >= ax && ay >= az)
            {
                // Y dominant — matches the default mapping, no override needed.
                return;
            }
            else if (ax >= ay && ax >= az)
            {
                // X dominant: twist = X, remaining = Y, Z.
                twistAxis = 0;
                frontBackAxis = 1;
                leftRightAxis = 2;
            }
            else
            {
                // Z dominant: twist = Z, remaining = X, Y.
                twistAxis = 2;
                frontBackAxis = 0;
                leftRightAxis = 1;
            }

            Settings.BoneAxisMappings[bone.Node.Name] = new BoneAxisMapping
            {
                TwistAxis = twistAxis,
                FrontBackAxis = frontBackAxis,
                LeftRightAxis = leftRightAxis,
            };

            Debug.Animation(
                $"[AutoAxisMap] '{bone.Node.Name}': " +
                $"twist={twistAxis} fb={frontBackAxis} lr={leftRightAxis} " +
                $"(local dir to child: ({dirLocal.X:F3},{dirLocal.Y:F3},{dirLocal.Z:F3}))");
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

        /// <summary>
        /// Applies root motion position as a bind-relative offset on the Hips bone.
        /// In Unity humanoid clips, RootT represents the body center (hips) position —
        /// e.g. RootT.y ≈ 1.0 means hip height above ground, not absolute world Y.
        /// </summary>
        public void SetRootPosition(Vector3 position)
        {
            var hipsNode = Hips.Node;
            if (hipsNode is null)
                return;

            var tfm = hipsNode.GetTransformAs<Transform>(true);
            if (tfm is null)
                return;

            // Apply as bind-relative translation: the animation position is relative to the bind pose.
            tfm.Translation = tfm.BindState.Translation + position;
        }

        /// <summary>
        /// Applies root motion rotation as a bind-relative rotation on the Hips bone.
        /// In Unity humanoid clips, RootQ represents the body center (hips) orientation.
        /// </summary>
        public void SetRootRotation(Quaternion rotation)
        {
            var hipsNode = Hips.Node;
            if (hipsNode is null)
                return;

            hipsNode.GetTransformAs<Transform>(true)?.SetBindRelativeRotation(rotation);
        }

        private static Func<SceneNode, bool> ByNameContainsAny(params string[] names)
            => node => names.Any(name => node.Name?.Contains(name, StringComparison.InvariantCultureIgnoreCase) ?? false);

        private static Func<SceneNode, bool> ByNameContainsAnyAndDoesNotContain(string[] any, string[] none)
            => node =>
            any.Any(name => node.Name?.Contains(name, StringComparison.InvariantCultureIgnoreCase) ?? false) &&
            none.All(name => !(node.Name?.Contains(name, StringComparison.InvariantCultureIgnoreCase) ?? false));

        private static Func<SceneNode, bool> ByNameContainsAll(params string[] names)
            => node => names.Any(name => node.Name?.Contains(name, StringComparison.InvariantCultureIgnoreCase) ?? false);

        private static Func<SceneNode, bool> ByNameContainsAny(StringComparison comp, params string[] names)
            => node => names.Any(name => node.Name?.Contains(name, comp) ?? false);

        private static Func<SceneNode, bool> ByNameContainsAll(StringComparison comp, params string[] names)
            => node => names.Any(name => node.Name?.Contains(name, comp) ?? false);
        
        private static Func<SceneNode, bool> ByPosition(string nameContains, Func<Vector3, bool> posMatch, StringComparison comp = StringComparison.InvariantCultureIgnoreCase)
            => node => (nameContains is null || (node.Name?.Contains(nameContains, comp) ?? false)) && posMatch(node.Transform.LocalTranslation);

        private static Func<SceneNode, bool> ByName(string name, StringComparison comp = StringComparison.InvariantCultureIgnoreCase)
            => node => node.Name?.Equals(name, comp) ?? false;

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
