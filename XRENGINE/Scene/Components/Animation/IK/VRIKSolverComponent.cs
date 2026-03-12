using System;
using System.Collections.Generic;
using System.Numerics;
using XREngine.Core.Attributes;
using XREngine.Networking;
using XREngine.Scene.Transforms;
using XREngine.Timers;

namespace XREngine.Components.Animation
{
    /// <summary>
    /// Component that uses the VRIK solver to solve IK for a humanoid character controlled by a VR headset, controllers, and optional trackers.
    /// </summary>
    [RequireComponents(typeof(HumanoidComponent))]
    public class VRIKSolverComponent : IKSolverComponent
    {
        private const double BaselineIntervalSeconds = 1.0;
        private static readonly long BaselineIntervalTicks = EngineTimer.SecondsToStopwatchTicks(BaselineIntervalSeconds);
        private static readonly Dictionary<ushort, (QuantizedHumanoidPose pose, ushort sequence)> _receivedBaselines = new();
        private static readonly Dictionary<ushort, VRIKSolverComponent> _registry = new();

        public IKSolverVR Solver { get; } = new();

        public bool UpdateHeadTarget { get; set; } = true;
        public bool UpdateHipsTarget { get; set; } = true;
        public bool UpdateLeftHandTarget { get; set; } = true;
        public bool UpdateRightHandTarget { get; set; } = true;
        public bool UpdateLeftFootTarget { get; set; } = true;
        public bool UpdateRightFootTarget { get; set; } = true;
        public bool UpdateLeftElbowTarget { get; set; } = true;
        public bool UpdateRightElbowTarget { get; set; } = true;
        public bool UpdateLeftKneeTarget { get; set; } = true;
        public bool UpdateRightKneeTarget { get; set; } = true;
        public bool UpdateChestTarget { get; set; } = true;

        public (TransformBase? tfm, Matrix4x4 offset) HeadTarget
        {
            get => GetHumanoidTarget(EHumanoidIKTarget.Head);
            set => SetHumanoidTarget(EHumanoidIKTarget.Head, value.tfm, value.offset);
        }

        public (TransformBase? tfm, Matrix4x4 offset) HipsTarget
        {
            get => GetHumanoidTarget(EHumanoidIKTarget.Hips);
            set => SetHumanoidTarget(EHumanoidIKTarget.Hips, value.tfm, value.offset);
        }

        public (TransformBase? tfm, Matrix4x4 offset) LeftHandTarget
        {
            get => GetHumanoidTarget(EHumanoidIKTarget.LeftHand);
            set => SetHumanoidTarget(EHumanoidIKTarget.LeftHand, value.tfm, value.offset);
        }

        public (TransformBase? tfm, Matrix4x4 offset) RightHandTarget
        {
            get => GetHumanoidTarget(EHumanoidIKTarget.RightHand);
            set => SetHumanoidTarget(EHumanoidIKTarget.RightHand, value.tfm, value.offset);
        }

        public (TransformBase? tfm, Matrix4x4 offset) LeftFootTarget
        {
            get => GetHumanoidTarget(EHumanoidIKTarget.LeftFoot);
            set => SetHumanoidTarget(EHumanoidIKTarget.LeftFoot, value.tfm, value.offset);
        }

        public (TransformBase? tfm, Matrix4x4 offset) RightFootTarget
        {
            get => GetHumanoidTarget(EHumanoidIKTarget.RightFoot);
            set => SetHumanoidTarget(EHumanoidIKTarget.RightFoot, value.tfm, value.offset);
        }

        public (TransformBase? tfm, Matrix4x4 offset) LeftElbowTarget
        {
            get => GetHumanoidTarget(EHumanoidIKTarget.LeftElbow);
            set => SetHumanoidTarget(EHumanoidIKTarget.LeftElbow, value.tfm, value.offset);
        }

        public (TransformBase? tfm, Matrix4x4 offset) RightElbowTarget
        {
            get => GetHumanoidTarget(EHumanoidIKTarget.RightElbow);
            set => SetHumanoidTarget(EHumanoidIKTarget.RightElbow, value.tfm, value.offset);
        }

        public (TransformBase? tfm, Matrix4x4 offset) LeftKneeTarget
        {
            get => GetHumanoidTarget(EHumanoidIKTarget.LeftKnee);
            set => SetHumanoidTarget(EHumanoidIKTarget.LeftKnee, value.tfm, value.offset);
        }

        public (TransformBase? tfm, Matrix4x4 offset) RightKneeTarget
        {
            get => GetHumanoidTarget(EHumanoidIKTarget.RightKnee);
            set => SetHumanoidTarget(EHumanoidIKTarget.RightKnee, value.tfm, value.offset);
        }

