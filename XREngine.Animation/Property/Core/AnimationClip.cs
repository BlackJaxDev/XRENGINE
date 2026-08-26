using System.Numerics;
using System.Diagnostics;
using MemoryPack;
using XREngine.Animation.IK;
using XREngine.Animation.Importers;
using XREngine.Core.Files;
using XREngine.Components.Animation;
using XREngine.Data;
using XREngine.Data.MMD;
using YamlDotNet.Serialization;

namespace XREngine.Animation
{
    /// <summary>
    /// Represents a single animation clip that can be played with an AnimationClipComponent or an AnimStateMachineComponent.
    /// </summary>
    [XRAssetInspector("XREngine.Editor.AssetEditors.AnimationClipInspector")]
    [XRAssetContextMenu("Open in Animation Clip Editor", "XREngine.Editor.UI.Tools.AnimationClipAssetMenuActions", "OpenInAnimationClipEditor")]
    [XR3rdPartyExtensions(typeof(XREngine.Data.XRDefault3rdPartyImportOptions), "vmd", "anim")]
    [MemoryPackable(GenerateType.NoGenerate)]
    public partial class AnimationClip : MotionBase
    {
        public override string ToString()
            => $"AnimationClip: {Name}";

        private EAnimTreeTraversalMethod _traversalMethod = EAnimTreeTraversalMethod.BreadthFirst;
        public EAnimTreeTraversalMethod TraversalMethod
        {
            get => _traversalMethod;
            set => SetField(ref _traversalMethod, value);
        }

        [MemoryPackConstructor]
        public AnimationClip()
            : base() { }
        public AnimationClip(AnimationMember rootFolder)
            : this() => RootMember = rootFolder;
        public AnimationClip(string animationName, string memberPath, BasePropAnim anim) : this()
        {
            Name = animationName;

            string[] memberPathParts = memberPath.Split('.');
            AnimationMember? last = null;

            foreach (string childMemberName in memberPathParts)
            {
                AnimationMember member = new(childMemberName);

                if (last is null)
                    RootMember = member;
                else
                    last.Children.Add(member);

                last = member;
            }

            LengthInSeconds = anim.LengthInSeconds;
            Looped = anim.Looped;
            if (last != null)
                last.Animation = anim;
        }

        private float _lengthInSeconds = 0.0f;
        /// <summary>
        /// The length of the longest included sub-animation in seconds.
        /// </summary>
        public float LengthInSeconds
        {
            get => _lengthInSeconds;
            set => SetField(ref _lengthInSeconds, value);
        }

        private bool _looped = false;
        /// <summary>
        /// Whether or not the animation should loop when the longest sub-animation reaches the end.
        /// </summary>
        public bool Looped
        {
            get => _looped;
            set => SetField(ref _looped, value);
        }

        private EAnimationClipKind _clipKind = EAnimationClipKind.Unknown;
        /// <summary>
        /// Classification of the animation data format (e.g. humanoid muscle vs generic transform).
        /// Set during import to enable automatic pipeline selection.
        /// </summary>
        public EAnimationClipKind ClipKind
        {
            get => _clipKind;
            set => SetField(ref _clipKind, value);
        }

        private bool _hasMuscleChannels;
        /// <summary>
        /// Whether this clip contains Unity humanoid muscle channels.
        /// </summary>
        public bool HasMuscleChannels
        {
            get => _hasMuscleChannels;
            set => SetField(ref _hasMuscleChannels, value);
        }

        private bool _hasRootMotion;
        /// <summary>
        /// Whether this clip contains root motion channels (RootT/RootQ).
        /// </summary>
        public bool HasRootMotion
        {
            get => _hasRootMotion;
            set => SetField(ref _hasRootMotion, value);
        }

        private bool _hasIKGoals;
        /// <summary>
        /// Whether this clip contains IK goal channels (LeftFootT/Q, RightHandT/Q, etc.).
        /// </summary>
        public bool HasIKGoals
        {
            get => _hasIKGoals;
            set => SetField(ref _hasIKGoals, value);
        }

        private int _sampleRate = 30;
        /// <summary>
        /// Source clip sample rate when provided by the importer.
        /// </summary>
        public int SampleRate
        {
            get => _sampleRate;
            set => SetField(ref _sampleRate, value);
        }

        private int _totalAnimCount = 0;
        public int TotalAnimCount
        {
            get => _totalAnimCount;
            set => SetField(ref _totalAnimCount, value);
        }

        private int _endedAnimations = 0;

        [MemoryPackIgnore]
        private AnimationMember? _rootMember;

        [MemoryPackIgnore]
        private readonly Dictionary<AnimationMember, object?[]> _unityHumanoidMethodArgumentBaselines = [];

        [MemoryPackIgnore]
        private long _unityHumanoidStatePlaybackTicks;

        [MemoryPackIgnore]
        private long _unityHumanoidStateLoopCycle;

        [MemoryPackIgnore]
        private bool _unityHumanoidStateClockInitialized;

        [MemoryPackIgnore]
        private bool _unityHumanoidSourceWrapped;

        [MemoryPackIgnore]
        private float _unityHumanoidStateSampleTime;

        [MemoryPackIgnore]
        private float _unityHumanoidStateSamplePhase;

        /// <summary>Signed loop epoch for this clip when evaluated as a state-machine leaf.</summary>
        [MemoryPackIgnore]
        public long UnityHumanoidStateLoopCycle => _unityHumanoidStateLoopCycle;

        /// <summary>
        /// True when CycleOffset crossed the source endpoint inside the current logical cycle.
        /// The runtime evaluator prefixes one conjugated generator in this case.
        /// </summary>
        [MemoryPackIgnore]
        public bool UnityHumanoidSourceWrapped => _unityHumanoidSourceWrapped;

