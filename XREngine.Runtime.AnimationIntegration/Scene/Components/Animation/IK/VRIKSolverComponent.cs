using System;
using System.Collections.Generic;
using System.Numerics;
using System.Buffers.Binary;
using XREngine.Core.Attributes;
using XREngine.Networking;
using XREngine.Components;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.Components.Animation
{
    /// <summary>
    /// Component that uses the VRIK solver to solve IK for a humanoid character controlled by a VR headset, controllers, and optional trackers.
    /// </summary>
    [RequireComponents(typeof(HumanoidComponent))]
    public partial class VRIKSolverComponent : IKSolverComponent, IVRIKSolverHandle
    {
        private const double BaselineIntervalSeconds = 1.0;
        private static readonly long BaselineIntervalTicks = Math.Max(1L, (long)Math.Round(BaselineIntervalSeconds * System.Diagnostics.Stopwatch.Frequency));

        public IKSolverVR Solver { get; } = new();
        private EHumanoidPosePreviewMode? _previewModeBeforeCalibration;
        private readonly List<(XRComponent Component, bool WasActive, EHumanoidRootMotionApplicationMode RootMode)> _suspendedAnimationWriters = [];

        /// <summary>Display the avatar's measured T-pose with its eyes at the headset and its body facing headset yaw.</summary>
        public VrCalibrationResult ApplyCanonicalCalibrationPose(Matrix4x4 headWorld, Vector3 eyeOffsetFromHead = default)
        {
            if (!VrCalibrationMath.TryGetRigidPose(headWorld, out _)
                || !Matrix4x4.Decompose(headWorld, out _, out Quaternion headRotation, out Vector3 headPosition)
                || !float.IsFinite(eyeOffsetFromHead.X) || !float.IsFinite(eyeOffsetFromHead.Y)
                || !float.IsFinite(eyeOffsetFromHead.Z) || Root is null)
                return VrCalibrationResult.Failed("The headset pose is invalid.");

            HumanoidComponent human = Humanoid;
            SuspendCalibrationAnimationWriters();
            human.PosePreviewMode = EHumanoidPosePreviewMode.TPose;
            human.ApplyVrCanonicalTPose();

            Vector3 forward = Vector3.Transform(-Vector3.UnitZ, headRotation);
            forward.Y = 0f;
            if (forward.LengthSquared() < 1e-8f)
            {
                forward = Root.WorldForward;
                forward.Y = 0f;
                if (forward.LengthSquared() < 1e-8f)
                    forward = -Vector3.UnitZ;
            }
            forward = Vector3.Normalize(forward);
            float yaw = MathF.Atan2(-forward.X, -forward.Z);
            if (!human.TryGetVrBindBodyToEngine(out Matrix4x4 bindBodyToEngine))
                return VrCalibrationResult.Failed("The avatar body facing basis is unavailable.");
            Matrix4x4 bodyOrientation = bindBodyToEngine * Matrix4x4.CreateRotationY(yaw);
            Quaternion bodyRotation = Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(bodyOrientation));
            Root.SetWorldTranslationRotation(Root.WorldTranslation, bodyRotation);
            Root.RecalculateMatrices(true);
            bool hasMeasuredEyeOffset = eyeOffsetFromHead.LengthSquared() > 1e-10f;
            TransformBase eyes = hasMeasuredEyeOffset
                ? human.Head.Node?.Transform ?? Root
                : human.EyesTarget.Node?.Transform ?? human.Head.Node?.Transform ?? Root;
            eyes.RecalculateMatrices(true);
            Vector3 eyesPosition = eyes.WorldTranslation;
            if (hasMeasuredEyeOffset)
                eyesPosition += Vector3.Transform(eyeOffsetFromHead, bodyRotation);
            Root.SetWorldTranslationRotation(Root.WorldTranslation + headPosition - eyesPosition, bodyRotation);
            Root.RecalculateMatrices(true);
            return VrCalibrationResult.Completed();
        }

        /// <summary>Stops animation components from overwriting the displayed calibration pose.</summary>
        public void SuspendCalibrationAnimationWriters()
        {
            if (_previewModeBeforeCalibration is not null)
                return;
            _previewModeBeforeCalibration = Humanoid.PosePreviewMode;
            SuspendAnimationWriters(Humanoid.SceneNode);
        }

        /// <summary>Restore the animation preview state after capture or cancellation.</summary>
        public void EndCalibrationPose()
        {
            if (_previewModeBeforeCalibration is not EHumanoidPosePreviewMode previous)
                return;
            Humanoid.PosePreviewMode = previous;
            _previewModeBeforeCalibration = null;
            for (int i = 0; i < _suspendedAnimationWriters.Count; i++)
            {
                var (component, wasActive, rootMode) = _suspendedAnimationWriters[i];
                if (component is AnimStateMachineComponent stateMachine)
                    stateMachine.RootMotionApplicationMode = rootMode;
                else if (component is AnimationClipComponent clip)
                    clip.RootMotionApplicationMode = rootMode;
                component.IsActive = wasActive;
            }
            _suspendedAnimationWriters.Clear();
        }

        private void SuspendAnimationWriters(SceneNode node)
        {
            foreach (XRComponent component in node.GetComponents<XRComponent>())
            {
                EHumanoidRootMotionApplicationMode rootMode;
                if (component is AnimStateMachineComponent stateMachine)
                    rootMode = stateMachine.RootMotionApplicationMode;
                else if (component is AnimationClipComponent clip)
                    rootMode = clip.RootMotionApplicationMode;
                else
                    continue;
                _suspendedAnimationWriters.Add((component, component.IsActive, rootMode));
                component.IsActive = false;
            }

            foreach (TransformBase child in node.Transform.Children)
                if (child.SceneNode is SceneNode childNode)
                    SuspendAnimationWriters(childNode);
        }


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

        /// <summary>Stack capacity for one outgoing avatar record. A baseline record is the largest form.</summary>
        private const int OutgoingPosePayloadCapacity = 128;

        private ushort _poseEntityId;
        private FixedQuantizedHumanoidPose _baselinePose;
        private bool _hasBaselinePose;
        private FixedQuantizedHumanoidPose _receivedBaselinePose;
        private bool _hasReceivedBaselinePose;
        /// <summary>
        /// Baselines for every avatar in a multi-avatar stream. A solver that is not bound to one
        /// remote identity needs them to walk past the other avatars' delta records.
        /// </summary>
        private readonly Dictionary<ushort, (FixedQuantizedHumanoidPose Pose, ushort Sequence)> _unboundReceivedBaselines = [];
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
            ReleaseCalibrationTargets();
            SyncSolverTargets();
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
            ReleaseCalibrationTargets();
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
            if (_calibrationTargets.Count > 0)
                UpdateTrackingWeights(RuntimeAnimationHostServices.Current.DilatedUpdateDeltaSeconds);
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
            FixedQuantizedHumanoidPose quantized = HumanoidPoseCodec.QuantizeFixed(sample, _quantization);

            // The avatar record is written on the stack and handed to the transport as a span, so
            // sending a pose every solver update performs no heap allocation.
            Span<byte> payload = stackalloc byte[OutgoingPosePayloadCapacity];
            HumanoidPoseSpanPacketWriter writer = new(payload, _quantization, _delta);

            long nowTicks = RuntimeAnimationHostServices.Current.ElapsedTicks;
            bool sendBaseline = ShouldSendBaseline(!_hasBaselinePose, nowTicks, _lastBaselineTicks);
            if (sendBaseline)
            {
                ushort baselineSequence = (ushort)(_baselineSequence + 1);
                writer.BeginFrame(HumanoidPosePacketKind.Baseline, baselineSequence);
                if (!writer.TryAddBaselineAvatar(PoseEntityId, quantized))
                    return;

                _baselineSequence = baselineSequence;
                _baselinePose = quantized;
                _hasBaselinePose = true;
                _lastBaselineTicks = nowTicks;
            }
            else
            {
                writer.BeginFrame(HumanoidPosePacketKind.Delta, _baselineSequence);
                if (!writer.TryAddDeltaAvatar(PoseEntityId, quantized, _baselinePose))
                    return;
            }

            RuntimeAnimationHostServices.Current.BroadcastHumanoidPose(
                writer.Kind,
                writer.BaselineSequence,
                writer.AvatarCount,
                payload[..writer.BytesWritten]);
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

        private void ApplyPose(in FixedQuantizedHumanoidPose pose)
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
            Solver.Spine.HeadTarget = ResolveCalibrationTarget(EHumanoidIKTarget.Head);
            Solver.Spine.HipsTarget = ResolveCalibrationTarget(EHumanoidIKTarget.Hips);
            Solver.LeftArm.Target = ResolveCalibrationTarget(EHumanoidIKTarget.LeftHand);
            Solver.RightArm.Target = ResolveCalibrationTarget(EHumanoidIKTarget.RightHand);
            Solver.LeftLeg.Target = ResolveCalibrationTarget(EHumanoidIKTarget.LeftFoot);
            Solver.RightLeg.Target = ResolveCalibrationTarget(EHumanoidIKTarget.RightFoot);
            Solver.LeftArm.UpperArmTarget = ResolveCalibrationTarget(EHumanoidIKTarget.LeftElbow);
            Solver.RightArm.UpperArmTarget = ResolveCalibrationTarget(EHumanoidIKTarget.RightElbow);
            Solver.LeftLeg.KneeTarget = ResolveCalibrationTarget(EHumanoidIKTarget.LeftKnee);
            Solver.RightLeg.KneeTarget = ResolveCalibrationTarget(EHumanoidIKTarget.RightKnee);
            Solver.Spine.ChestTarget = ResolveCalibrationTarget(EHumanoidIKTarget.Chest);
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
            => RuntimeAnimationHostServices.Current.HumanoidPosePacketReceived += OnHumanoidPosePacket;

        private void UnsubscribeNetworking()
            => RuntimeAnimationHostServices.Current.HumanoidPosePacketReceived -= OnHumanoidPosePacket;

        /// <summary>
        /// Applies a received pose packet. The packet is read in place through spans and the fixed
        /// quantized pose struct, so receiving a pose allocates nothing.
        /// </summary>
        private void OnHumanoidPosePacket(in HumanoidPosePacketView packet)
        {
            if (!PoseReceiveEnabled)
                return;

            if (BoundPoseSessionId == Guid.Empty)
            {
                ApplyUnboundPosePacket(packet);
                return;
            }

            HumanoidPosePacketHeader header = packet.Header;
            if (string.IsNullOrWhiteSpace(BoundPoseClientId)
                || header.SessionId != BoundPoseSessionId
                || !packet.SourceClientIdEquals(BoundPoseClientId)
                || header.AvatarCount != 1
                || header.FrameSequence == 0
                || header.FrameSequence <= _lastReceivedFrameSequence)
            {
                return;
            }

            bool parsed = header.Kind switch
            {
                HumanoidPosePacketKind.Baseline => TryApplyReceivedBaseline(packet),
                HumanoidPosePacketKind.Delta => TryApplyReceivedDelta(packet),
                _ => false,
            };
            if (parsed)
            {
                _lastReceivedFrameSequence = header.FrameSequence;
                _appliedNetworkPoseFrameCount++;
            }
        }

        /// <summary>
        /// Applies a packet to a solver that is not bound to one remote identity. The stream may
        /// carry several avatars, so every record is walked and only the one matching
        /// <see cref="PoseEntityId"/> drives this solver.
        /// </summary>
        private void ApplyUnboundPosePacket(in HumanoidPosePacketView packet)
        {
            ReadOnlySpan<byte> payload = packet.Payload;
            ushort frameBaselineSequence = packet.Header.BaselineSequence;
            int avatarCount = packet.Header.AvatarCount;
            int offset = 0;
            for (int avatar = 0; avatar < avatarCount; avatar++)
            {
                if (payload.Length - offset < 6)
                    return;

                ushort entityId = BinaryPrimitives.ReadUInt16LittleEndian(payload[offset..]);
                HumanoidPoseFlags flags = (HumanoidPoseFlags)BinaryPrimitives.ReadUInt16LittleEndian(payload[(offset + 2)..]);
                bool isBaseline = flags.HasFlag(HumanoidPoseFlags.Baseline);
                bool parsed;
                FixedQuantizedHumanoidPose pose;
                HumanoidPoseAvatarHeader header;
                int consumed;
                if (isBaseline)
                    parsed = HumanoidPoseCodec.TryReadBaselineAvatarFixed(payload[offset..], out header, out pose, out consumed);
                else if (_unboundReceivedBaselines.TryGetValue(entityId, out var baseline) && baseline.Sequence == frameBaselineSequence)
                    parsed = HumanoidPoseCodec.TryReadDeltaAvatar(payload[offset..], baseline.Pose, out header, out pose, out consumed, _delta);
                else
                    return;

                if (!parsed || consumed <= 0 || offset + consumed > payload.Length)
                    return;

                offset += consumed;
                if (isBaseline)
                {
                    _unboundReceivedBaselines[entityId] = (pose, header.Sequence);
                    if (entityId == PoseEntityId)
                    {
                        _receivedBaselinePose = pose;
                        _hasReceivedBaselinePose = true;
                        _receivedBaselineSequence = header.Sequence;
                    }
                }

                if (entityId == PoseEntityId)
                    ApplyPose(pose);
            }
        }

        private bool TryApplyReceivedBaseline(in HumanoidPosePacketView packet)
        {
            ReadOnlySpan<byte> payload = packet.Payload;
            if (!HumanoidPoseCodec.TryReadBaselineAvatarFixed(payload, out HumanoidPoseAvatarHeader header, out FixedQuantizedHumanoidPose pose, out int consumed)
                || consumed != payload.Length
                || header.EntityId != PoseEntityId
                || !header.Flags.HasFlag(HumanoidPoseFlags.Baseline)
                || header.Sequence == 0
                || header.Sequence != packet.Header.BaselineSequence)
            {
                return false;
            }

            _receivedBaselinePose = pose;
            _hasReceivedBaselinePose = true;
            _receivedBaselineSequence = header.Sequence;
            ApplyPose(pose);
            return true;
        }

        private bool TryApplyReceivedDelta(in HumanoidPosePacketView packet)
        {
            ushort baselineSequence = packet.Header.BaselineSequence;
            if (!_hasReceivedBaselinePose || baselineSequence == 0 || baselineSequence != _receivedBaselineSequence)
                return false;

            ReadOnlySpan<byte> payload = packet.Payload;
            if (!HumanoidPoseCodec.TryReadDeltaAvatar(payload, _receivedBaselinePose, out HumanoidPoseAvatarHeader header, out FixedQuantizedHumanoidPose pose, out int consumed, _delta)
                || consumed != payload.Length
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
            _receivedBaselinePose = default;
            _hasReceivedBaselinePose = false;
            _unboundReceivedBaselines.Clear();
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