        public (TransformBase? tfm, Matrix4x4 offset) ChestTarget
        {
            get => GetHumanoidTarget(EHumanoidIKTarget.Chest);
            set => SetHumanoidTarget(EHumanoidIKTarget.Chest, value.tfm, value.offset);
        }

        public ushort PoseEntityId
        {
            get => _poseEntityId;
            set
            {
                if (_poseEntityId == value)
                    return;
                Unregister();
                _poseEntityId = value;
                Register();
            }
        }

        /// <summary>
        /// When true, the component will publish VR humanoid pose frames over the networking manager.
        /// </summary>
        public bool PoseBroadcastEnabled { get; set; } = true;

        /// <summary>
        /// When true, incoming pose frames that match <see cref="PoseEntityId"/> will drive this solver's targets.
        /// </summary>
        public bool PoseReceiveEnabled { get; set; } = true;

        private ushort _poseEntityId;
        private QuantizedHumanoidPose? _baselinePose;
        private ushort _baselineSequence;
        private long _lastBaselineTicks;

        internal static bool ShouldSendBaseline(bool baselineMissing, long nowTicks, long lastBaselineTicks)
            => baselineMissing || Math.Max(0L, nowTicks - lastBaselineTicks) >= BaselineIntervalTicks;
        private HumanoidQuantizationSettings _quantization = HumanoidQuantizationSettings.Default;
        private HumanoidPoseDeltaSettings _delta = HumanoidPoseDeltaSettings.Default;

        public override void Visualize()
        {
            using var profilerState = Engine.Profiler.Start("VRIKSolverComponent.Visualize");
            Solver.Visualize();
        }

        public void ClearTargets()
        {
            Humanoid.ClearIKTarget(EHumanoidIKTarget.Head);
            Humanoid.ClearIKTarget(EHumanoidIKTarget.LeftHand);
            Humanoid.ClearIKTarget(EHumanoidIKTarget.RightHand);
            Humanoid.ClearIKTarget(EHumanoidIKTarget.Hips);
            Humanoid.ClearIKTarget(EHumanoidIKTarget.LeftFoot);
            Humanoid.ClearIKTarget(EHumanoidIKTarget.RightFoot);
            Humanoid.ClearIKTarget(EHumanoidIKTarget.Chest);
            Humanoid.ClearIKTarget(EHumanoidIKTarget.LeftElbow);
            Humanoid.ClearIKTarget(EHumanoidIKTarget.RightElbow);
            Humanoid.ClearIKTarget(EHumanoidIKTarget.LeftKnee);
            Humanoid.ClearIKTarget(EHumanoidIKTarget.RightKnee);
        }

        /// <summary>
        /// Fills in arm wristToPalmAxis and palmToThumbAxis.
        /// </summary>
        public void GuessHandOrientations()
            => Solver.GuessHandOrientations(Humanoid, false);

        public override IKSolver GetIKSolver() => Solver;

        protected override void InitializeSolver()
        {
            Solver.SetToReferences(Humanoid);
            SyncSolverTargets();
            base.InitializeSolver();
        }

        protected override void OnComponentActivated()
        {
            base.OnComponentActivated();
            ApplyEnvironmentOverrides();
            if (PoseEntityId == 0)
                PoseEntityId = PoseIdFromSceneNode();

            Register();
            SubscribeNetworking();
        }

        protected override void OnComponentDeactivated()
        {
            base.OnComponentDeactivated();
            Unregister();
            UnsubscribeNetworking();
        }

        protected override void UpdateSolver()
        {
            if (!(Humanoid?.SceneNode?.IsTransformNull ?? true) && Humanoid.SceneNode.Transform.LossyWorldScale.LengthSquared() < float.Epsilon)
            {
                Debug.Animation("VRIK Root Transform's scale is zero, can not update VRIK. Make sure you have not calibrated the character to a zero scale.");
                IsActive = false;
                return;
            }

            SyncSolverTargets();
            base.UpdateSolver();
            TrySendPose();
        }

        private void TrySendPose()
        {
            if (!PoseBroadcastEnabled)
                return;

            if (Engine.Networking is not BaseNetworkingManager net)
                return;

            if (Root is null || Humanoid is null)
                return;

            HumanoidPoseSample sample = CapturePose();
            QuantizedHumanoidPose quantized = HumanoidPoseCodec.Quantize(sample, _quantization);

            HumanoidPosePacketBuilder builder = new(_quantization, _delta);

            long nowTicks = Engine.ElapsedTicks;
            bool sendBaseline = ShouldSendBaseline(_baselinePose is null, nowTicks, _lastBaselineTicks);
            if (sendBaseline)
            {
                _baselineSequence++;
                _baselinePose = quantized;
                _lastBaselineTicks = nowTicks;

                builder.BeginFrame(HumanoidPosePacketKind.Baseline, _baselineSequence);
                builder.AddBaselineAvatar(PoseEntityId, quantized);
            }
            else
            {
                if (_baselinePose is null)
                    return;

                builder.BeginFrame(HumanoidPosePacketKind.Delta, _baselineSequence);
                builder.AddDeltaAvatar(PoseEntityId, quantized, _baselinePose.Value);
            }

            HumanoidPoseFrame frame = builder.BuildFrame();
            net.BroadcastHumanoidPoseFrame(frame, compress: false);
        }