        /// <summary>Effective source phase after CycleOffset for this state-machine leaf.</summary>
        [MemoryPackIgnore]
        public float UnityHumanoidStateSamplePhase => _unityHumanoidStateSamplePhase;

        [MemoryPackIgnore]
        [YamlIgnore]
        public AnimationMember? RootMember
        {
            get => _rootMember;
            set => SetField(ref _rootMember, value);
        }

        protected override bool OnPropertyChanging<T>(string? propName, T field, T @new)
        {
            bool change = base.OnPropertyChanging(propName, field, @new);
            if (change)
            {
                switch (propName)
                {
                    case nameof(RootMember):
                        //_rootMember?.Unregister(this);
                        break;
                }
            }
            return change;
        }

        protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
        {
            base.OnPropertyChanged(propName, prev, field);
            switch (propName)
            {
                case nameof(RootMember):
                    //TotalAnimCount = _rootMember?.Register(this) ?? 0;
                    break;
            }
        }

        internal void AnimationHasEnded(BaseAnimation obj)
        {
            //if (Interlocked.Increment(ref _endedAnimations) >= _totalAnimCount)
                //AllAnimationsEnded();
            //else
            //    Debug.WriteLine($"Animation {obj.Name} ended, {TotalAnimCount - _endedAnimations} remaining.");
        }

        private void AllAnimationsEnded()
        {
            if (Looped)
            {
                _rootMember?.StartAnimations();
                _endedAnimations = 0;
            }
            else
                OnAnimationEnded();
        }

        private void OnAnimationEnded()
        {
            _rootMember?.Unregister(this);
            IsPlaying = false;
            AnimationEnded?.Invoke(this);
        }

        public event Action<AnimationClip>? AnimationStarted;
        public event Action<AnimationClip>? AnimationEnded;

        public bool IsPlaying { get; private set; } = false;

        public Dictionary<string, BasePropAnim> GetAllAnimations()
        {
            Dictionary<string, BasePropAnim> anims = [];
            _rootMember?.CollectAnimations(null, anims);
            return anims;
        }
        public void Start(object? rootObject)
        {
            IsPlaying = true;
            TotalAnimCount = _rootMember?.Register(this, true) ?? 0;
            AnimationStarted?.Invoke(this);
        }
        public void Stop()
        {
            if (_endedAnimations < _totalAnimCount)
                _rootMember?.StopAnimations();
        }

        public override void GetAnimationValues(MotionBase? parentMotion, IDictionary<string, AnimVar> variables, float weight)
        {
            bool hasUnityHumanoidPolicy = TryGetUnityHumanoidEvaluationContext(
                out UnityHumanoidRootMotionPolicy unityPolicy,
                out float unitySampleTime,
                out float unitySamplePhase);

            // Typed store path: write directly to ValueStore via slot indices (no boxing)
            if (SlotLayout is not null)
            {
                foreach (var member in AnimatedMembersArray)
                {
                    if (!member.Slot.IsValid)
                        continue;

                    if (member.Animation is null && member.MemberType != EAnimationMemberType.Method)
                    {
                        member.WriteDefaultToStore(ValueStore);
                        continue;
                    }

                    if (hasUnityHumanoidPolicy
                        && TryWriteUnityHumanoidValueToStore(
                            member,
                            unityPolicy,
                            unitySampleTime,
                            unitySamplePhase))
                        continue;

                    member.WriteCurrentValueToStore(ValueStore);
                }
                parentMotion?.CopyAnimationValuesFrom(this);
                return;
            }

            // Legacy path
            foreach (var kvp in _animatedCurves)
            {
                if (kvp.Value.Animation is null && kvp.Value.MemberType != EAnimationMemberType.Method)
                {
                    SetAnimValue(kvp.Key, kvp.Value.DefaultValue);
                    continue;
                }
                
                object? animatedValue = hasUnityHumanoidPolicy
                    ? GetUnityHumanoidValueLegacy(
                        kvp.Value,
                        unityPolicy,
                        unitySampleTime,
                        unitySamplePhase)
                    : kvp.Value.GetAnimationValue();
                SetAnimValue(kvp.Key, animatedValue);
            }
            parentMotion?.CopyAnimationValuesFrom(this);
        }

        private bool TryGetUnityHumanoidEvaluationContext(
            out UnityHumanoidRootMotionPolicy policy,
            out float sampleTime,
            out float samplePhase)
        {
            policy = default;
            sampleTime = 0.0f;
            samplePhase = 0.0f;
            if (UnityHumanoidRootMotionSettings is not { } settings
                || !UnityHumanoidRootMotionPolicy.TryCreate(settings, out policy, out _))
                return false;

            if (!_unityHumanoidStateClockInitialized)
            {
                float initialPlaybackTime = 0.0f;
                AnimationMember[] members = AnimatedMembersArray;
                for (int i = 0; i < members.Length; i++)
                {
                    if (members[i].Animation is BasePropAnim animation)
                    {
                        initialPlaybackTime = animation.CurrentTime;
                        break;
                    }
                }
                _unityHumanoidStatePlaybackTicks = SecondsToUnityHumanoidTicks(initialPlaybackTime);
                _unityHumanoidStateLoopCycle = 0L;
                _unityHumanoidStateClockInitialized = true;
            }

            float playbackTime = UnityHumanoidTicksToSeconds(_unityHumanoidStatePlaybackTicks);

            if (!(LengthInSeconds > 0.0f) || !float.IsFinite(LengthInSeconds))
            {
                _unityHumanoidStateSampleTime = 0.0f;
                _unityHumanoidStateSamplePhase = 0.0f;
                _unityHumanoidSourceWrapped = false;
                return true;
            }

            float shiftedTime = playbackTime + policy.NormalizedCycleOffset * LengthInSeconds;
            sampleTime = policy.LoopTime
                ? WrapUnityHumanoidSampleTime(shiftedTime, LengthInSeconds)
                : Math.Clamp(shiftedTime, 0.0f, LengthInSeconds);
            samplePhase = Math.Clamp(sampleTime / LengthInSeconds, 0.0f, 1.0f);
            _unityHumanoidStateSampleTime = sampleTime;
            _unityHumanoidStateSamplePhase = samplePhase;
            _unityHumanoidSourceWrapped = policy.LoopTime
                && policy.NormalizedCycleOffset > 0.0f
                && playbackTime + policy.NormalizedCycleOffset * LengthInSeconds >= LengthInSeconds;
            return true;
        }

