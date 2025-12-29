using System;
using System.Collections.Generic;
using System.Numerics;
using XREngine.Core.Attributes;
using XREngine.Networking;
using XREngine.Scene.Transforms;

namespace XREngine.Components.Animation
{
    /// <summary>
    /// Component that uses the VRIK solver to solve IK for a humanoid character controlled by a VR headset, controllers, and optional trackers.
    /// </summary>
    [RequireComponents(typeof(HumanoidComponent))]
    public class VRIKSolverComponent : IKSolverComponent
    {
        private const double BaselineIntervalSeconds = 1.0;
        private static readonly Dictionary<ushort, (QuantizedHumanoidPose pose, ushort sequence)> _receivedBaselines = new();
        private static readonly Dictionary<ushort, VRIKSolverComponent> _registry = new();

        public IKSolverVR Solver { get; } = new();
        public HumanoidComponent Humanoid => GetSiblingComponent<HumanoidComponent>(true)!;
        public Transform? Root => Humanoid?.SceneNode?.GetTransformAs<Transform>(true);

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
        private double _lastBaselineTime;
        private HumanoidQuantizationSettings _quantization = HumanoidQuantizationSettings.Default;
        private HumanoidPoseDeltaSettings _delta = HumanoidPoseDeltaSettings.Default;

        public override void Visualize()
        {
            using var profilerState = Engine.Profiler.Start("VRIKSolverComponent.Visualize");
            Solver.Visualize();
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
            base.InitializeSolver();
        }

        protected internal override void OnComponentActivated()
        {
            base.OnComponentActivated();
            if (PoseEntityId == 0)
                PoseEntityId = PoseIdFromSceneNode();

            Register();
            SubscribeNetworking();
        }

        protected internal override void OnComponentDeactivated()
        {
            base.OnComponentDeactivated();
            Unregister();
            UnsubscribeNetworking();
        }

        protected override void UpdateSolver()
        {
            if (!(Humanoid?.SceneNode?.IsTransformNull ?? true) && Humanoid.SceneNode.Transform.LossyWorldScale.LengthSquared() < float.Epsilon)
            {
                Debug.LogWarning("VRIK Root Transform's scale is zero, can not update VRIK. Make sure you have not calibrated the character to a zero scale.");
                IsActive = false;
                return;
            }

            base.UpdateSolver();
            TrySendPose();
        }

        private void TrySendPose()
        {
            if (!PoseBroadcastEnabled)
                return;

            if (Engine.Networking is not Engine.BaseNetworkingManager net)
                return;

            if (Root is null || Humanoid is null)
                return;

            HumanoidPoseSample sample = CapturePose();
            QuantizedHumanoidPose quantized = HumanoidPoseCodec.Quantize(sample, _quantization);

            HumanoidPosePacketBuilder builder = new(_quantization, _delta);

            double now = Engine.ElapsedTime;
            bool sendBaseline = _baselinePose is null || now - _lastBaselineTime >= BaselineIntervalSeconds;
            if (sendBaseline)
            {
                _baselineSequence++;
                _baselinePose = quantized;
                _lastBaselineTime = now;

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

            Vector3 hip = GetLocalTracker(Humanoid.HipsTarget, rootPos, rootYaw);
            Vector3 head = GetLocalTracker(Humanoid.HeadTarget, rootPos, rootYaw);
            Vector3 leftHand = GetLocalTracker(Humanoid.LeftHandTarget, rootPos, rootYaw);
            Vector3 rightHand = GetLocalTracker(Humanoid.RightHandTarget, rootPos, rootYaw);
            Vector3 leftFoot = GetLocalTracker(Humanoid.LeftFootTarget, rootPos, rootYaw);
            Vector3 rightFoot = GetLocalTracker(Humanoid.RightFootTarget, rootPos, rootYaw);

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

        private Vector3 GetLocalTracker((TransformBase? tfm, Matrix4x4 offset) target, Vector3 rootPos, float rootYaw)
        {
            if (target.tfm is null)
                return Vector3.Zero;

            Matrix4x4 world = HumanoidComponent.GetMatrixForTarget(target);
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
            SetTransform(Humanoid.HipsTarget.tfm as Transform, sample.RootPosition + Vector3.Transform(sample.Hip, yawRot));
            SetTransform(Humanoid.HeadTarget.tfm as Transform, sample.RootPosition + Vector3.Transform(sample.Head, yawRot));
            SetTransform(Humanoid.LeftHandTarget.tfm as Transform, sample.RootPosition + Vector3.Transform(sample.LeftHand, yawRot));
            SetTransform(Humanoid.RightHandTarget.tfm as Transform, sample.RootPosition + Vector3.Transform(sample.RightHand, yawRot));
            SetTransform(Humanoid.LeftFootTarget.tfm as Transform, sample.RootPosition + Vector3.Transform(sample.LeftFoot, yawRot));
            SetTransform(Humanoid.RightFootTarget.tfm as Transform, sample.RootPosition + Vector3.Transform(sample.RightFoot, yawRot));
        }

        private static void SetTransform(TransformBase? tfm, Vector3 position, Quaternion? rotation = null)
        {
            if (tfm is null)
                return;

            if (tfm is Transform concrete)
            {
                concrete.TargetTranslation = position;
                if (rotation.HasValue)
                    concrete.TargetRotation = rotation.Value;
            }
            else
            {
                // TransformBase doesn't have TargetTranslation/TargetRotation, set via world matrix
                Matrix4x4 matrix = rotation.HasValue
                    ? Matrix4x4.CreateFromQuaternion(rotation.Value) * Matrix4x4.CreateTranslation(position)
                    : Matrix4x4.CreateTranslation(position);
                tfm.DeriveWorldMatrix(matrix);
            }
        }

        private ushort PoseIdFromSceneNode()
        {
            Guid guid = SceneNode?.ID ?? Guid.NewGuid();
            return BitConverter.ToUInt16(guid.ToByteArray(), 0);
        }

        private void SubscribeNetworking()
        {
            if (Engine.Networking is not Engine.BaseNetworkingManager net)
                return;

            net.HumanoidPoseFrameReceived += OnHumanoidPoseFrame;
        }

        private void UnsubscribeNetworking()
        {
            if (Engine.Networking is not Engine.BaseNetworkingManager net)
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
