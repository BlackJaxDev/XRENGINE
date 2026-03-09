using System.Numerics;
using XREngine.Animation;
using XREngine.Animation.IK;
using XREngine.Components;
using XREngine.Data;
using XREngine.Data.Animation;
using XREngine.Scene.Transforms;
using Extensions;

namespace XREngine.Components.Animation
{
    public class AnimationClipComponent : XRComponent
    {
        private bool _initialized;
        private readonly List<AnimationMember> _animatedMembers = [];
        private AnimationMember[] _animatedMembersSnapshot = [];
        private readonly Dictionary<AnimationMember, object?[]> _baselineMethodArguments = [];
        private readonly HashSet<Transform> _animatedQuaternionTargets = [];
        private Transform[] _animatedQuaternionTargetsSnapshot = [];

        private AnimationClip? _animation;
        public AnimationClip? Animation
        {
            get => _animation;
            set => SetField(ref _animation, value);
        }

        private bool _startOnActivate = false;
        public bool StartOnActivate
        {
            get => _startOnActivate;
            set => SetField(ref _startOnActivate, value);
        }

        private float _weight = 1.0f;
        public float Weight
        {
            get => _weight;
            set => SetField(ref _weight, value);
        }

        private float _speed = 1.0f;
        public float Speed
        {
            get => _speed;
            set => SetField(ref _speed, value);
        }

        private float _playbackTime;
        public float PlaybackTime
        {
            get => _playbackTime;
            private set => SetField(ref _playbackTime, value);
        }

        private bool _suspendSiblingStateMachine = true;
        public bool SuspendSiblingStateMachine
        {
            get => _suspendSiblingStateMachine;
            set => SetField(ref _suspendSiblingStateMachine, value);
        }

        private bool _flipMuscleLeftRight;
        public bool FlipMuscleLeftRight
        {
            get => _flipMuscleLeftRight;
            set => SetField(ref _flipMuscleLeftRight, value);
        }

        private bool _flipMuscleZ = false;
        public bool FlipMuscleZ
        {
            get => _flipMuscleZ;
            set => SetField(ref _flipMuscleZ, value);
        }

        private bool _flipIKPositionLeftRight;
        public bool FlipIKPositionLeftRight
        {
            get => _flipIKPositionLeftRight;
            set => SetField(ref _flipIKPositionLeftRight, value);
        }

        private bool _flipIKPositionZ;
        public bool FlipIKPositionZ
        {
            get => _flipIKPositionZ;
            set => SetField(ref _flipIKPositionZ, value);
        }

        private bool _flipIKRotationLeftRight;
        public bool FlipIKRotationLeftRight
        {
            get => _flipIKRotationLeftRight;
            set => SetField(ref _flipIKRotationLeftRight, value);
        }

        private bool _flipIKRotationZ;
        public bool FlipIKRotationZ
        {
            get => _flipIKRotationZ;
            set => SetField(ref _flipIKRotationZ, value);
        }

        private AnimStateMachineComponent? _suspendedSiblingAnimator;
        private bool _suspendedSiblingWasAlreadySuspended;

