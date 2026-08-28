using XREngine.Extensions;
using XREngine.Animation;
using XREngine.Components;
using XREngine.Components.Animation;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Numerics;
using XREngine.Animation.Importers;
using XREngine.Scene.Transforms;

namespace XREngine.Components
{
    public partial class AnimStateMachineComponent : XRComponent
    {
        private const string ParamSchemaPacketId = "SCHEMA";
        private const string ChangeIndexedPacketId = "CHANGE_INDEX";
        private const string ChangeCollisionPacketId = "CHANGE_COLLISION";
        private const string ChangeHashPacketId = "CHANGE_HASH";

        private int _lastSentSchemaVersion = -1;
        private bool _stateMachineInitialized;
        private bool _playbackCapabilitiesValid;
        private HumanoidProjectedRootPose _appliedRootMotionPose = HumanoidProjectedRootPose.Identity;
        private HumanoidProjectedRootPose _previousAppliedRootMotionPose = HumanoidProjectedRootPose.Identity;
        private HumanoidProjectedRootPose _rootMotionEpochReferencePose = HumanoidProjectedRootPose.Identity;
        private HumanoidRootMotionDelta _appliedRootMotionDelta = HumanoidRootMotionDelta.Identity;
        private Transform? _rootMotionAnchorTarget;
        private Vector3 _rootMotionAnchorTranslation;
        private Quaternion _rootMotionAnchorRotation = Quaternion.Identity;
        private ulong _rootMotionEpoch;
        private ulong _rootMotionSequence;
        private bool _hasRootMotionAnchor;
        private bool _hasPreviousAppliedRootMotionPose;
        private bool _hasRootMotionEpochReference;
        private bool _rebaseRootMotionFromNextPose;

        private AnimStateMachine _stateMachine = new();
        public AnimStateMachine StateMachine
        {
            get => _stateMachine;
            set => SetField(ref _stateMachine, value);
        }

        private HumanoidComponent? _humanoid;
        public HumanoidComponent? Humanoid
        {
            get => _humanoid;
            set => SetField(ref _humanoid, value);
        }

        private EHumanoidRootMotionApplicationMode _rootMotionApplicationMode;
        public EHumanoidRootMotionApplicationMode RootMotionApplicationMode
        {
            get => _rootMotionApplicationMode;
            set => SetField(ref _rootMotionApplicationMode, value);
        }

        private float _speed = 1.0f;
        /// <summary>
        /// Signed state-machine playback rate. Negative values evaluate active motions in reverse.
        /// </summary>
        public float Speed
        {
            get => _speed;
            set => SetField(ref _speed, float.IsFinite(value) ? value : 1.0f);
        }

        private Transform? _rootMotionTarget;
        public Transform? RootMotionTarget
        {
            get => _rootMotionTarget;
            set => SetField(ref _rootMotionTarget, value);
        }

        public ulong RootMotionEpoch => _rootMotionEpoch;
        public ulong RootMotionSequence => _rootMotionSequence;
        public long RootMotionLoopCycle => _dominantRootMotionLoopCycle;
        public HumanoidProjectedRootPose AppliedRootMotionPose => _appliedRootMotionPose;
        public HumanoidRootMotionDelta AppliedRootMotionDelta => _appliedRootMotionDelta;

        public event Action<HumanoidProjectedRootPose, HumanoidRootMotionDelta>? RootMotionEvaluated;

        /// <summary>Raised after an imported AnimationEvent is dispatched to scene components.</summary>
        public event Action<ImportedAnimationEventOccurrence>? ImportedAnimationEventTriggered;

        private bool _suspendedByClip;
        public bool SuspendedByClip
        {
            get => _suspendedByClip;
            private set => SetField(ref _suspendedByClip, value);
        }

        private string _playbackCapabilityDiagnostic = string.Empty;
        /// <summary>
        /// Last source-capability or avatar-definition reason that prevented
        /// state-machine evaluation.
        /// </summary>
        public string PlaybackCapabilityDiagnostic
        {
            get => _playbackCapabilityDiagnostic;
            private set => SetField(ref _playbackCapabilityDiagnostic, value);
        }