        private static float WrapUnityHumanoidSampleTime(float time, float length)
        {
            float wrapped = time % length;
            return wrapped < 0.0f ? wrapped + length : wrapped;
        }

        private bool TryWriteUnityHumanoidValueToStore(
            AnimationMember member,
            UnityHumanoidRootMotionPolicy policy,
            float sampleTime,
            float samplePhase)
        {
            if (!member.Slot.IsValid || member.Animation is null)
                return false;

            RestoreUnityHumanoidMethodArguments(member);
            switch (member.Slot.Type)
            {
                case EAnimValueType.Float when TrySampleFloat(member.Animation, sampleTime, out float floatValue):
                    floatValue = ApplyUnityHumanoidLoopPose(member, policy, samplePhase, floatValue);
                    ApplyUnityHumanoidMirror(member, policy, ref floatValue);
                    ValueStore.SetFloat(member.Slot.TypeIndex, floatValue);
                    return true;

                case EAnimValueType.Vector2 when member.Animation is PropAnimVector2 vector2Animation:
                    ValueStore.SetVector2(member.Slot.TypeIndex, vector2Animation.GetValue(sampleTime));
                    return true;

                case EAnimValueType.Vector3 when member.Animation is PropAnimVector3 vector3Animation:
                    Vector3 vector3Value = vector3Animation.GetValue(sampleTime);
                    vector3Value = ApplyUnityHumanoidLoopPose(member, policy, samplePhase, vector3Value);
                    ApplyUnityHumanoidMirror(member, policy, ref vector3Value);
                    ValueStore.SetVector3(member.Slot.TypeIndex, vector3Value);
                    return true;

                case EAnimValueType.Vector4 when member.Animation is PropAnimVector4 vector4Animation:
                    ValueStore.SetVector4(member.Slot.TypeIndex, vector4Animation.GetValue(sampleTime));
                    return true;

                case EAnimValueType.Quaternion when member.Animation is PropAnimQuaternion quaternionAnimation:
                    Quaternion quaternionValue = quaternionAnimation.GetValue(sampleTime);
                    quaternionValue = ApplyUnityHumanoidLoopPose(member, policy, samplePhase, quaternionValue);
                    ApplyUnityHumanoidMirror(member, policy, ref quaternionValue);
                    ValueStore.SetQuaternion(member.Slot.TypeIndex, quaternionValue);
                    return true;

                case EAnimValueType.Bool when member.Animation is PropAnimBool boolAnimation:
                    ValueStore.SetBool(member.Slot.TypeIndex, boolAnimation.GetValue(sampleTime));
                    return true;

                default:
                    ValueStore.SetValue(member.Slot, member.Animation.GetValueGeneric(sampleTime));
                    return true;
            }
        }

        private object? GetUnityHumanoidValueLegacy(
            AnimationMember member,
            UnityHumanoidRootMotionPolicy policy,
            float sampleTime,
            float samplePhase)
        {
            RestoreUnityHumanoidMethodArguments(member);
            object? value = member.Animation?.GetValueGeneric(sampleTime) ?? member.DefaultValue;
            switch (value)
            {
                case float floatValue:
                    floatValue = ApplyUnityHumanoidLoopPose(member, policy, samplePhase, floatValue);
                    ApplyUnityHumanoidMirror(member, policy, ref floatValue);
                    return floatValue;
                case Vector3 vector3Value:
                    vector3Value = ApplyUnityHumanoidLoopPose(member, policy, samplePhase, vector3Value);
                    ApplyUnityHumanoidMirror(member, policy, ref vector3Value);
                    return vector3Value;
                case Quaternion quaternionValue:
                    quaternionValue = ApplyUnityHumanoidLoopPose(member, policy, samplePhase, quaternionValue);
                    ApplyUnityHumanoidMirror(member, policy, ref quaternionValue);
                    return quaternionValue;
                default:
                    return value;
            }
        }

        private static bool TrySampleFloat(BasePropAnim animation, float sampleTime, out float value)
        {
            switch (animation)
            {
                case PropAnimFloat floatAnimation:
                    value = floatAnimation.GetValue(sampleTime);
                    return true;
                case PropAnimMethod<float> methodAnimation when methodAnimation.GetValue is { } getValue:
                    value = getValue(sampleTime);
                    return true;
                case PropAnimMethod<float> methodAnimation when methodAnimation.DefaultValue is float defaultValue:
                    value = defaultValue;
                    return true;
                default:
                    value = 0.0f;
                    return false;
            }
        }

        private float ApplyUnityHumanoidLoopPose(
            AnimationMember member,
            UnityHumanoidRootMotionPolicy policy,
            float phase,
            float value)
        {
            if (!policy.LoopPose
                || !IsUnityHumanoidPoseMember(member)
                || member.Animation is null
                || !TrySampleFloat(member.Animation, 0.0f, out float start)
                || !TrySampleFloat(member.Animation, LengthInSeconds, out float end))
                return value;

            return value + (start - end) * phase;
        }