        private HumanoidPoseSample CapturePose()
        {
            Vector3 rootPos = Root?.RenderTranslation ?? Vector3.Zero;
            float rootYaw = GetYawRadians(Root?.RenderRotation ?? Quaternion.Identity);

            Vector3 hip = GetLocalTracker(EHumanoidIKTarget.Hips, rootPos, rootYaw);
            Vector3 head = GetLocalTracker(EHumanoidIKTarget.Head, rootPos, rootYaw);
            Vector3 leftHand = GetLocalTracker(EHumanoidIKTarget.LeftHand, rootPos, rootYaw);
            Vector3 rightHand = GetLocalTracker(EHumanoidIKTarget.RightHand, rootPos, rootYaw);
            Vector3 leftFoot = GetLocalTracker(EHumanoidIKTarget.LeftFoot, rootPos, rootYaw);
            Vector3 rightFoot = GetLocalTracker(EHumanoidIKTarget.RightFoot, rootPos, rootYaw);

            return new HumanoidPoseSample(
                rootPos,
                rootYaw,
                hip,
                head,
                leftHand,
                rightHand,
                leftFoot,
                rightFoot);
        }

        private Vector3 GetLocalTracker(EHumanoidIKTarget targetType, Vector3 rootPos, float rootYaw)
        {
            var target = Humanoid.GetIKTarget(targetType);
            if (target.tfm is null)
                return Vector3.Zero;

            Matrix4x4 world = GetMatrixForTarget(target);
            Vector3 worldPos = world.Translation;
            Vector3 offset = worldPos - rootPos;

            Quaternion invYaw = Quaternion.CreateFromAxisAngle(Vector3.UnitY, -rootYaw);
            return Vector3.Transform(offset, invYaw);
        }

        private void ApplyPose(QuantizedHumanoidPose pose)
        {
            if (!PoseReceiveEnabled)
                return;

            HumanoidPoseSample sample = HumanoidPoseCodec.Dequantize(pose, _quantization);
            ApplySampleToTargets(sample);
        }

        private void ApplySampleToTargets(HumanoidPoseSample sample)
        {
            float yaw = sample.RootYawRadians;
            Quaternion yawRot = Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw);

            SetTransform(Root, sample.RootPosition, yawRot);
            if (UpdateHipsTarget)
                Humanoid.SetIKTargetWorldPosition(EHumanoidIKTarget.Hips, sample.RootPosition + Vector3.Transform(sample.Hip, yawRot));
            if (UpdateHeadTarget)
                Humanoid.SetIKTargetWorldPosition(EHumanoidIKTarget.Head, sample.RootPosition + Vector3.Transform(sample.Head, yawRot));
            if (UpdateLeftHandTarget)
                Humanoid.SetIKTargetWorldPosition(EHumanoidIKTarget.LeftHand, sample.RootPosition + Vector3.Transform(sample.LeftHand, yawRot));
            if (UpdateRightHandTarget)
                Humanoid.SetIKTargetWorldPosition(EHumanoidIKTarget.RightHand, sample.RootPosition + Vector3.Transform(sample.RightHand, yawRot));
            if (UpdateLeftFootTarget)
                Humanoid.SetIKTargetWorldPosition(EHumanoidIKTarget.LeftFoot, sample.RootPosition + Vector3.Transform(sample.LeftFoot, yawRot));
            if (UpdateRightFootTarget)
                Humanoid.SetIKTargetWorldPosition(EHumanoidIKTarget.RightFoot, sample.RootPosition + Vector3.Transform(sample.RightFoot, yawRot));
        }

        private void SyncSolverTargets()
        {
            Solver.Spine.HeadTarget = GetHumanoidTargetTransform(EHumanoidIKTarget.Head);
            Solver.Spine.HipsTarget = GetHumanoidTargetTransform(EHumanoidIKTarget.Hips);
            Solver.LeftArm.Target = GetHumanoidTargetTransform(EHumanoidIKTarget.LeftHand);
            Solver.RightArm.Target = GetHumanoidTargetTransform(EHumanoidIKTarget.RightHand);
            Solver.LeftLeg.Target = GetHumanoidTargetTransform(EHumanoidIKTarget.LeftFoot);
            Solver.RightLeg.Target = GetHumanoidTargetTransform(EHumanoidIKTarget.RightFoot);
            Solver.LeftArm.BendGoal = GetHumanoidTargetTransform(EHumanoidIKTarget.LeftElbow);
            Solver.RightArm.BendGoal = GetHumanoidTargetTransform(EHumanoidIKTarget.RightElbow);
            Solver.LeftLeg.KneeTarget = GetHumanoidTargetTransform(EHumanoidIKTarget.LeftKnee);
            Solver.RightLeg.KneeTarget = GetHumanoidTargetTransform(EHumanoidIKTarget.RightKnee);
        }