        public void SetSuspendedByClip(bool suspended)
        {
            if (SuspendedByClip == suspended)
                return;

            SuspendedByClip = suspended;

            if (!IsActiveInHierarchy)
                return;

            if (suspended)
            {
                UnregisterTick(ETickGroup.Normal, ETickOrder.Animation, EvaluationTick);
                UnregisterTick(ETickGroup.Late, ETickOrder.Input, PublishRootMotion);
                EndRootMotionEpoch();
            }
            else
            {
                if (_playbackCapabilitiesValid)
                {
                    RegisterTick(ETickGroup.Normal, ETickOrder.Animation, EvaluationTick);
                    RegisterTick(ETickGroup.Late, ETickOrder.Input, PublishRootMotion);
                    BeginRootMotionEpoch(rebaseFromNextPose: true);
                }
            }
        }

        private HumanoidComponent? GetHumanoidComponent()
            => Humanoid ?? (TryGetSiblingComponent<HumanoidComponent>(out var humanoid) ? humanoid : null);

        protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
        {
            base.OnPropertyChanged(propName, prev, field);

            // Detached scene graphs never become active-in-hierarchy, but explicitly disabling
            // an animation owner must still release the pose it authored.
            switch (propName)
            {
                case nameof(StateMachine):
                    ResetImportedAnimationBindings();
                    _playbackCapabilitiesValid = false;
                    break;
                case nameof(IsActive) when field is bool isActive && !isActive && !IsActiveInHierarchy:
                    ResetDrivenPose();
                    break;
                case nameof(RootMotionApplicationMode):
                case nameof(RootMotionTarget):
                    if (IsActiveInHierarchy)
                        BeginRootMotionEpoch(rebaseFromNextPose: true);
                    break;
            }
        }
        protected override void OnComponentActivated()
        {
            base.OnComponentActivated();
            if (!TryPrepareStateMachinePlayback())
            {
                Debug.Animation(
                    $"[AnimStateMachineComponent] Playback rejected on '{SceneNode.Name}': {PlaybackCapabilityDiagnostic}");
                return;
            }

            if (!SuspendedByClip)
                GetHumanoidComponent()?.ResetRootMotionBaseline();
            if (!SuspendedByClip)
            {
                RegisterTick(ETickGroup.Normal, ETickOrder.Animation, EvaluationTick);
                RegisterTick(ETickGroup.Late, ETickOrder.Input, PublishRootMotion);
                BeginRootMotionEpoch(rebaseFromNextPose: true);
            }
        }

        private readonly HashSet<AnimVar> _changedLastEval = [];

        private void VariableChanged(AnimVar? var)
        {
            if (var is null)
                return;

            _changedLastEval.Add(var);
        }

        protected override void OnComponentDeactivated()
        {
            base.OnComponentDeactivated();
            UnregisterTick(ETickGroup.Normal, ETickOrder.Animation, EvaluationTick);
            UnregisterTick(ETickGroup.Late, ETickOrder.Input, PublishRootMotion);
            EndRootMotionEpoch();
            if (!SuspendedByClip)
                ResetDrivenPose();
            if (_stateMachineInitialized)
            {
                StateMachine.ImportedAnimationEventTriggered -= OnImportedAnimationEventTriggered;
                StateMachine.Deinitialize();
                StateMachine.VariableChanged -= VariableChanged;
                _stateMachineInitialized = false;
            }
            _playbackCapabilitiesValid = false;
            ResetImportedAnimationBindings();
            _changedLastEval.Clear();
        }

        private void ResetDrivenPose()
        {
            var humanoid = GetHumanoidComponent();
            if (humanoid is not null)
                humanoid.ResetPose();
            else
                StateMachine.ResetAnimatedState();
        }
        protected internal void EvaluationTick()
        {
            if (SuspendedByClip || !_playbackCapabilitiesValid)
                return;

            var humanoid = GetHumanoidComponent();
            if (humanoid is not null && !humanoid.IsAnimatedPosePreviewActive)
                return;

            EvaluateAndApply(
                ScalePlaybackTicks(RuntimeAnimationHostServices.Current.UpdateDeltaTicks, Speed),
                humanoid);
        }