        private Vector3 ApplyUnityHumanoidLoopPose(
            AnimationMember member,
            UnityHumanoidRootMotionPolicy policy,
            float phase,
            Vector3 value)
        {
            if (!policy.LoopPose
                || !IsUnityHumanoidPoseMember(member)
                || member.Animation is not PropAnimVector3 animation)
                return value;

            return value + (animation.GetValue(0.0f) - animation.GetValue(LengthInSeconds)) * phase;
        }

        private Quaternion ApplyUnityHumanoidLoopPose(
            AnimationMember member,
            UnityHumanoidRootMotionPolicy policy,
            float phase,
            Quaternion value)
        {
            if (!policy.LoopPose
                || !IsUnityHumanoidPoseMember(member)
                || member.Animation is not PropAnimQuaternion animation)
                return value;

            Quaternion start = Quaternion.Normalize(animation.GetValue(0.0f));
            Quaternion end = Quaternion.Normalize(animation.GetValue(LengthInSeconds));
            if (Quaternion.Dot(start, end) < 0.0f)
                end = new Quaternion(-end.X, -end.Y, -end.Z, -end.W);
            Quaternion correction = Quaternion.Slerp(
                Quaternion.Identity,
                Quaternion.Normalize(Quaternion.Inverse(end) * start),
                phase);
            return Quaternion.Normalize(value * correction);
        }

        private static bool IsUnityHumanoidPoseMember(AnimationMember member)
            => member.MemberType == EAnimationMemberType.Method
            && member.MemberName is "SetValue"
                or "SetImportedRawValue"
                or "SetAnimatedIKPosition"
                or "SetAnimatedIKPositionX"
                or "SetAnimatedIKPositionY"
                or "SetAnimatedIKPositionZ"
                or "SetAnimatedIKRotation"
                or "SetAnimatedIKRotationX"
                or "SetAnimatedIKRotationY"
                or "SetAnimatedIKRotationZ"
                or "SetAnimatedIKRotationW";

        private void ApplyUnityHumanoidMirror(
            AnimationMember member,
            UnityHumanoidRootMotionPolicy policy,
            ref float value)
        {
            if (!policy.Mirror || member.MemberType != EAnimationMemberType.Method)
                return;

            if (member.MemberName is "SetValue" or "SetImportedRawValue"
                && TryGetUnityHumanoidMuscleArgument(member, out EHumanoidValue muscle))
            {
                member.MethodArguments[0] = UnityHumanoidMirrorOperator.MirrorMuscle(muscle, out float parity);
                value *= parity;
                return;
            }

            if (IsUnityHumanoidIKMember(member)
                && TryGetUnityHumanoidGoalArgument(member, out ELimbEndEffector goal))
                member.MethodArguments[0] = UnityHumanoidMirrorOperator.MirrorGoal(goal);

            // RootT/RootQ remain in their imported basis here. The shared humanoid
            // evaluator mirrors the complete Body sample once, after atomic staging.
            if (member.MemberName is "SetAnimatedIKPositionX"
                or "SetAnimatedIKRotationY" or "SetAnimatedIKRotationZ")
                value = -value;
        }

        private void ApplyUnityHumanoidMirror(
            AnimationMember member,
            UnityHumanoidRootMotionPolicy policy,
            ref Vector3 value)
        {
            if (!policy.Mirror || member.MemberType != EAnimationMemberType.Method)
                return;

            if (IsUnityHumanoidIKMember(member)
                && TryGetUnityHumanoidGoalArgument(member, out ELimbEndEffector goal))
            {
                member.MethodArguments[0] = UnityHumanoidMirrorOperator.MirrorGoal(goal);
                value = UnityHumanoidMirrorOperator.MirrorPosition(value);
            }
        }

        private void ApplyUnityHumanoidMirror(
            AnimationMember member,
            UnityHumanoidRootMotionPolicy policy,
            ref Quaternion value)
        {
            if (!policy.Mirror || member.MemberType != EAnimationMemberType.Method)
                return;

            if (IsUnityHumanoidIKMember(member)
                && TryGetUnityHumanoidGoalArgument(member, out ELimbEndEffector goal))
            {
                member.MethodArguments[0] = UnityHumanoidMirrorOperator.MirrorGoal(goal);
                value = UnityHumanoidMirrorOperator.MirrorRotation(value);
            }
        }

        private static bool IsUnityHumanoidIKMember(AnimationMember member)
            => member.MemberName.StartsWith("SetAnimatedIK", StringComparison.Ordinal);

        private static bool TryGetUnityHumanoidMuscleArgument(
            AnimationMember member,
            out EHumanoidValue muscle)
        {
            if (member.MethodArguments.Length > 0 && member.MethodArguments[0] is EHumanoidValue value)
            {
                muscle = value;
                return true;
            }

            muscle = default;
            return false;
        }

        private static bool TryGetUnityHumanoidGoalArgument(
            AnimationMember member,
            out ELimbEndEffector goal)
        {
            if (member.MethodArguments.Length > 0 && member.MethodArguments[0] is ELimbEndEffector value)
            {
                goal = value;
                return true;
            }

            goal = default;
            return false;
        }

