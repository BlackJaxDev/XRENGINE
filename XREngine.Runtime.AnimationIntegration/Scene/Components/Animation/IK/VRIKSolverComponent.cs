using System;
using System.Collections.Generic;
using System.Numerics;
using System.Buffers.Binary;
using XREngine.Core.Attributes;
using XREngine.Networking;
using XREngine.Scene.Transforms;

namespace XREngine.Components.Animation
{
    /// <summary>
    /// Component that uses the VRIK solver to solve IK for a humanoid character controlled by a VR headset, controllers, and optional trackers.
    /// </summary>
    [RequireComponents(typeof(HumanoidComponent))]
    public class VRIKSolverComponent : IKSolverComponent, IVRIKSolverHandle
    {
        private const double BaselineIntervalSeconds = 1.0;
        private static readonly long BaselineIntervalTicks = Math.Max(1L, (long)Math.Round(BaselineIntervalSeconds * System.Diagnostics.Stopwatch.Frequency));

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
            set => SetField(ref _poseEntityId, value);
        }

        /// <summary>
        /// When true, the component will publish VR humanoid pose frames over the networking manager.
        /// </summary>
        public bool PoseBroadcastEnabled { get; set; } = true;

        /// <summary>
        /// When true, incoming pose frames that match <see cref="PoseEntityId"/> will drive this solver's targets.
        /// </summary>
        public bool PoseReceiveEnabled { get; set; } = true;

        /// <summary>Managed identity bound to this remote avatar's pose stream.</summary>
        public Guid BoundPoseSessionId { get; private set; }

        /// <summary>Managed client identity bound to this remote avatar's pose stream.</summary>
        public string? BoundPoseClientId { get; private set; }

        /// <summary>Last validated managed pose frame applied by this remote solver.</summary>
        public uint LastAppliedNetworkPoseFrameSequence => _lastReceivedFrameSequence;

        /// <summary>Number of validated managed pose frames applied by this solver.</summary>
        public long AppliedNetworkPoseFrameCount => _appliedNetworkPoseFrameCount;

        private ushort _poseEntityId;
        private QuantizedHumanoidPose? _baselinePose;
        private QuantizedHumanoidPose? _receivedBaselinePose;
        private readonly Dictionary<ushort, (QuantizedHumanoidPose Pose, ushort Sequence)> _legacyReceivedBaselines = [];
        private ushort _receivedBaselineSequence;
        private uint _lastReceivedFrameSequence;
        private long _appliedNetworkPoseFrameCount;
        private ushort _baselineSequence;
        private long _lastBaselineTicks;

        internal static bool ShouldSendBaseline(bool baselineMissing, long nowTicks, long lastBaselineTicks)
            => baselineMissing || Math.Max(0L, nowTicks - lastBaselineTicks) >= BaselineIntervalTicks;
        private HumanoidQuantizationSettings _quantization = HumanoidQuantizationSettings.Default;
        private HumanoidPoseDeltaSettings _delta = HumanoidPoseDeltaSettings.Default;

        public override void Visualize()
        {
            using var profilerState = RuntimeAnimationHostServices.Current.StartProfileScope("VRIKSolverComponent.Visualize");
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

            SubscribeNetworking();
        }

        protected override void OnComponentDeactivated()
        {
            base.OnComponentDeactivated();
            UnsubscribeNetworking();
            ClearReceivedPoseState();
            BoundPoseSessionId = Guid.Empty;
            BoundPoseClientId = null;
        }

        protected override void OnDestroying()
        {
            UnsubscribeNetworking();
            ClearReceivedPoseState();
            BoundPoseSessionId = Guid.Empty;
            BoundPoseClientId = null;
            base.OnDestroying();
        }