        /// <summary>
        /// Seeks active state motions to an exact time and atomically applies the resulting pose.
        /// Temporal root-motion continuity is intentionally reset at this discontinuity.
        /// </summary>
        public void EvaluateAtTime(float timeSeconds)
            => EvaluateAtTime(timeSeconds, dispatchEvents: false);

        /// <summary>
        /// Seeks active motions and optionally emits every event crossed by the
        /// discontinuity. Editor scrubbing uses the non-dispatching overload.
        /// </summary>
        public void EvaluateAtTime(float timeSeconds, bool dispatchEvents)
        {
            if (!_playbackCapabilitiesValid && !TryPrepareStateMachinePlayback())
                return;

            var humanoid = GetHumanoidComponent();
            if (humanoid is not null && !humanoid.IsAnimatedPosePreviewActive)
                return;

            StateMachine.SeekActiveMotions(timeSeconds, dispatchEvents);
            _observedMotionContinuityVersion = StateMachine.HumanoidMotionContinuityVersion;
            BeginRootMotionEpoch(
                preserveExistingAnchor: true,
                rebaseFromNextPose: true);
            EvaluateAndApply(0L, humanoid);
            PublishRootMotion();
        }

        /// <summary>
        /// Runs graph-wide Unity import and target-avatar validation before any
        /// state motion is initialized or evaluated.
        /// </summary>
        public bool TryValidatePlaybackCapabilities(out string diagnostic)
        {
            if (!StateMachine.TryValidateSourceImportCapabilities(
                ValidateImportedAnimationClipBindings,
                out diagnostic,
                out bool requiresHumanoidAvatar))
            {
                PlaybackCapabilityDiagnostic = diagnostic;
                _playbackCapabilitiesValid = false;
                return false;
            }

            if (requiresHumanoidAvatar)
            {
                HumanoidComponent? humanoid = GetHumanoidComponent();
                if (humanoid is null)
                {
                    diagnostic = "The state machine contains Unity humanoid data, but no HumanoidComponent owns the target avatar definition.";
                    PlaybackCapabilityDiagnostic = diagnostic;
                    _playbackCapabilitiesValid = false;
                    return false;
                }

                if (!humanoid.TryValidateAvatarDefinitionForPlayback(out diagnostic))
                {
                    PlaybackCapabilityDiagnostic = diagnostic;
                    _playbackCapabilitiesValid = false;
                    return false;
                }
            }

            PlaybackCapabilityDiagnostic = string.Empty;
            _playbackCapabilitiesValid = true;
            diagnostic = string.Empty;
            return true;
        }

        private bool TryPrepareStateMachinePlayback()
        {
            if (!TryValidatePlaybackCapabilities(out _))
                return false;
            if (_stateMachineInitialized)
                return true;

            StateMachine.Initialize(this);
            InitializeStateMachineRootMotionPipeline();
            StateMachine.VariableChanged += VariableChanged;
            StateMachine.ImportedAnimationEventTriggered += OnImportedAnimationEventTriggered;
            _stateMachineInitialized = true;
            ReplicateParameterSchema(force: true);
            return true;
        }

        private void OnImportedAnimationEventTriggered(ImportedAnimationEventOccurrence occurrence)
        {
            int receiverCount = ImportedAnimationEventDispatcher.Dispatch(this, occurrence);
            ImportedAnimationEventTriggered?.Invoke(occurrence);
            if (receiverCount == 0
                && occurrence.Event.MessageOptions == EImportedAnimationEventMessageOptions.RequireReceiver)
            {
                Debug.Animation(
                    $"[AnimationEvent] '{occurrence.Event.EventId}' from state '{occurrence.StateName}' " +
                    $"had no compatible receiver on '{SceneNode.Name}'.");
            }
        }