        private void RestoreUnityHumanoidMethodArguments(AnimationMember member)
        {
            if (member.MemberType != EAnimationMemberType.Method)
                return;

            if (!_unityHumanoidMethodArgumentBaselines.TryGetValue(member, out object?[]? baseline))
            {
                baseline = (object?[])member.MethodArguments.Clone();
                _unityHumanoidMethodArgumentBaselines.Add(member, baseline);
            }

            int count = Math.Min(member.MethodArguments.Length, baseline.Length);
            for (int i = 0; i < count; i++)
            {
                if (i != member.AnimatedMethodArgumentIndex)
                    member.MethodArguments[i] = baseline[i];
            }
        }

        internal void ResetUnityHumanoidEvaluationState()
        {
            foreach ((AnimationMember member, object?[] baseline) in _unityHumanoidMethodArgumentBaselines)
            {
                int count = Math.Min(member.MethodArguments.Length, baseline.Length);
                for (int i = 0; i < count; i++)
                {
                    if (i != member.AnimatedMethodArgumentIndex)
                        member.MethodArguments[i] = baseline[i];
                }
            }
            _unityHumanoidMethodArgumentBaselines.Clear();
            ResetUnityHumanoidStateClock(0.0f);
        }

        internal void ResetUnityHumanoidStateClock(float timeSeconds)
        {
            long lengthTicks = SecondsToUnityHumanoidTicks(LengthInSeconds);
            _unityHumanoidStatePlaybackTicks = Math.Clamp(
                SecondsToUnityHumanoidTicks(timeSeconds),
                0L,
                Math.Max(0L, lengthTicks));
            _unityHumanoidStateLoopCycle = 0L;
            _unityHumanoidStateClockInitialized = true;
            _unityHumanoidSourceWrapped = false;
            _unityHumanoidStateSampleTime = Math.Clamp(timeSeconds, 0.0f, Math.Max(0.0f, LengthInSeconds));
            _unityHumanoidStateSamplePhase = LengthInSeconds > 0.0f
                ? Math.Clamp(_unityHumanoidStateSampleTime / LengthInSeconds, 0.0f, 1.0f)
                : 0.0f;
        }

        /// <summary>
        /// Publishes the current authored muscle sample before Loop Pose correction. Feet-based
        /// root projection consumes this sample before the corrected pose is committed.
        /// </summary>
        public void PublishUnityHumanoidProjectionMuscles(IUnityHumanoidProjectionPoseSink sink)
            => PublishUnityHumanoidProjectionMusclesAtTime(_unityHumanoidStateSampleTime, sink);

        /// <summary>
        /// Publishes an authored pre-Loop-Pose muscle sample without changing the state-machine
        /// clock. Endpoint probes use this to calculate feet-based Body seam correction before
        /// the first visible evaluation.
        /// </summary>
        public void PublishUnityHumanoidProjectionMusclesAtTime(
            float timeSeconds,
            IUnityHumanoidProjectionPoseSink sink)
        {
            ArgumentNullException.ThrowIfNull(sink);
            if (UnityHumanoidRootMotionSettings is not { } settings
                || !UnityHumanoidRootMotionPolicy.TryCreate(
                    settings,
                    out UnityHumanoidRootMotionPolicy policy,
                    out _)
                || (!policy.LoopPose
                    && (policy.BakePositionYIntoPose
                        || policy.PositionYBasis is not EUnityHumanoidRootPositionYBasis.Feet)))
                return;

            AnimationMember[] members = AnimatedMembersArray;
            for (int i = 0; i < members.Length; i++)
            {
                AnimationMember member = members[i];
                if (member.MemberType != EAnimationMemberType.Method
                    || member.MemberName is not ("SetValue" or "SetImportedRawValue")
                    || member.Animation is not BasePropAnim animation
                    || !TrySampleFloat(animation, timeSeconds, out float amount))
                    continue;

                RestoreUnityHumanoidMethodArguments(member);
                if (!TryGetUnityHumanoidMuscleArgument(member, out EHumanoidValue muscle))
                    continue;

                if (policy.Mirror)
                {
                    muscle = UnityHumanoidMirrorOperator.MirrorMuscle(muscle, out float parity);
                    amount *= parity;
                }

                bool flipImportedMuscleZ = member.MemberName == "SetImportedRawValue"
                    && member.MethodArguments.Length > 2
                    && member.MethodArguments[2] is true;
                sink.SetUnityHumanoidProjectionMuscle(muscle, amount, flipImportedMuscleZ);
            }
        }

        /// <summary>
        /// Samples the imported-mapped RootT/RootQ leaf data without mutating its playback clock.
        /// </summary>
        public bool TrySampleUnityHumanoidBody(
            float timeSeconds,
            out Vector3 position,
            out Quaternion rotation)
        {
            position = Vector3.Zero;
            rotation = Quaternion.Identity;
            bool hasPosition = false;
            bool hasRotation = false;
            AnimationMember[] members = AnimatedMembersArray;
            for (int i = 0; i < members.Length; i++)
            {
                AnimationMember member = members[i];
                if (member.Animation is not BasePropAnim animation
                    || !TrySampleFloat(animation, timeSeconds, out float value))
                    continue;

                switch (member.MemberName)
                {
                    case "SetRootPositionX": position.X = value; hasPosition = true; break;
                    case "SetRootPositionY": position.Y = value; hasPosition = true; break;
                    case "SetRootPositionZ": position.Z = value; hasPosition = true; break;
                    case "SetRootRotationX": rotation.X = value; hasRotation = true; break;
                    case "SetRootRotationY": rotation.Y = value; hasRotation = true; break;
                    case "SetRootRotationZ": rotation.Z = value; hasRotation = true; break;
                    case "SetRootRotationW": rotation.W = value; hasRotation = true; break;
                }
            }

            if (hasRotation && rotation.LengthSquared() > 1.0e-8f)
                rotation = Quaternion.Normalize(rotation);
            else
                rotation = Quaternion.Identity;
            return hasPosition || hasRotation;
        }