        protected override bool OnPropertyChanging<T>(string? propName, T field, T @new)
        {
            bool change = base.OnPropertyChanging(propName, field, @new);
            if (change)
            {
                switch (propName)
                {
                    case nameof(Animation):
                        Stop();
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
                case nameof(Animation):
                case nameof(StartOnActivate):
                    if (IsActiveInHierarchy && StartOnActivate)
                        Start();
                    break;
            }
        }

        private void Start()
        {
            if (Animation is null || Animation.IsPlaying)
                return;

            if (SuspendSiblingStateMachine && _suspendedSiblingAnimator is null && TryGetSiblingComponent<AnimStateMachineComponent>(out var animator) && animator is not null)
            {
                _suspendedSiblingAnimator = animator;
                _suspendedSiblingWasAlreadySuspended = animator.SuspendedByClip;
                animator.SetSuspendedByClip(true);
            }

            EnsureHumanoidAnimationIKSolver();
            ResetRootMotionBaselineIfNeeded();
            EnsureInitialized();

            // Bind members to this component/SceneNode via the anim state machine.
            // Seed the underlying property animations to a canonical clip time.
            float initialTime = GetInitialPlaybackTime(Animation, Speed);
            StartAllPropertyAnimations(Animation, initialTime);
            PlaybackTime = initialTime;
            ApplyAnimatedValues();

            RegisterTick(ETickGroup.Normal, ETickOrder.Animation, TickAnimation);
        }

        private void Stop()
        {
            ClearHumanoidAnimationIKSolverGoals();

            if (Animation is not null)
                StopAllPropertyAnimations(Animation);

            if (_initialized)
                RestoreAnimatedState();

            UnregisterTick(ETickGroup.Normal, ETickOrder.Animation, TickAnimation);

            if (_suspendedSiblingAnimator is not null)
            {
                if (!_suspendedSiblingWasAlreadySuspended)
                    _suspendedSiblingAnimator.SetSuspendedByClip(false);
                _suspendedSiblingAnimator = null;
                _suspendedSiblingWasAlreadySuspended = false;
            }

            if (_initialized)
                Deinitialize();
        }

        private void RestoreAnimatedState()
        {
            var humanoid = GetSiblingHumanoid();
            if (humanoid is not null)
            {
                humanoid.ResetPose();
                return;
            }

            foreach (var member in _animatedMembersSnapshot)
            {
                if (member.MemberType == EAnimationMemberType.Method)
                {
                    RestoreBaselineMethodArguments(member);
                    continue;
                }

                member.ApplyAnimationValue(member.DefaultValue);
            }

            NormalizeAnimatedQuaternionTargets();
        }

        public void EvaluateAtTime(float timeSeconds)
        {
            if (Animation is null)
                return;

            EnsureHumanoidAnimationIKSolver();
            if (!_initialized)
                ResetRootMotionBaselineIfNeeded();
            EnsureInitialized();

            float evaluationTime = NormalizePlaybackTime(timeSeconds, Animation, wrapLooped: false);
            SetAllPropertyAnimationTimes(Animation, evaluationTime, wrapLooped: false);
            PlaybackTime = evaluationTime;

            if (!ShouldDriveSiblingHumanoidPose())
                return;

            ApplyAnimatedValues();

            if (Animation.HasMuscleChannels)
            {
                var humanoid = GetSiblingHumanoid();
                humanoid?.ApplyCurrentMusclePose();
            }
        }

        private void TickAnimation()
        {
            if (Animation is null || !_initialized)
                return;

            float delta = Engine.Delta * Speed;
            PlaybackTime = NormalizePlaybackTime(PlaybackTime + delta, Animation, wrapLooped: Animation.Looped);
            SetAllPropertyAnimationTimes(Animation, PlaybackTime, wrapLooped: false);

            if (!ShouldDriveSiblingHumanoidPose())
                return;

            ApplyAnimatedValues();
        }

        private void EnsureInitialized()
        {
            if (_initialized || Animation?.RootMember is null)
                return;

            _animatedMembers.Clear();
            _baselineMethodArguments.Clear();
            _animatedQuaternionTargets.Clear();
            InitializeMembers(Animation.RootMember, this, _animatedMembers, _animatedQuaternionTargets);
            _animatedMembersSnapshot = [.. _animatedMembers];
            _animatedQuaternionTargetsSnapshot = [.. _animatedQuaternionTargets];
            foreach (var member in _animatedMembersSnapshot)
            {
                if (member.MemberType == EAnimationMemberType.Method)
                    _baselineMethodArguments[member] = (object?[])member.MethodArguments.Clone();
            }
            _initialized = true;
        }

        private HumanoidIKSolverComponent? EnsureHumanoidAnimationIKSolver()
        {
            if (Animation?.HasIKGoals != true)
                return null;

            var humanoid = GetSiblingHumanoid();
            if (humanoid is null)
                return null;

            if (humanoid.SceneNode.GetComponent<VRIKSolverComponent>() is not null)
                return null;

            // Animation clips should only drive IK goals when a humanoid IK solver
            // was already intentionally added (e.g., via AddCharacterIK setup).
            return humanoid.SceneNode.GetComponent<HumanoidIKSolverComponent>();
        }

        private void ClearHumanoidAnimationIKSolverGoals()
        {
            if (Animation?.HasIKGoals != true)
                return;

            var solver = SceneNode.GetComponentInHierarchy("HumanoidIKSolverComponent") as HumanoidIKSolverComponent;
            solver?.ClearAnimatedIKGoals();
        }

        private void ResetRootMotionBaselineIfNeeded()
        {
            if (Animation?.HasRootMotion != true)
                return;

            var humanoid = GetSiblingHumanoid();
            humanoid?.ResetRootMotionBaseline();
        }

        private HumanoidComponent? GetSiblingHumanoid()
            => TryGetSiblingComponent<HumanoidComponent>(out var humanoid) ? humanoid : null;

        private void Deinitialize()
        {
            _animatedMembers.Clear();
            _animatedMembersSnapshot = [];
            _baselineMethodArguments.Clear();
            _animatedQuaternionTargets.Clear();
            _animatedQuaternionTargetsSnapshot = [];
            _initialized = false;
        }

        private static void InitializeMembers(
            AnimationMember member,
            object? parentObject,
            List<AnimationMember> animatedMembers,
            HashSet<Transform> animatedQuaternionTargets)
        {
            object? currentObject = parentObject;

            if (member.MemberType != EAnimationMemberType.Group)
            {
                if (parentObject is Transform transform && IsAnimatedQuaternionComponentMember(member))
                    animatedQuaternionTargets.Add(transform);

                if (member.Initialize is null)
                {
                    currentObject = member.MemberType switch
                    {
                        EAnimationMemberType.Field => member.InitializeField(parentObject),
                        EAnimationMemberType.Property => member.InitializeProperty(parentObject),
                        EAnimationMemberType.Method => member.InitializeMethod(parentObject),
                        _ => parentObject
                    };
                }
                else
                {
                    currentObject = member.Initialize.Invoke(parentObject);
                }
            }

            if (member.Animation is not null || (member.MemberType == EAnimationMemberType.Method && member.AnimatedMethodArgumentIndex >= 0))
                animatedMembers.Add(member);

            if (member.Children.Count == 0)
                return;

            if (member.MemberType != EAnimationMemberType.Group && currentObject is null)
                return;

            foreach (var child in member.Children)
                InitializeMembers(child, currentObject, animatedMembers, animatedQuaternionTargets);
        }

        private void ApplyAnimatedValues()
        {
            if (Animation is null)
                return;

            var snapshot = _animatedMembersSnapshot;
            foreach (var member in snapshot)
            {
                if (member.Animation is null && member.MemberType != EAnimationMemberType.Method)
                {
                    member.ApplyAnimationValue(member.DefaultValue);
                    continue;
                }

                object? defaultValue = member.DefaultValue;
                object? animatedValue = member.GetAnimationValue();
                object? weightedValue = LerpValue(defaultValue, animatedValue, Weight);
                ApplyRuntimeClipRemaps(member, ref weightedValue);
                member.ApplyAnimationValue(weightedValue);
            }

            NormalizeAnimatedQuaternionTargets();
        }

        private bool ShouldDriveSiblingHumanoidPose()
        {
            var humanoid = GetSiblingHumanoid();
            return humanoid?.IsAnimatedPosePreviewActive ?? true;
        }

        private void ApplyRuntimeClipRemaps(AnimationMember member, ref object? value)
        {
            if (member.MemberType != EAnimationMemberType.Method)
                return;

            RestoreBaselineMethodArguments(member);

            switch (member.MemberName)
            {
                case "SetValue":
                case "SetImportedRawValue":
                    RemapHumanoidMuscle(member, ref value);
                    break;
                case "SetAnimatedIKPosition":
                case "SetAnimatedIKPositionX":
                case "SetAnimatedIKPositionY":
                case "SetAnimatedIKPositionZ":
                    RemapAnimatedIKPosition(member, ref value);
                    break;
                case "SetAnimatedIKRotation":
                case "SetAnimatedIKRotationX":
                case "SetAnimatedIKRotationY":
                case "SetAnimatedIKRotationZ":
                case "SetAnimatedIKRotationW":
                    RemapAnimatedIKRotation(member, ref value);
                    break;
            }
        }

        private void RestoreBaselineMethodArguments(AnimationMember member)
        {
            if (!_baselineMethodArguments.TryGetValue(member, out var baseline))
                return;

            var args = member.MethodArguments;
            int count = Math.Min(args.Length, baseline.Length);
            for (int i = 0; i < count; i++)
            {
                if (i == member.AnimatedMethodArgumentIndex)
                    continue;
                args[i] = baseline[i];
            }
        }

        private void RemapHumanoidMuscle(AnimationMember member, ref object? value)
        {
            if (member.MethodArguments.Length == 0)
                return;

            if (value is not float amount)
                return;

            object? muscleArg = member.MethodArguments[0];
            if (!TryGetHumanoidValue(muscleArg, out var humanoidValue))
                return;

            if (FlipMuscleLeftRight)
                humanoidValue = SwapHumanoidLeftRight(humanoidValue);

            member.MethodArguments[0] = ConvertHumanoidArgumentType(muscleArg, humanoidValue);
            if (string.Equals(member.MemberName, "SetImportedRawValue", StringComparison.Ordinal) && member.MethodArguments.Length > 2)
                member.MethodArguments[2] = FlipMuscleZ;
            value = amount;
        }

        private void RemapAnimatedIKPosition(AnimationMember member, ref object? value)
        {
            if (member.MethodArguments.Length == 0)
                return;

            if (TryGetLimbGoal(member.MethodArguments[0], out var goal) && FlipIKPositionLeftRight)
                member.MethodArguments[0] = SwapLimbGoalArgumentType(member.MethodArguments[0], SwapLimbLeftRight(goal));

            if (!FlipIKPositionZ)
                return;

            if (value is Vector3 pos)
            {
                value = new Vector3(pos.X, pos.Y, -pos.Z);
                return;
            }

            if (value is float scalar && member.MemberName == "SetAnimatedIKPositionZ")
                value = -scalar;
        }

        private void RemapAnimatedIKRotation(AnimationMember member, ref object? value)
        {
            if (member.MethodArguments.Length == 0)
                return;

            if (TryGetLimbGoal(member.MethodArguments[0], out var goal) && FlipIKRotationLeftRight)
                member.MethodArguments[0] = SwapLimbGoalArgumentType(member.MethodArguments[0], SwapLimbLeftRight(goal));

            if (!FlipIKRotationZ)
                return;

            if (value is Quaternion rot)
            {
                value = new Quaternion(-rot.X, -rot.Y, rot.Z, rot.W);
                return;
            }

            if (value is float scalar && (member.MemberName == "SetAnimatedIKRotationX" || member.MemberName == "SetAnimatedIKRotationY"))
                value = -scalar;
        }

        private static bool TryGetHumanoidValue(object? arg, out EHumanoidValue value)
        {
            switch (arg)
            {
                case EHumanoidValue v:
                    value = v;
                    return true;
                case int i when Enum.IsDefined(typeof(EHumanoidValue), i):
                    value = (EHumanoidValue)i;
                    return true;
                default:
                    value = default;
                    return false;
            }
        }

        private static object ConvertHumanoidArgumentType(object? originalArg, EHumanoidValue value)
            => originalArg is int ? (int)value : value;

        private static EHumanoidValue SwapHumanoidLeftRight(EHumanoidValue value)
        {
            string name = value.ToString();
            if (name.StartsWith("Left", StringComparison.Ordinal))
            {
                string rightName = "Right" + name[4..];
                if (Enum.TryParse(rightName, out EHumanoidValue rightValue))
                    return rightValue;
            }
            else if (name.StartsWith("Right", StringComparison.Ordinal))
            {
                string leftName = "Left" + name[5..];
                if (Enum.TryParse(leftName, out EHumanoidValue leftValue))
                    return leftValue;
            }

            return value;
        }

        private static bool TryGetLimbGoal(object? arg, out ELimbEndEffector goal)
        {
            if (arg is ELimbEndEffector g)
            {
                goal = g;
                return true;
            }

            goal = default;
            return false;
        }

        private static object SwapLimbGoalArgumentType(object? originalArg, ELimbEndEffector goal)
            => originalArg is ELimbEndEffector ? goal : originalArg ?? goal;

        private static ELimbEndEffector SwapLimbLeftRight(ELimbEndEffector goal)
            => goal switch
            {
                ELimbEndEffector.LeftHand => ELimbEndEffector.RightHand,
                ELimbEndEffector.RightHand => ELimbEndEffector.LeftHand,
                ELimbEndEffector.LeftFoot => ELimbEndEffector.RightFoot,
                ELimbEndEffector.RightFoot => ELimbEndEffector.LeftFoot,
                _ => goal,
            };

        private static object? LerpValue(object? defaultValue, object? animatedValue, float weight) => defaultValue switch
        {
            float df when animatedValue is float af => Interp.Lerp(df, af, weight),
            Vector2 df2 when animatedValue is Vector2 af2 => Vector2.Lerp(df2, af2, weight),
            Vector3 df3 when animatedValue is Vector3 af3 => Vector3.Lerp(df3, af3, weight),
            Vector4 df4 when animatedValue is Vector4 af4 => Vector4.Lerp(df4, af4, weight),
            Quaternion dfq when animatedValue is Quaternion afq => Quaternion.Slerp(dfq, afq, weight),
            _ => weight > 0.5f ? animatedValue : defaultValue,
        };

        private void NormalizeAnimatedQuaternionTargets()
        {
            var snapshot = _animatedQuaternionTargetsSnapshot;
            foreach (var transform in snapshot)
            {
                Quaternion rotation = transform.Rotation;
                float lengthSquared = rotation.LengthSquared();
                if (!float.IsFinite(lengthSquared) || lengthSquared <= float.Epsilon)
                    continue;

                if (MathF.Abs(1.0f - lengthSquared) <= 0.0001f)
                    continue;

                transform.Rotation = Quaternion.Normalize(rotation);
            }
        }

        private static bool IsAnimatedQuaternionComponentMember(AnimationMember member)
            => member.MemberType == EAnimationMemberType.Property
            && (member.MemberName == "QuaternionX"
            || member.MemberName == "QuaternionY"
            || member.MemberName == "QuaternionZ"
            || member.MemberName == "QuaternionW");

        private static float GetInitialPlaybackTime(AnimationClip clip, float speed)
            => speed < 0.0f ? clip.LengthInSeconds : 0.0f;

        private static void StartAllPropertyAnimations(AnimationClip clip, float initialTime)
        {
            foreach (var anim in clip.GetAllAnimations().Values)
            {
                anim.Looped = clip.Looped;
                anim.Start();
                anim.Seek(initialTime, wrapLooped: false);
            }
        }

        private static void SetAllPropertyAnimationTimes(AnimationClip clip, float timeSeconds, bool wrapLooped)
        {
            foreach (var anim in clip.GetAllAnimations().Values)
            {
                anim.Looped = clip.Looped;
                if (anim.State == EAnimationState.Stopped)
                    anim.Start();
                anim.Seek(timeSeconds, wrapLooped);
            }
        }

        private static float NormalizePlaybackTime(float timeSeconds, AnimationClip clip, bool wrapLooped)
        {
            if (clip.LengthInSeconds <= 0.0f)
                return 0.0f;

            return wrapLooped
                ? timeSeconds.RemapToRange(0.0f, clip.LengthInSeconds)
                : Math.Clamp(timeSeconds, 0.0f, clip.LengthInSeconds);
        }

        private static void StopAllPropertyAnimations(AnimationClip clip)
        {
            foreach (var anim in clip.GetAllAnimations().Values)
                anim.Stop();
        }

        protected internal override void OnComponentActivated()
        {
            base.OnComponentActivated();

            if (Animation is not null && StartOnActivate)
                Start();
        }
        protected internal override void OnComponentDeactivated()
        {
            base.OnComponentDeactivated();

            if (Animation is not null)
                Stop();
        }
    }
}