        private void EvaluateAndApply(long deltaTicks, HumanoidComponent? humanoid)
        {
            bool ownsImportedBodySampleTransaction = false;
            try
            {
                StateMachine.EvaluateAnimationValues(this, deltaTicks);
                if (humanoid is not null)
                {
                    // RootT/RootQ are still present in the shared typed pose store. Swallow those
                    // scalar setters atomically; the per-leaf sidecar below owns Body/root
                    // projection and composition without losing clip-local policy or time state.
                    ownsImportedBodySampleTransaction = humanoid.BeginImportedBodySampleTransaction(
                        this,
                        HumanoidImportedBodySample.Neutral,
                        hasCanonicalSample: false);
                    if (!ownsImportedBodySampleTransaction)
                    {
                        const string diagnostic =
                            "Another evaluator owns the target humanoid Body/root transaction.";
                        PlaybackCapabilityDiagnostic = diagnostic;
                        _playbackCapabilitiesValid = false;
                        Debug.Animation(
                            $"[AnimStateMachineComponent] Playback rejected on '{SceneNode.Name}': {diagnostic}");
                        return;
                    }
                }

                StateMachine.ApplyAnimationValues();

                if (ownsImportedBodySampleTransaction)
                {
                    humanoid!.CancelImportedBodySampleTransaction(this);
                    ownsImportedBodySampleTransaction = false;
                }

                if (!PrepareStateMachineRootMotionFrame(humanoid))
                {
                    _playbackCapabilitiesValid = false;
                    Debug.Animation(
                        $"[AnimStateMachineComponent] Playback rejected on '{SceneNode.Name}': {PlaybackCapabilityDiagnostic}");
                    return;
                }
            }
            catch
            {
                if (ownsImportedBodySampleTransaction)
                    humanoid!.CancelImportedBodySampleTransaction(this);
                humanoid?.ClearStateMachineRootMotionFrame(this);
                throw;
            }

            if (deltaTicks == 0L)
                humanoid?.ApplyCurrentStateMachineMusclePoseImmediately();

            // Keep schema in sync before sending any indexed changes.
            ReplicateParameterSchema(force: false);
            ReplicateModifiedVariables();
            _changedLastEval.Clear();
        }

        private static long ScalePlaybackTicks(long ticks, float speed)
        {
            if (ticks == 0L || speed == 0.0f)
                return 0L;

            double scaled = ticks * (double)speed;
            if (!double.IsFinite(scaled))
                return 0L;
            if (scaled >= long.MaxValue)
                return long.MaxValue;
            if (scaled <= long.MinValue)
                return long.MinValue;
            return (long)Math.Round(scaled);
        }

        private void PublishRootMotion()
        {
            if (TransformBase.IsDiagnosticEvaluationActive || !_hasStateMachineRootMotion)
                return;

            HumanoidComponent? humanoid = GetHumanoidComponent();
            if (humanoid is null)
                return;

            HumanoidProjectedRootPose composedPose = humanoid.CurrentProjectedRootPose;
            if (composedPose.Channels == EHumanoidProjectedRootChannels.None)
                return;

            if (_rebaseRootMotionFromNextPose)
            {
                _rootMotionEpochReferencePose = composedPose;
                _hasRootMotionEpochReference = true;
                _rebaseRootMotionFromNextPose = false;
            }

            HumanoidProjectedRootPose unwrappedPose = _hasRootMotionEpochReference
                ? HumanoidComponent.ComposeProjectedRootPoses(
                    HumanoidComponent.InvertProjectedRootPose(_rootMotionEpochReferencePose),
                    composedPose)
                : composedPose;
            _appliedRootMotionDelta = _hasPreviousAppliedRootMotionPose
                ? HumanoidComponent.CalculateProjectedRootDelta(_previousAppliedRootMotionPose, unwrappedPose)
                : HumanoidRootMotionDelta.Identity;
            _appliedRootMotionPose = unwrappedPose;
            _previousAppliedRootMotionPose = unwrappedPose;
            _hasPreviousAppliedRootMotionPose = true;
            _rootMotionSequence = unchecked(_rootMotionSequence + 1UL);

            switch (RootMotionApplicationMode)
            {
                case EHumanoidRootMotionApplicationMode.ExtractOnly:
                    return;
                case EHumanoidRootMotionApplicationMode.ExternalConsumer:
                    RootMotionEvaluated?.Invoke(unwrappedPose, _appliedRootMotionDelta);
                    return;
                case EHumanoidRootMotionApplicationMode.ApplyToExplicitTarget:
                    ApplyProjectedRootPoseToTarget(unwrappedPose);
                    return;
            }
        }