        private static object? Lerp(object? defaultvalue, object? animatedValue, float weight) => defaultvalue switch
        {
            float df when animatedValue is float af => Interp.Lerp(df, af, weight),
            Vector2 df2 when animatedValue is Vector2 af2 => Vector2.Lerp(df2, af2, weight),
            Vector3 df3 when animatedValue is Vector3 af3 => Vector3.Lerp(df3, af3, weight),
            Vector4 df4 when animatedValue is Vector4 af4 => Vector4.Lerp(df4, af4, weight),
            Quaternion dfq when animatedValue is Quaternion afq => Quaternion.Slerp(dfq, afq, weight),
            _ => weight > 0.5f ? animatedValue : defaultvalue, //Discrete; choose closest value
        };

        public override void Tick(float delta)
        {
            AdvanceUnityHumanoidStateClock(SecondsToUnityHumanoidTicks(delta));
            TickPropertyAnimations(delta);
        }

        public override void Tick(long deltaTicks)
        {
            AdvanceUnityHumanoidStateClock(deltaTicks);
            TickPropertyAnimations(deltaTicks);
        }

        private void AdvanceUnityHumanoidStateClock(long deltaTicks)
        {
            if (!_unityHumanoidStateClockInitialized
                || UnityHumanoidRootMotionSettings is not { } settings
                || !UnityHumanoidRootMotionPolicy.TryCreate(settings, out UnityHumanoidRootMotionPolicy policy, out _))
                return;

            long lengthTicks = SecondsToUnityHumanoidTicks(LengthInSeconds);
            if (lengthTicks <= 0L)
                return;

            long unwrappedTicks = _unityHumanoidStatePlaybackTicks + deltaTicks;
            if (!policy.LoopTime)
            {
                _unityHumanoidStatePlaybackTicks = Math.Clamp(unwrappedTicks, 0L, lengthTicks);
                return;
            }

            _unityHumanoidStateLoopCycle += CountUnityHumanoidWrappedCycles(unwrappedTicks, lengthTicks);
            _unityHumanoidStatePlaybackTicks = WrapUnityHumanoidTicks(unwrappedTicks, lengthTicks);
        }

        private static long CountUnityHumanoidWrappedCycles(long unwrappedTicks, long lengthTicks)
        {
            long quotient = unwrappedTicks / lengthTicks;
            if (unwrappedTicks % lengthTicks < 0L)
                quotient--;
            return quotient;
        }

        private static long WrapUnityHumanoidTicks(long ticks, long lengthTicks)
        {
            long wrapped = ticks % lengthTicks;
            return wrapped < 0L ? wrapped + lengthTicks : wrapped;
        }

        private static long SecondsToUnityHumanoidTicks(double seconds)
            => !double.IsFinite(seconds) || seconds == 0.0
                ? 0L
                : (long)Math.Round(seconds * Stopwatch.Frequency);

        private static float UnityHumanoidTicksToSeconds(long ticks)
            => (float)(ticks / (double)Stopwatch.Frequency);

        public override bool Load3rdParty(string filePath)
        {
            string ext = System.IO.Path.GetExtension(filePath).ToLowerInvariant();
            switch (ext)
            {
                case ".vmd":
                    VMDFile vmd = new();
                    vmd.Load(filePath);
                    LoadFromVMD(vmd);
                    return true;
                case ".anim":
                        var imported = Importers.AnimYamlImporter.Import(filePath);
                    Name = imported.Name;
                    LengthInSeconds = imported.LengthInSeconds;
                    Looped = imported.Looped;
                    ClipKind = imported.ClipKind;
                    HasMuscleChannels = imported.HasMuscleChannels;
                    HasRootMotion = imported.HasRootMotion;
                    HasIKGoals = imported.HasIKGoals;
                    SourceMaterialBindings = imported.SourceMaterialBindings;
                    MaterialBindingDiagnostics = imported.MaterialBindingDiagnostics;
                    UnityHumanoidRootMotionSettings = imported.UnityHumanoidRootMotionSettings;
                    UnityImportManifest = imported.UnityImportManifest;
                    SampleRate = imported.SampleRate;
                    RootMember = imported.RootMember;
                    return true;
            }
            return false;
        }

        public void LoadFromVMD(VMDFile vmd)
        {
            const float fps = 30.0f;
            LengthInSeconds = vmd.MaxFrameCount / fps;
            AssembleVMDTree(vmd, fps);
        }

        /// <summary>
        /// Creates 4 quaternion control points for a cubic Bézier curve between start and end,
        /// using cp1 and cp2 (from a 2D easing curve defined on [0,1]²) to control the tangents.
        /// </summary>
        public static void BezierCurveToControlPoints(Quaternion start, Quaternion end, Vector2 cp1, Vector2 cp2, out Quaternion startCP, out Quaternion endCP)
        {
            Quaternion q0 = start;
            Quaternion q3 = end;

            // Compute the relative rotation from start to end
            Quaternion delta = Quaternion.Inverse(q0) * q3;
            Vector3 logDelta = QuaternionLog(delta);

            // Use the y-values of the 2D control points as easing factors:
            // Q1 rotates from q0 by a fraction cp1.y of the total rotation.
            startCP = q0 * QuaternionExp(logDelta * cp1.Y);
            // Q2 rotates backwards from q3 by a fraction (1 - cp2.y) of the total rotation.
            endCP = q3 * QuaternionExp(-logDelta * (1 - cp2.Y));
        }