        /// <summary>
        /// Binds this instance to one server-authorized remote avatar. Binding is
        /// intentionally per solver so poses cannot bleed across worlds or clients.
        /// </summary>
        public void BindNetworkPose(Guid sessionId, string clientId, ushort entityId)
        {
            if (sessionId == Guid.Empty)
                throw new ArgumentOutOfRangeException(nameof(sessionId));
            if (string.IsNullOrWhiteSpace(clientId))
                throw new ArgumentException("A remote pose requires a client identity.", nameof(clientId));
            if (entityId == 0)
                throw new ArgumentOutOfRangeException(nameof(entityId));

            if (BoundPoseSessionId == sessionId
                && string.Equals(BoundPoseClientId, clientId, StringComparison.Ordinal)
                && PoseEntityId == entityId)
            {
                PoseBroadcastEnabled = false;
                PoseReceiveEnabled = true;
                return;
            }

            BoundPoseSessionId = sessionId;
            BoundPoseClientId = clientId;
            PoseEntityId = entityId;
            PoseBroadcastEnabled = false;
            PoseReceiveEnabled = true;
            ClearReceivedPoseState();
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

            if (!RuntimeAnimationHostServices.Current.HumanoidPoseTransportAvailable)
                return;

            if (Root is null || Humanoid is null)
                return;

            HumanoidPoseSample sample = CapturePose();
            QuantizedHumanoidPose quantized = HumanoidPoseCodec.Quantize(sample, _quantization);

            HumanoidPosePacketBuilder builder = new(_quantization, _delta);

            long nowTicks = RuntimeAnimationHostServices.Current.ElapsedTicks;
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
            RuntimeAnimationHostServices.Current.BroadcastHumanoidPoseFrame(frame, compress: false);
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
            if (TryGetUShortEnv(XREngineEnvironmentVariables.PoseEntityId, out ushort poseEntityId) && poseEntityId > 0)
            {
                PoseEntityId = poseEntityId;
                Debug.Out($"VRIK pose entity id overridden to {poseEntityId} via XRE_POSE_ENTITY_ID.");
            }

            if (TryGetBoolEnv(XREngineEnvironmentVariables.PoseBroadcastEnabled, out bool poseBroadcastEnabled))
            {
                PoseBroadcastEnabled = poseBroadcastEnabled;
                Debug.Out($"VRIK pose broadcast overridden to {poseBroadcastEnabled} via XRE_POSE_BROADCAST_ENABLED.");
            }

            if (TryGetBoolEnv(XREngineEnvironmentVariables.PoseReceiveEnabled, out bool poseReceiveEnabled))
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
            => RuntimeAnimationHostServices.Current.HumanoidPoseFrameReceived += OnHumanoidPoseFrame;

        private void UnsubscribeNetworking()
            => RuntimeAnimationHostServices.Current.HumanoidPoseFrameReceived -= OnHumanoidPoseFrame;

        private void OnHumanoidPoseFrame(HumanoidPoseFrame frame)
        {
            if (!PoseReceiveEnabled)
                return;

            if (BoundPoseSessionId == Guid.Empty)
            {
                ApplyLegacyPoseFrame(frame);
                return;
            }

            if (string.IsNullOrWhiteSpace(BoundPoseClientId)
                || frame.SessionId != BoundPoseSessionId
                || !string.Equals(frame.SourceClientId, BoundPoseClientId, StringComparison.Ordinal)
                || frame.AvatarCount != 1
                || frame.FrameSequence == 0
                || frame.FrameSequence <= _lastReceivedFrameSequence)
            {
                return;
            }

            bool parsed = frame.Kind switch
            {
                HumanoidPosePacketKind.Baseline => TryApplyReceivedBaseline(frame),
                HumanoidPosePacketKind.Delta => TryApplyReceivedDelta(frame),
                _ => false,
            };
            if (parsed)
            {
                _lastReceivedFrameSequence = frame.FrameSequence;
                _appliedNetworkPoseFrameCount++;
            }
        }

        private void ApplyLegacyPoseFrame(HumanoidPoseFrame frame)
        {
            ReadOnlySpan<byte> payload = frame.Payload;
            int offset = 0;
            for (int avatar = 0; avatar < frame.AvatarCount; avatar++)
            {
                if (payload.Length - offset < 6)
                    return;

                ushort entityId = BinaryPrimitives.ReadUInt16LittleEndian(payload[offset..]);
                HumanoidPoseFlags flags = (HumanoidPoseFlags)BinaryPrimitives.ReadUInt16LittleEndian(payload[(offset + 2)..]);
                bool isBaseline = flags.HasFlag(HumanoidPoseFlags.Baseline);
                bool parsed;
                QuantizedHumanoidPose pose;
                HumanoidPoseAvatarHeader header;
                int consumed;
                if (isBaseline)
                    parsed = HumanoidPoseCodec.TryReadBaselineAvatar(payload[offset..], out header, out pose, out consumed);
                else if (_legacyReceivedBaselines.TryGetValue(entityId, out var baseline) && baseline.Sequence == frame.BaselineSequence)
                    parsed = HumanoidPoseCodec.TryReadDeltaAvatar(payload[offset..], baseline.Pose, out header, out pose, out consumed, _delta);
                else
                    return;

                if (!parsed || consumed <= 0 || offset + consumed > payload.Length)
                    return;

                offset += consumed;
                if (isBaseline)
                {
                    _legacyReceivedBaselines[entityId] = (pose, header.Sequence);
                    if (entityId == PoseEntityId)
                    {
                        _receivedBaselinePose = pose;
                        _receivedBaselineSequence = header.Sequence;
                    }
                }

                if (entityId == PoseEntityId)
                    ApplyPose(pose);
            }
        }

        private bool TryApplyReceivedBaseline(HumanoidPoseFrame frame)
        {
            if (!HumanoidPoseCodec.TryReadBaselineAvatar(frame.Payload, out HumanoidPoseAvatarHeader header, out QuantizedHumanoidPose pose, out int consumed)
                || consumed != frame.Payload.Length
                || header.EntityId != PoseEntityId
                || !header.Flags.HasFlag(HumanoidPoseFlags.Baseline)
                || header.Sequence == 0
                || header.Sequence != frame.BaselineSequence)
            {
                return false;
            }

            _receivedBaselinePose = pose;
            _receivedBaselineSequence = header.Sequence;
            ApplyPose(pose);
            return true;
        }

        private bool TryApplyReceivedDelta(HumanoidPoseFrame frame)
        {
            if (_receivedBaselinePose is not { } baseline || frame.BaselineSequence == 0 || frame.BaselineSequence != _receivedBaselineSequence)
                return false;

            if (!HumanoidPoseCodec.TryReadDeltaAvatar(frame.Payload, baseline, out HumanoidPoseAvatarHeader header, out QuantizedHumanoidPose pose, out int consumed, _delta)
                || consumed != frame.Payload.Length
                || header.EntityId != PoseEntityId
                || header.Flags.HasFlag(HumanoidPoseFlags.Baseline))
            {
                return false;
            }

            ApplyPose(pose);
            return true;
        }

        private void ClearReceivedPoseState()
        {
            _receivedBaselinePose = null;
            _legacyReceivedBaselines.Clear();
            _receivedBaselineSequence = 0;
            _lastReceivedFrameSequence = 0;
            _appliedNetworkPoseFrameCount = 0;
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