        private void ApplyProjectedRootPoseToTarget(HumanoidProjectedRootPose pose)
        {
            Transform? target = RootMotionTarget;
            if (target is null)
                return;

            if (!_hasRootMotionAnchor || !ReferenceEquals(_rootMotionAnchorTarget, target))
                CaptureRootMotionAnchor(target);

            Vector3 projectedPosition = SelectProjectedRootPosition(pose);
            Quaternion projectedRotation = (pose.Channels & EHumanoidProjectedRootChannels.RotationYaw) != 0
                && IsFiniteNonZero(pose.Rotation)
                    ? Quaternion.Normalize(pose.Rotation)
                    : Quaternion.Identity;
            target.SetLocalTranslationRotation(
                _rootMotionAnchorTranslation + Vector3.Transform(projectedPosition, _rootMotionAnchorRotation),
                Quaternion.Normalize(_rootMotionAnchorRotation * projectedRotation));
        }

        private void BeginRootMotionEpoch(
            bool preserveExistingAnchor = false,
            bool rebaseFromNextPose = false)
        {
            if (TransformBase.IsDiagnosticEvaluationActive)
                return;

            Transform? target = RootMotionApplicationMode == EHumanoidRootMotionApplicationMode.ApplyToExplicitTarget
                ? RootMotionTarget
                : null;
            bool keepAnchor = preserveExistingAnchor
                && target is not null
                && _hasRootMotionAnchor
                && ReferenceEquals(_rootMotionAnchorTarget, target);
            Vector3 anchorTranslation = _rootMotionAnchorTranslation;
            Quaternion anchorRotation = _rootMotionAnchorRotation;

            _rootMotionEpoch = unchecked(_rootMotionEpoch + 1UL);
            _rootMotionSequence = 0UL;
            _appliedRootMotionPose = HumanoidProjectedRootPose.Identity;
            _previousAppliedRootMotionPose = HumanoidProjectedRootPose.Identity;
            _appliedRootMotionDelta = HumanoidRootMotionDelta.Identity;
            _hasPreviousAppliedRootMotionPose = false;
            _rootMotionEpochReferencePose = HumanoidProjectedRootPose.Identity;
            _hasRootMotionEpochReference = false;
            _rebaseRootMotionFromNextPose = rebaseFromNextPose;
            _hasRootMotionAnchor = false;
            _rootMotionAnchorTarget = null;

            GetHumanoidComponent()?.InvalidateProjectedRootMotionBaseline(this);
            if (target is null)
                return;
            if (keepAnchor)
            {
                _rootMotionAnchorTarget = target;
                _rootMotionAnchorTranslation = anchorTranslation;
                _rootMotionAnchorRotation = anchorRotation;
                _hasRootMotionAnchor = true;
            }
            else
            {
                CaptureRootMotionAnchor(target);
            }
        }

        private void EndRootMotionEpoch()
        {
            ClearStateMachineRootMotionPipeline(GetHumanoidComponent());
            _hasRootMotionAnchor = false;
            _rootMotionAnchorTarget = null;
            _hasPreviousAppliedRootMotionPose = false;
            _rootMotionEpochReferencePose = HumanoidProjectedRootPose.Identity;
            _hasRootMotionEpochReference = false;
            _rebaseRootMotionFromNextPose = false;
            _appliedRootMotionDelta = HumanoidRootMotionDelta.Identity;
        }