        public static void BezierCurveToControlPoints(float start, float end, Vector2 cp1, Vector2 cp2, out float startCP, out float endCP)
        {
            float p0 = start;
            float p3 = end;
            // Use the y-values of the 2D control points as easing factors:
            // P1 moves from p0 by a fraction cp1.y of the total distance.
            startCP = p0 + (p3 - p0) * cp1.Y;
            // P2 moves backwards from p3 by a fraction (1 - cp2.y) of the total distance.
            endCP = p3 + (p0 - p3) * (1 - cp2.Y);
        }
        public static void BezierCurveToControlPoints(Vector3 start, Vector3 end, Vector2 cp1, Vector2 cp2, out Vector3 startCP, out Vector3 endCP)
        {
            Vector3 p0 = start;
            Vector3 p3 = end;
            // Use the y-values of the 2D control points as easing factors:
            // P1 moves from p0 by a fraction cp1.y of the total distance.
            startCP = p0 + (p3 - p0) * cp1.Y;
            // P2 moves backwards from p3 by a fraction (1 - cp2.y) of the total distance.
            endCP = p3 + (p0 - p3) * (1 - cp2.Y);
        }

        /// <summary>
        /// Computes the logarithm of a unit quaternion.
        /// The result is a vector representing the "angle-axis" (with angle = |v|) in the tangent space.
        /// </summary>
        public static Vector3 QuaternionLog(Quaternion q)
        {
            // Ensure the quaternion is normalized.
            if (q.W > 1f)
                q = Quaternion.Normalize(q);

            float angle = MathF.Acos(q.W);
            float sinAngle = MathF.Sin(angle);
            if (MathF.Abs(sinAngle) > 0.0001f)
                return new Vector3(q.X, q.Y, q.Z) * (angle / sinAngle);
            else
                return new Vector3(q.X, q.Y, q.Z); // small-angle approximation
        }

        /// <summary>
        /// Computes the exponential of a pure imaginary quaternion (represented as a Vector3).
        /// This returns a unit quaternion.
        /// </summary>
        public static Quaternion QuaternionExp(Vector3 v)
        {
            float angle = v.Length();
            float sinAngle = MathF.Sin(angle);
            Quaternion result;
            if (MathF.Abs(angle) > 0.0001f)
            {
                result = new Quaternion(
                    v.X * (sinAngle / angle),
                    v.Y * (sinAngle / angle),
                    v.Z * (sinAngle / angle),
                    MathF.Cos(angle));
            }
            else
            {
                result = new Quaternion(v.X, v.Y, v.Z, 1.0f); // small-angle approximation
            }
            return Quaternion.Normalize(result);
        }
        private void AssembleVMDTree(VMDFile vmd, float fps)
        {
            RootMember = new("SceneNode", EAnimationMemberType.Property);
            AnimationMember? ikRoot = null;
            if (vmd.BoneAnimation is not null)
            {
                foreach (var bone in vmd.BoneAnimation)
                {
                    PropAnimFloat xAnim = new((int)vmd.MaxFrameCount, fps, false, true);
                    PropAnimFloat yAnim = new((int)vmd.MaxFrameCount, fps, false, true);
                    PropAnimFloat zAnim = new((int)vmd.MaxFrameCount, fps, false, true);
                    PropAnimQuaternion rotAnim = new((int)vmd.MaxFrameCount, fps, false, true);
                    PopulateVMDAnimation(fps, bone, xAnim, yAnim, zAnim, rotAnim);

                    //ConstrainAndLerpFloat(fps, xAnim);
                    //ConstrainAndLerpFloat(fps, yAnim);
                    //ConstrainAndLerpFloat(fps, zAnim);
                    //ConstrainAndLerpQuat(fps, rotAnim);

                    if (bone.Key.Contains("IK"))
                    {
                        if (ikRoot is null)
                        {
                            ikRoot = new AnimationMember("GetComponent", EAnimationMemberType.Method)
                            {
                                MethodArguments = ["HumanoidIKSolverComponent"],
                                CacheReturnValue = true, //Cache this method call so we don't have to search for the humanoid every frame
                            };
                            RootMember.Children.Add(ikRoot);
                        }

                        if (bone.Key.Contains("Foot"))
                        {
                            bool left = bone.Key.Contains('L');
                            ELimbEndEffector eff = left ? ELimbEndEffector.LeftFoot : ELimbEndEffector.RightFoot;
                            AnimationMember boneX = new("SetIKPositionX", EAnimationMemberType.Method, xAnim)
                            {
                                MethodArguments = [eff, 0.0f],
                                AnimatedMethodArgumentIndex = 1,
                            };
                            ikRoot.Children.Add(boneX);

                            AnimationMember boneY = new("SetIKPositionY", EAnimationMemberType.Method, yAnim)
                            {
                                MethodArguments = [eff, 0.0f],
                                AnimatedMethodArgumentIndex = 1,
                            };
                            ikRoot.Children.Add(boneY);

                            AnimationMember boneZ = new("SetIKPositionZ", EAnimationMemberType.Method, zAnim)
                            {
                                MethodArguments = [eff, 0.0f],
                                AnimatedMethodArgumentIndex = 1,
                            };
                            ikRoot.Children.Add(boneZ);

                            AnimationMember boneRot = new("SetIKRotation", EAnimationMemberType.Method, rotAnim)
                            {
                                MethodArguments = [eff, Quaternion.Identity],
                                AnimatedMethodArgumentIndex = 1,
                            };
                            ikRoot.Children.Add(boneRot);
                        }
                    }
                    else
                    {
                        //Trace.WriteLine($"Bone: {bone.Key}");
                        var getBone = new AnimationMember("FindDescendantByName", EAnimationMemberType.Method)
                        {
                            MethodArguments = [bone.Key, StringComparison.InvariantCultureIgnoreCase],
                            CacheReturnValue = true, //Cache this method call so we don't have to search for the bone every frame
                        };
                        RootMember.Children.Add(getBone);

                        var transform = new AnimationMember("Transform", EAnimationMemberType.Property);
                        getBone.Children.Add(transform);

                        AnimationMember boneX = new("SetBindRelativeX", EAnimationMemberType.Method, xAnim) { MethodArguments = [0.0f] };
                        transform.Children.Add(boneX);

                        AnimationMember boneY = new("SetBindRelativeY", EAnimationMemberType.Method, yAnim) { MethodArguments = [0.0f] };
                        transform.Children.Add(boneY);

                        AnimationMember boneZ = new("SetBindRelativeZ", EAnimationMemberType.Method, zAnim) { MethodArguments = [0.0f] };
                        transform.Children.Add(boneZ);

                        AnimationMember boneRot = new("SetBindRelativeRotation", EAnimationMemberType.Method, rotAnim) { MethodArguments = [Quaternion.Identity] };
                        transform.Children.Add(boneRot);
                    }
                }
            }
            if (vmd.ShapeKeyAnimation is not null)
            {
                //foreach (var morph in vmd.ShapeKeyAnimation)
                //{
                //    var getMorph = new AnimationMember("SetBlendshapeValue", EAnimationMemberType.Method)
                //    {
                //        MethodArguments = [morph.Key, StringComparison.InvariantCultureIgnoreCase],
                //        CacheReturnValue = true, //Cache this method call so we don't have to search for the meshes every frame
                //    };
                //    morphMember.Children.Add(morphAnim);
                //    PropAnimFloat morphAnimFloat = new((int)vmd.MaxFrameCount, fps, false, true);
                //    AnimationMember morphFloat = new("SetMorph");
                //    morphAnim.Children.Add(morphFloat);
                //    foreach (var frame in morph.Value)
                //        morphAnimFloat.Keyframes.Add(new FloatKeyframe((int)frame.Key, fps, frame.Value.Weight, 0.0f, EVectorInterpType.Step));
                //}
            }

        }