        private ushort PoseIdFromSceneNode()
        {
            Guid guid = SceneNode?.ID ?? Guid.NewGuid();
            return BitConverter.ToUInt16(guid.ToByteArray(), 0);
        }

        private void ApplyEnvironmentOverrides()
        {
            if (TryGetUShortEnv("XRE_POSE_ENTITY_ID", out ushort poseEntityId) && poseEntityId > 0)
            {
                PoseEntityId = poseEntityId;
                Debug.Out($"VRIK pose entity id overridden to {poseEntityId} via XRE_POSE_ENTITY_ID.");
            }

            if (TryGetBoolEnv("XRE_POSE_BROADCAST_ENABLED", out bool poseBroadcastEnabled))
            {
                PoseBroadcastEnabled = poseBroadcastEnabled;
                Debug.Out($"VRIK pose broadcast overridden to {poseBroadcastEnabled} via XRE_POSE_BROADCAST_ENABLED.");
            }

            if (TryGetBoolEnv("XRE_POSE_RECEIVE_ENABLED", out bool poseReceiveEnabled))
            {
                PoseReceiveEnabled = poseReceiveEnabled;
                Debug.Out($"VRIK pose receive overridden to {poseReceiveEnabled} via XRE_POSE_RECEIVE_ENABLED.");
            }
        }

        private static bool TryGetUShortEnv(string name, out ushort value)
        {
            value = default;
            string? raw = Environment.GetEnvironmentVariable(name);
            return !string.IsNullOrWhiteSpace(raw) && ushort.TryParse(raw, out value);
        }

        private static bool TryGetBoolEnv(string name, out bool value)
        {
            value = default;
            string? raw = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrWhiteSpace(raw))
                return false;

            if (raw == "1")
            {
                value = true;
                return true;
            }

            if (raw == "0")
            {
                value = false;
                return true;
            }

            return bool.TryParse(raw, out value);
        }

        private void SubscribeNetworking()
        {
            if (Engine.Networking is not BaseNetworkingManager net)
                return;

            net.HumanoidPoseFrameReceived += OnHumanoidPoseFrame;
        }

        private void UnsubscribeNetworking()
        {
            if (Engine.Networking is not BaseNetworkingManager net)
                return;

            net.HumanoidPoseFrameReceived -= OnHumanoidPoseFrame;
        }

        private void OnHumanoidPoseFrame(HumanoidPoseFrame frame)
        {
            ReadOnlySpan<byte> payload = frame.Payload;
            int offset = 0;

            for (int i = 0; i < frame.AvatarCount && offset < payload.Length; i++)
            {
                if (payload.Length - offset < 6)
                    break;

                HumanoidPoseFlags flags = (HumanoidPoseFlags)BitConverter.ToUInt16(payload[(offset + 2)..]);
                bool isBaseline = flags.HasFlag(HumanoidPoseFlags.Baseline);

                bool parsed;
                QuantizedHumanoidPose pose = default;
                HumanoidPoseAvatarHeader header = default;
                int consumed = 0;

                if (isBaseline)
                    parsed = HumanoidPoseCodec.TryReadBaselineAvatar(payload[offset..], out header, out pose, out consumed);
                else if (_receivedBaselines.TryGetValue(BitConverter.ToUInt16(payload[offset..]), out var baseline) && baseline.sequence == frame.BaselineSequence)
                    parsed = HumanoidPoseCodec.TryReadDeltaAvatar(payload[offset..], baseline.pose, out header, out pose, out consumed, _delta);
                else
                    parsed = false;

                if (!parsed || consumed <= 0)
                    break;

                offset += consumed;

                if (isBaseline)
                    _receivedBaselines[header.EntityId] = (pose, frame.BaselineSequence);

                if (header.EntityId == PoseEntityId)
                    ApplyPose(pose);
            }
        }

        private void Register()
        {
            if (PoseEntityId == 0)
                return;

            _registry[PoseEntityId] = this;
        }

        private void Unregister()
        {
            if (PoseEntityId == 0)
                return;

            _registry.Remove(PoseEntityId);
        }

        private static float GetYawRadians(Quaternion q)
        {
            // Extract yaw from quaternion (rotation around Y).
            float siny_cosp = 2f * (q.W * q.Y + q.Z * q.X);
            float cosy_cosp = 1f - 2f * (q.Y * q.Y + q.Z * q.Z);
            return MathF.Atan2(siny_cosp, cosy_cosp);
        }
    }
}