        private void CaptureRootMotionAnchor(Transform target)
        {
            _rootMotionAnchorTarget = target;
            _rootMotionAnchorTranslation = target.Translation;
            _rootMotionAnchorRotation = IsFiniteNonZero(target.Rotation)
                ? Quaternion.Normalize(target.Rotation)
                : Quaternion.Identity;
            _hasRootMotionAnchor = true;
        }

        private static Vector3 SelectProjectedRootPosition(HumanoidProjectedRootPose pose)
        {
            Vector3 position = Vector3.Zero;
            if ((pose.Channels & EHumanoidProjectedRootChannels.PositionXZ) != 0)
            {
                position.X = float.IsFinite(pose.Position.X) ? pose.Position.X : 0.0f;
                position.Z = float.IsFinite(pose.Position.Z) ? pose.Position.Z : 0.0f;
            }
            if ((pose.Channels & EHumanoidProjectedRootChannels.PositionY) != 0)
                position.Y = float.IsFinite(pose.Position.Y) ? pose.Position.Y : 0.0f;
            return position;
        }

        private static bool IsFiniteNonZero(Quaternion value)
            => float.IsFinite(value.X)
            && float.IsFinite(value.Y)
            && float.IsFinite(value.Z)
            && float.IsFinite(value.W)
            && float.IsFinite(value.LengthSquared())
            && value.LengthSquared() > 1.0e-8f;