        private static void PopulateVMDAnimation(
            float fps,
            KeyValuePair<string, FrameDictionary<BoneFrameKey>> bone,
            PropAnimFloat xAnim,
            PropAnimFloat yAnim,
            PropAnimFloat zAnim,
            PropAnimQuaternion rotAnim)
        {
            var frames = bone.Value.ToArray();
            for (int i = 0; i < frames.Length; i++)
            {
                KeyValuePair<uint, BoneFrameKey> frame = frames[i];
                bool firstFrame = i == 0;
                bool lastFrame = i == frames.Length - 1;
                var data = frame.Value;
                var lastData = lastFrame ? data : frames[i + 1].Value;
                var nextData = firstFrame ? data : frames[i - 1].Value;

                BezierCurveToControlPoints(
                    lastData.Translation.X,
                    data.Translation.X,
                    lastData.TranslationXBezier!.StartControlPoint,
                    data.TranslationXBezier!.EndControlPoint,
                    out _,
                    out float xOutTan);
                BezierCurveToControlPoints(
                    data.Translation.X,
                    nextData.Translation.X,
                    lastData.TranslationXBezier!.StartControlPoint,
                    data.TranslationXBezier!.EndControlPoint,
                    out float xInTan,
                    out _);
                xAnim.Keyframes.Add(new FloatKeyframe((int)frame.Key, fps, data.Translation.X, xOutTan, xInTan, EVectorInterpType.Smooth));

                BezierCurveToControlPoints(
                    lastData.Translation.Y,
                    data.Translation.Y,
                    lastData.TranslationYBezier!.StartControlPoint,
                    data.TranslationYBezier!.EndControlPoint,
                    out _,
                    out float yOutTan);
                BezierCurveToControlPoints(
                    data.Translation.Y,
                    nextData.Translation.Y,
                    lastData.TranslationYBezier!.StartControlPoint,
                    data.TranslationYBezier!.EndControlPoint,
                    out float yInTan,
                    out _);
                yAnim.Keyframes.Add(new FloatKeyframe((int)frame.Key, fps, data.Translation.Y, yOutTan, yInTan, EVectorInterpType.Smooth));

                BezierCurveToControlPoints(
                    lastData.Translation.Z,
                    data.Translation.Z,
                    lastData.TranslationZBezier!.StartControlPoint,
                    data.TranslationZBezier!.EndControlPoint,
                    out _,
                    out float zOutTan);
                BezierCurveToControlPoints(
                    data.Translation.Z,
                    nextData.Translation.Z,
                    lastData.TranslationZBezier!.StartControlPoint,
                    data.TranslationZBezier!.EndControlPoint,
                    out float zInTan,
                    out _);
                zAnim.Keyframes.Add(new FloatKeyframe((int)frame.Key, fps, data.Translation.Z, zOutTan, zInTan, EVectorInterpType.Smooth));

                BezierCurveToControlPoints(
                    lastData.Rotation,
                    data.Rotation,
                    lastData.RotationBezier!.StartControlPoint,
                    data.RotationBezier!.EndControlPoint,
                    out _,
                    out Quaternion outRotTan);
                BezierCurveToControlPoints(
                    data.Rotation,
                    nextData.Rotation,
                    lastData.RotationBezier!.StartControlPoint,
                    data.RotationBezier!.EndControlPoint,
                    out Quaternion inRotTan,
                    out _);
                rotAnim.Keyframes.Add(new QuaternionKeyframe((int)frame.Key, fps, data.Rotation, outRotTan, inRotTan, ERadialInterpType.Smooth));
            }
        }
    }
}
