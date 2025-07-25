﻿using System.Numerics;
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
    public enum EHumanoidValue
    {
        LeftEyeDownUp,
        LeftEyeInOut,
        RightEyeDownUp,
        RightEyeInOut,
    }
    public class HumanoidComponent : XRComponent, IRenderable
    {
        protected internal override void OnComponentActivated()
        {
            base.OnComponentActivated();
            if (SolveIK)
                RegisterTick(ETickGroup.Late, ETickOrder.Scene, SolveFullBodyIK);
        }

        protected internal override void OnComponentDeactivated()
        {
            base.OnComponentDeactivated();
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
            }
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

        protected internal override void AddedToSceneNode(SceneNode sceneNode)
        {
            base.AddedToSceneNode(sceneNode);
            SetFromNode();
        }

        private bool _solveIK = true;
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
        public BoneDef Neck { get; } = new();
        public BoneDef Head { get; } = new();
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

            return _hipToHeadChain ??= Link([Hips, Spine, Chest, Neck, Head]);
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
                FindChildrenFor(Head, [
                    (Left.Eye, ByPosition("Eye", x => x.X > 0.0f)),
                    (Right.Eye, ByPosition("Eye", x => x.X < 0.0f)),
                ]);

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
            Neck.ResetPose();
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