        private void ReplicateParameterSchema(bool force)
        {
            int schemaVersion = StateMachine.ParameterSchemaVersion;
            if (!force && schemaVersion == _lastSentSchemaVersion)
                return;

            var schema = StateMachine.GetOrderedParameterSchemaSnapshot();

            using var ms = new MemoryStream();
            using (var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
            {
                bw.Write(schemaVersion);
                bw.Write((ushort)schema.Count);
                for (int i = 0; i < schema.Count; i++)
                {
                    var entry = schema[i];
                    string name = entry.Name ?? string.Empty;
                    byte[] utf8 = Encoding.UTF8.GetBytes(name);
                    bw.Write((ushort)utf8.Length);
                    bw.Write(utf8);

                    bw.Write((byte)entry.Type);
                    switch (entry.Type)
                    {
                        case AnimStateMachine.AnimParameterType.Bool:
                            bw.Write(entry.BoolDefault);
                            break;
                        case AnimStateMachine.AnimParameterType.Int:
                            bw.Write(entry.IntDefault);
                            break;
                        case AnimStateMachine.AnimParameterType.Float:
                            bw.Write(entry.FloatDefault);
                            break;
                    }
                }
            }

            // The schema is needed for indexed replication; try to deliver reliably.
            EnqueueDataReplication(ParamSchemaPacketId, ms.ToArray(), compress: true, resendOnFailedAck: true);
            _lastSentSchemaVersion = schemaVersion;
        }

        private void ReplicateModifiedVariables()
        {
            int bitCount = 0;
            int indexBits = StateMachine.ParameterNameIdBitCount;
            bool canUseIndexFormat = indexBits < 16;
            bool useIndexFormat = canUseIndexFormat;

            if (useIndexFormat)
            {
                foreach (var variable in _changedLastEval)
                {
                    if (variable is null)
                        continue;
                    if (!StateMachine.TryGetParameterIndex(variable.ParameterName, out _))
                    {
                        useIndexFormat = false;
                        break;
                    }
                }
            }

            bool useHashedFormat = !StateMachine.HasAnyHashCollisions;
            foreach (var variable in _changedLastEval)
            {
                if (variable is null)
                    continue;

                if (useIndexFormat)
                {
                    bitCount += indexBits; // parameter id
                }
                else
                {
                    bitCount += 16; // hash
                    if (!useHashedFormat)
                    {
                        // Collision support: 1-bit flag + variable-length index when needed.
                        int collisionCount = StateMachine.GetNamesForHash(variable.Hash).Count;
                        bool hasCollision = collisionCount > 1;
                        bitCount += 1; // flag
                        if (hasCollision)
                            bitCount += GetCollisionIndexBitCount(collisionCount);
                    }
                }

                bitCount += variable.CalcBitCount();
            }
            if (bitCount == 0)
                return;

            byte[] data = new byte[bitCount.Align(8) / 8];
            int bitOffset = 0;
            foreach (var variable in _changedLastEval)
            {
                if (useIndexFormat)
                {
                    StateMachine.TryGetParameterIndex(variable!.ParameterName, out int paramId);
                    WriteBits(data, ref bitOffset, (uint)paramId, indexBits);
                }
                else
                {
                    ushort hash = variable!.Hash;
                    WriteBits(data, ref bitOffset, hash, 16);

                    if (!useHashedFormat)
                    {
                        int collisionCount = StateMachine.GetNamesForHash(hash).Count;
                        bool hasCollision = collisionCount > 1;
                        WriteBits(data, ref bitOffset, hasCollision ? 1u : 0u, 1);
                        if (hasCollision)
                        {
                            int collisionIndexBits = GetCollisionIndexBitCount(collisionCount);
                            int collisionIndex = GetCollisionIndex(hash, variable.ParameterName);
                            WriteBits(data, ref bitOffset, (uint)collisionIndex, collisionIndexBits);
                        }
                    }
                }
                variable?.WriteBits(data, ref bitOffset);
            }

            string packetId = useIndexFormat 
                ? ChangeIndexedPacketId
                : (useHashedFormat 
                    ? ChangeHashPacketId
                    : ChangeCollisionPacketId);
            
            EnqueueDataReplication(packetId, data, false, false);
        }

        private static int GetCollisionIndexBitCount(int collisionCount)
        {
            return AnimStateMachine.GetMinimalBitCountForCount(collisionCount);
        }

        private int GetCollisionIndex(ushort hash, string name)
        {
            var names = StateMachine.GetNamesForHash(hash);
            if (names.Count <= 1)
                return 0;

            int i = 0;
            foreach (var n in names)
            {
                if (string.Equals(n, name, StringComparison.Ordinal))
                    return i;
                i++;
            }

            return 0;
        }

        private static string? GetCollisionNameByIndex(IReadOnlyCollection<string> names, int index)
        {
            if (names.Count <= 1)
            {
                foreach (var n in names)
                    return n;
                return null;
            }

            if (index < 0)
                return null;

            int i = 0;
            foreach (var n in names)
            {
                if (i == index)
                    return n;
                i++;
            }
            return null;
        }

        private static void WriteBits(byte[] data, ref int bitOffset, uint value, int bitCount)
        {
            for (int i = 0; i < bitCount; i++)
            {
                int byteIndex = bitOffset / 8;
                int bitIndex = bitOffset % 8;
                data[byteIndex] |= (byte)(((value >> i) & 1) << bitIndex);
                bitOffset++;
            }
        }

        private static uint ReadBits(byte[] bytes, ref int bitOffset, int bitCount)
        {
            uint value = 0;
            for (int i = 0; i < bitCount; i++)
            {
                int byteIndex = bitOffset / 8;
                int bitIndex = bitOffset % 8;
                value |= (uint)(((bytes[byteIndex] >> bitIndex) & 1) << i);
                bitOffset++;
            }
            return value;
        }

        public override void ReceiveData(string id, object? data)
        {
            if (data is not byte[] bytes || bytes.Length == 0)
                return;

            if (id == ParamSchemaPacketId)
            {
                try
                {
                    using var ms = new MemoryStream(bytes);
                    using var br = new BinaryReader(ms, Encoding.UTF8, leaveOpen: false);

                    int schemaVersion = br.ReadInt32();
                    int count = br.ReadUInt16();
                    var entries = new List<AnimStateMachine.AnimParameterSchemaEntry>(count);
                    for (int i = 0; i < count; i++)
                    {
                        int len = br.ReadUInt16();
                        byte[] nameBytes = br.ReadBytes(len);

                        string name = Encoding.UTF8.GetString(nameBytes);
                        var type = (AnimStateMachine.AnimParameterType)br.ReadByte();

                        bool boolDefault = false;
                        int intDefault = 0;
                        float floatDefault = 0f;
                        switch (type)
                        {
                            case AnimStateMachine.AnimParameterType.Bool:
                                boolDefault = br.ReadBoolean();
                                break;
                            case AnimStateMachine.AnimParameterType.Int:
                                intDefault = br.ReadInt32();
                                break;
                            case AnimStateMachine.AnimParameterType.Float:
                                floatDefault = br.ReadSingle();
                                break;
                            default:
                                // Unknown type; abort the schema.
                                return;
                        }

                        entries.Add(new AnimStateMachine.AnimParameterSchemaEntry(name, type, boolDefault, intDefault, floatDefault));
                    }

                    StateMachine.ApplyReplicatedParameterSchema(entries, schemaVersion);
                    // Prevent immediately echoing the same schema back.
                    _lastSentSchemaVersion = schemaVersion;
                }
                catch
                {
                    // Ignore malformed schema payloads.
                }
                return;
            }

            int bitOffset = 0;
            switch (id)
            {
                case ChangeIndexedPacketId: //[paramId:indexBits][valueBits...]
                {
                    int indexBits = StateMachine.ParameterNameIdBitCount;
                    while (bitOffset + indexBits <= bytes.Length * 8)
                    {
                        int paramIndex = indexBits == 0 
                            ? 0 
                            : (int)ReadBits(bytes, ref bitOffset, indexBits);

                        if (!StateMachine.TryGetParameterNameByIndex(paramIndex, out var varName) || 
                            varName is null || 
                            !StateMachine.Variables.TryGetValue(varName, out var animVar) || 
                            animVar is null)
                            break;

                        animVar.ReadBits(bytes, ref bitOffset);
                    }
                    return;
                }
                case ChangeHashPacketId: //[hash:16][valueBits...]
                {
                    while (bitOffset + 16 <= bytes.Length * 8)
                    {
                        ushort hash = (ushort)ReadBits(bytes, ref bitOffset, 16);

                        if (StateMachine.HashToName.TryGetValue(hash, out var varName) &&
                            StateMachine.Variables.TryGetValue(varName, out var animVar))
                            animVar.ReadBits(bytes, ref bitOffset);
                    }
                    return;
                }
                case ChangeCollisionPacketId: //[hash:16][hasCollision:1][collisionIndex:?][valueBits...]
                {
                    while (bitOffset + 17 <= bytes.Length * 8)
                    {
                        ushort hash = (ushort)ReadBits(bytes, ref bitOffset, 16);
                        bool hasCollision = ReadBits(bytes, ref bitOffset, 1) != 0;

                        string? varName;
                        if (!hasCollision)
                        {
                            StateMachine.HashToName.TryGetValue(hash, out varName);
                        }
                        else
                        {
                            var names = StateMachine.GetNamesForHash(hash);
                            int collisionCount = names.Count;
                            if (collisionCount <= 1)
                                continue;

                            int indexBits = GetCollisionIndexBitCount(collisionCount);
                            if (bitOffset + indexBits > bytes.Length * 8)
                                break;

                            int collisionIndex = (int)ReadBits(bytes, ref bitOffset, indexBits);
                            varName = GetCollisionNameByIndex(names, collisionIndex);
                        }

                        if (varName is null)
                            continue;

                        if (StateMachine.Variables.TryGetValue(varName, out var animVar))
                            animVar.ReadBits(bytes, ref bitOffset);
                    }
                    break;
                }
            }
        }

        public void SetFloat(string name, float value)
        {
            var sm = StateMachine;
            if (sm.Variables.TryGetValue(name, out var variable))
                variable.FloatValue = value;
        }
        public void SetInt(string name, int value)
        {
            var sm = StateMachine;
            if (sm.Variables.TryGetValue(name, out var variable))
                variable.IntValue = value;
        }
        public void SetBool(string name, bool value)
        {
            var sm = StateMachine;
            if (sm.Variables.TryGetValue(name, out var variable))
                variable.BoolValue = value;
        }

        public void SetHumanoidValue(EHumanoidValue name, float value)
            => GetHumanoidComponent()?.SetValue(name, value);
    }
}
