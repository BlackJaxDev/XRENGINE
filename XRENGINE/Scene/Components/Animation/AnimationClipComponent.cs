using System.Numerics;
using XREngine.Animation;
using XREngine.Animation.IK;
using XREngine.Components;
using XREngine.Data;
using XREngine.Data.Animation;
using XREngine.Scene.Transforms;
using Extensions;
using System.Diagnostics;

namespace XREngine.Components.Animation
{
    [XRComponentEditor("XREngine.Editor.ComponentEditors.AnimationClipComponentEditor")]
    public class AnimationClipComponent : XRComponent
    {
        private bool _initialized;
        private bool _isPlaying;
        private bool _isPaused;
        private readonly List<AnimationMember> _animatedMembers = [];
        private AnimationMember[] _animatedMembersSnapshot = [];
        private readonly Dictionary<AnimationMember, object?[]> _baselineMethodArguments = [];
        private readonly HashSet<Transform> _animatedQuaternionTargets = [];
        private Transform[] _animatedQuaternionTargetsSnapshot = [];
        private BasePropAnim[] _propertyAnimationsSnapshot = [];

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

        private long _playbackTimeTicks;

        public bool IsPlaying => _isPlaying;
        public bool IsPaused => _isPaused;

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
            if (Animation is null || _isPlaying)
                return;

            _isPlaying = true;

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
            long initialTicks = GetInitialPlaybackTicks(Animation, Speed);
            StartAllPropertyAnimations(Animation, initialTicks);
            SetPlaybackTimeTicks(initialTicks);
            ApplyAnimatedValues();

            RegisterTick(ETickGroup.Normal, ETickOrder.Animation, TickAnimation);
        }

        private void Stop()
        {
            _isPlaying = false;
            _isPaused = false;
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

            long evaluationTicks = NormalizePlaybackTime(SecondsToStopwatchTicks(timeSeconds), Animation, wrapLooped: false);
            SetAllPropertyAnimationTimes(Animation, evaluationTicks, wrapLooped: false);
            SetPlaybackTimeTicks(evaluationTicks);

            if (!ShouldDriveSiblingHumanoidPose())
                return;

            ApplyAnimatedValues();

            if (Animation.HasMuscleChannels)
            {
                var humanoid = GetSiblingHumanoid();
                humanoid?.ApplyCurrentMusclePose();
            }
        }

        public void Play()
        {
            if (_isPlaying || _isPaused)
                Stop();
            Start();
        }

        public void StopPlayback()
            => Stop();

        public void Pause()
        {
            if (!_isPlaying || _isPaused)
                return;
            _isPaused = true;
            UnregisterTick(ETickGroup.Normal, ETickOrder.Animation, TickAnimation);
        }

        public void Resume()
        {
            if (!_isPlaying || !_isPaused)
                return;
            _isPaused = false;
            RegisterTick(ETickGroup.Normal, ETickOrder.Animation, TickAnimation);
        }

        private void TickAnimation()
        {
            if (Animation is null || !_initialized)
                return;

            using var sample = Engine.Profiler.Start("AnimationClipComponent.TickAnimation");

            long deltaTicks;
            long playbackTimeTicks;
            using (Engine.Profiler.Start("AnimationClipComponent.TickAnimation.AdvanceTime"))
            {
                deltaTicks = ScaleStopwatchTicks(SecondsToStopwatchTicks(Engine.Delta), Speed);
                playbackTimeTicks = NormalizePlaybackTime(_playbackTimeTicks + deltaTicks, Animation, wrapLooped: Animation.Looped);
                SetPlaybackTimeTicks(playbackTimeTicks);
            }

            using (Engine.Profiler.Start("AnimationClipComponent.TickAnimation.SetPropertyTimes"))
                SetAllPropertyAnimationTimes(Animation, playbackTimeTicks, wrapLooped: false);

            if (!ShouldDriveSiblingHumanoidPose())
                return;

            using (Engine.Profiler.Start("AnimationClipComponent.TickAnimation.ApplyAnimatedValues"))
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

            // Cache the concrete track list once; rebuilding the clip animation map each tick
            // adds avoidable traversal and allocations to the playback hot path.
            List<BasePropAnim> propertyAnimations = new(_animatedMembersSnapshot.Length);
            foreach (var member in _animatedMembersSnapshot)
            {
                if (member.Animation is BasePropAnim animation)
                    propertyAnimations.Add(animation);
            }
            _propertyAnimationsSnapshot = [.. propertyAnimations];

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
            _propertyAnimationsSnapshot = [];
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

            using var sample = Engine.Profiler.Start("AnimationClipComponent.ApplyAnimatedValues");

            var snapshot = _animatedMembersSnapshot;
            float weight = Weight;
            bool fullWeight = weight >= 1.0f;

            foreach (var member in snapshot)
            {
                if (member.Animation is null && member.MemberType != EAnimationMemberType.Method)
                {
                    member.ApplyAnimationValue(member.DefaultValue);
                    continue;
                }

                if (TryApplyTypedAnimatedValue(member, fullWeight, weight))
                    continue;

                object? animatedValue = member.GetAnimationValue();

                if (fullWeight)
                {
                    // Fast path: skip LerpValue entirely — lerp(x, y, 1.0) == y.
                    // ApplyRuntimeClipRemaps only affects Method members, so skip for field/property.
                    if (member.MemberType == EAnimationMemberType.Method)
                        ApplyRuntimeClipRemaps(member, ref animatedValue);
                    member.ApplyAnimationValue(animatedValue);
                }
                else
                {
                    object? defaultValue = member.DefaultValue;
                    object? weightedValue = LerpValue(defaultValue, animatedValue, weight);
                    ApplyRuntimeClipRemaps(member, ref weightedValue);
                    member.ApplyAnimationValue(weightedValue);
                }
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
            if (member.MemberType != EAnimationMemberType.Method || !ShouldApplyRuntimeClipRemap(member.MemberName))
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

        private bool TryApplyTypedAnimatedValue(AnimationMember member, bool fullWeight, float weight)
            => TryApplyTypedFloat(member, fullWeight, weight)
            || TryApplyTypedVector3(member, fullWeight, weight)
            || TryApplyTypedQuaternion(member, fullWeight, weight)
            || TryApplyTypedVector2(member, fullWeight, weight)
            || TryApplyTypedVector4(member, fullWeight, weight)
            || TryApplyTypedBool(member, fullWeight, weight);

        private bool TryApplyTypedFloat(AnimationMember member, bool fullWeight, float weight)
        {
            if (!TryGetAnimatedFloat(member, out float animatedValue))
                return false;

            float value = animatedValue;
            if (!fullWeight)
            {
                if (member.DefaultValue is not float defaultValue)
                    return false;
                value = Interp.Lerp(defaultValue, animatedValue, weight);
            }

            if (member.MemberType == EAnimationMemberType.Method && ShouldApplyRuntimeClipRemap(member.MemberName))
            {
                RestoreBaselineMethodArguments(member);
                switch (member.MemberName)
                {
                    case "SetValue":
                    case "SetImportedRawValue":
                        RemapHumanoidMuscle(member, ref value);
                        break;
                    case "SetAnimatedIKPositionX":
                    case "SetAnimatedIKPositionY":
                    case "SetAnimatedIKPositionZ":
                        RemapAnimatedIKPosition(member, ref value);
                        break;
                    case "SetAnimatedIKRotationX":
                    case "SetAnimatedIKRotationY":
                    case "SetAnimatedIKRotationZ":
                    case "SetAnimatedIKRotationW":
                        RemapAnimatedIKRotation(member, ref value);
                        break;
                }
            }

            return member.TryApplyFloat(value);
        }

        private bool TryApplyTypedBool(AnimationMember member, bool fullWeight, float weight)
        {
            if (!TryGetAnimatedBool(member, out bool animatedValue))
                return false;

            bool value = animatedValue;
            if (!fullWeight)
            {
                if (member.DefaultValue is not bool defaultValue)
                    return false;
                value = weight > 0.5f ? animatedValue : defaultValue;
            }

            return member.TryApplyBool(value);
        }

        private bool TryApplyTypedVector2(AnimationMember member, bool fullWeight, float weight)
        {
            if (!TryGetAnimatedVector2(member, out Vector2 animatedValue))
                return false;

            Vector2 value = animatedValue;
            if (!fullWeight)
            {
                if (member.DefaultValue is not Vector2 defaultValue)
                    return false;
                value = Vector2.Lerp(defaultValue, animatedValue, weight);
            }

            return member.TryApplyVector2(value);
        }

        private bool TryApplyTypedVector3(AnimationMember member, bool fullWeight, float weight)
        {
            if (!TryGetAnimatedVector3(member, out Vector3 animatedValue))
                return false;

            Vector3 value = animatedValue;
            if (!fullWeight)
            {
                if (member.DefaultValue is not Vector3 defaultValue)
                    return false;
                value = Vector3.Lerp(defaultValue, animatedValue, weight);
            }

            if (member.MemberType == EAnimationMemberType.Method && ShouldApplyRuntimeClipRemap(member.MemberName) && member.MemberName == "SetAnimatedIKPosition")
            {
                RestoreBaselineMethodArguments(member);
                RemapAnimatedIKPosition(member, ref value);
            }

            return member.TryApplyVector3(value);
        }

        private bool TryApplyTypedVector4(AnimationMember member, bool fullWeight, float weight)
        {
            if (!TryGetAnimatedVector4(member, out Vector4 animatedValue))
                return false;

            Vector4 value = animatedValue;
            if (!fullWeight)
            {
                if (member.DefaultValue is not Vector4 defaultValue)
                    return false;
                value = Vector4.Lerp(defaultValue, animatedValue, weight);
            }

            return member.TryApplyVector4(value);
        }

        private bool TryApplyTypedQuaternion(AnimationMember member, bool fullWeight, float weight)
        {
            if (!TryGetAnimatedQuaternion(member, out Quaternion animatedValue))
                return false;

            Quaternion value = animatedValue;
            if (!fullWeight)
            {
                if (member.DefaultValue is not Quaternion defaultValue)
                    return false;
                value = Quaternion.Slerp(defaultValue, animatedValue, weight);
            }

            if (member.MemberType == EAnimationMemberType.Method && ShouldApplyRuntimeClipRemap(member.MemberName) && member.MemberName == "SetAnimatedIKRotation")
            {
                RestoreBaselineMethodArguments(member);
                RemapAnimatedIKRotation(member, ref value);
            }

            return member.TryApplyQuaternion(value);
        }

        private static bool TryGetAnimatedFloat(AnimationMember member, out float value)
        {
            switch (member.Animation)
            {
                case PropAnimFloat animation:
                    value = animation.CurrentPosition;
                    return true;
                case PropAnimMethod<float> animation when TryGetMethodAnimationValue(animation, out value):
                    return true;
                default:
                    value = default;
                    return false;
            }
        }

        private static bool TryGetAnimatedBool(AnimationMember member, out bool value)
        {
            switch (member.Animation)
            {
                case PropAnimBool animation:
                    value = animation.GetValue(animation.CurrentTime);
                    return true;
                case PropAnimMethod<bool> animation when TryGetMethodAnimationValue(animation, out value):
                    return true;
                default:
                    value = default;
                    return false;
            }
        }

        private static bool TryGetAnimatedVector2(AnimationMember member, out Vector2 value)
        {
            switch (member.Animation)
            {
                case PropAnimVector2 animation:
                    value = animation.CurrentPosition;
                    return true;
                case PropAnimMethod<Vector2> animation when TryGetMethodAnimationValue(animation, out value):
                    return true;
                default:
                    value = default;
                    return false;
            }
        }

        private static bool TryGetAnimatedVector3(AnimationMember member, out Vector3 value)
        {
            switch (member.Animation)
            {
                case PropAnimVector3 animation:
                    value = animation.CurrentPosition;
                    return true;
                case PropAnimMethod<Vector3> animation when TryGetMethodAnimationValue(animation, out value):
                    return true;
                default:
                    value = default;
                    return false;
            }
        }

        private static bool TryGetAnimatedVector4(AnimationMember member, out Vector4 value)
        {
            switch (member.Animation)
            {
                case PropAnimVector4 animation:
                    value = animation.CurrentPosition;
                    return true;
                case PropAnimMethod<Vector4> animation when TryGetMethodAnimationValue(animation, out value):
                    return true;
                default:
                    value = default;
                    return false;
            }
        }

        private static bool TryGetAnimatedQuaternion(AnimationMember member, out Quaternion value)
        {
            switch (member.Animation)
            {
                case PropAnimQuaternion animation:
                    value = animation.CurrentValue;
                    return true;
                case PropAnimMethod<Quaternion> animation when TryGetMethodAnimationValue(animation, out value):
                    return true;
                default:
                    value = default;
                    return false;
            }
        }

        private static bool TryGetMethodAnimationValue<T>(PropAnimMethod<T> animation, out T value)
        {
            if (animation.GetValue is { } getValue)
            {
                T? currentValue = getValue(animation.CurrentTime);
                if (currentValue is T typedValue)
                {
                    value = typedValue;
                    return true;
                }
            }

            if (animation.DefaultValue is T defaultValue)
            {
                value = defaultValue;
                return true;
            }

            value = default!;
            return false;
        }

        private static bool RequiresRuntimeClipRemap(string memberName)
            => memberName is "SetValue"
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

        private bool ShouldApplyRuntimeClipRemap(string memberName)
            => memberName switch
            {
                "SetValue" or "SetImportedRawValue"
                    => FlipMuscleLeftRight || FlipMuscleZ,
                "SetAnimatedIKPosition"
                or "SetAnimatedIKPositionX"
                or "SetAnimatedIKPositionY"
                or "SetAnimatedIKPositionZ"
                    => FlipIKPositionLeftRight || FlipIKPositionZ,
                "SetAnimatedIKRotation"
                or "SetAnimatedIKRotationX"
                or "SetAnimatedIKRotationY"
                or "SetAnimatedIKRotationZ"
                or "SetAnimatedIKRotationW"
                    => FlipIKRotationLeftRight || FlipIKRotationZ,
                _ => false,
            };

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

        private void RemapHumanoidMuscle(AnimationMember member, ref float value)
        {
            if (member.MethodArguments.Length == 0)
                return;

            object? muscleArg = member.MethodArguments[0];
            if (!TryGetHumanoidValue(muscleArg, out var humanoidValue))
                return;

            if (FlipMuscleLeftRight)
                humanoidValue = SwapHumanoidLeftRight(humanoidValue);

            member.MethodArguments[0] = ConvertHumanoidArgumentType(muscleArg, humanoidValue);
            if (string.Equals(member.MemberName, "SetImportedRawValue", StringComparison.Ordinal) && member.MethodArguments.Length > 2)
                member.MethodArguments[2] = FlipMuscleZ;
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

        private void RemapAnimatedIKPosition(AnimationMember member, ref Vector3 value)
        {
            if (member.MethodArguments.Length == 0)
                return;

            if (TryGetLimbGoal(member.MethodArguments[0], out var goal) && FlipIKPositionLeftRight)
                member.MethodArguments[0] = SwapLimbGoalArgumentType(member.MethodArguments[0], SwapLimbLeftRight(goal));

            if (FlipIKPositionZ)
                value = new Vector3(value.X, value.Y, -value.Z);
        }

        private void RemapAnimatedIKPosition(AnimationMember member, ref float value)
        {
            if (member.MethodArguments.Length == 0)
                return;

            if (TryGetLimbGoal(member.MethodArguments[0], out var goal) && FlipIKPositionLeftRight)
                member.MethodArguments[0] = SwapLimbGoalArgumentType(member.MethodArguments[0], SwapLimbLeftRight(goal));

            if (FlipIKPositionZ && member.MemberName == "SetAnimatedIKPositionZ")
                value = -value;
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

        private void RemapAnimatedIKRotation(AnimationMember member, ref Quaternion value)
        {
            if (member.MethodArguments.Length == 0)
                return;

            if (TryGetLimbGoal(member.MethodArguments[0], out var goal) && FlipIKRotationLeftRight)
                member.MethodArguments[0] = SwapLimbGoalArgumentType(member.MethodArguments[0], SwapLimbLeftRight(goal));

            if (FlipIKRotationZ)
                value = new Quaternion(-value.X, -value.Y, value.Z, value.W);
        }

        private void RemapAnimatedIKRotation(AnimationMember member, ref float value)
        {
            if (member.MethodArguments.Length == 0)
                return;

            if (TryGetLimbGoal(member.MethodArguments[0], out var goal) && FlipIKRotationLeftRight)
                member.MethodArguments[0] = SwapLimbGoalArgumentType(member.MethodArguments[0], SwapLimbLeftRight(goal));

            if (FlipIKRotationZ && (member.MemberName == "SetAnimatedIKRotationX" || member.MemberName == "SetAnimatedIKRotationY"))
                value = -value;
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

        private static readonly Dictionary<EHumanoidValue, EHumanoidValue> _humanoidLeftRightSwapCache = BuildHumanoidLeftRightSwapCache();

        private static Dictionary<EHumanoidValue, EHumanoidValue> BuildHumanoidLeftRightSwapCache()
        {
            var cache = new Dictionary<EHumanoidValue, EHumanoidValue>();
            foreach (EHumanoidValue v in Enum.GetValues<EHumanoidValue>())
            {
                string name = v.ToString();
                if (name.StartsWith("Left", StringComparison.Ordinal))
                {
                    string rightName = "Right" + name[4..];
                    if (Enum.TryParse(rightName, out EHumanoidValue rightValue))
                    {
                        cache[v] = rightValue;
                        cache[rightValue] = v;
                    }
                }
            }
            return cache;
        }

        private static EHumanoidValue SwapHumanoidLeftRight(EHumanoidValue value)
            => _humanoidLeftRightSwapCache.TryGetValue(value, out var swapped) ? swapped : value;

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

        private static long GetInitialPlaybackTicks(AnimationClip clip, float speed)
            => speed < 0.0f ? GetClipLengthTicks(clip) : 0L;

        private void StartAllPropertyAnimations(AnimationClip clip, long initialTimeTicks)
        {
            var animations = _propertyAnimationsSnapshot;
            for (int i = 0; i < animations.Length; i++)
            {
                var anim = animations[i];
                anim.Looped = clip.Looped;
                anim.Start();
                anim.Seek(initialTimeTicks, wrapLooped: false);
            }
        }

        private void SetAllPropertyAnimationTimes(AnimationClip clip, float timeSeconds, bool wrapLooped)
            => SetAllPropertyAnimationTimes(clip, SecondsToStopwatchTicks(timeSeconds), wrapLooped);

        private void SetAllPropertyAnimationTimes(AnimationClip clip, long timeTicks, bool wrapLooped)
        {
            var animations = _propertyAnimationsSnapshot;
            for (int i = 0; i < animations.Length; i++)
            {
                var anim = animations[i];
                anim.Looped = clip.Looped;
                if (anim.State == EAnimationState.Stopped)
                    anim.Start();
                anim.Seek(timeTicks, wrapLooped);
            }
        }

        private static float NormalizePlaybackTime(float timeSeconds, AnimationClip clip, bool wrapLooped)
            => StopwatchTicksToSeconds(NormalizePlaybackTime(SecondsToStopwatchTicks(timeSeconds), clip, wrapLooped));

        private static long NormalizePlaybackTime(long timeTicks, AnimationClip clip, bool wrapLooped)
        {
            long clipLengthTicks = GetClipLengthTicks(clip);
            if (clipLengthTicks <= 0L)
                return 0L;

            return wrapLooped
                ? WrapStopwatchTicks(timeTicks, clipLengthTicks)
                : Math.Clamp(timeTicks, 0L, clipLengthTicks);
        }

        private void SetPlaybackTimeTicks(long playbackTimeTicks)
        {
            _playbackTimeTicks = Math.Max(0L, playbackTimeTicks);
            // Bypass SetField — PlaybackTime fires change notifications every frame for no subscribers.
            _playbackTime = StopwatchTicksToSeconds(_playbackTimeTicks);
        }

        private static long GetClipLengthTicks(AnimationClip clip)
            => clip.LengthInSeconds <= 0.0f
                ? 0L
                : SecondsToStopwatchTicks(clip.LengthInSeconds);

        private static long SecondsToStopwatchTicks(double seconds)
            => !double.IsFinite(seconds) || seconds == 0.0
                ? 0L
                : (long)Math.Round(seconds * Stopwatch.Frequency);

        private static float StopwatchTicksToSeconds(long ticks)
            => (float)(ticks / (double)Stopwatch.Frequency);

        private static long ScaleStopwatchTicks(long deltaTicks, float speed)
            => deltaTicks == 0L || !float.IsFinite(speed) || speed == 0.0f
                ? 0L
                : (long)Math.Round(deltaTicks * (double)speed);

        private static long WrapStopwatchTicks(long valueTicks, long lengthTicks)
        {
            if (lengthTicks <= 0L)
                return 0L;

            long wrappedTicks = valueTicks % lengthTicks;
            if (wrappedTicks < 0L)
                wrappedTicks += lengthTicks;
            return wrappedTicks;
        }

        private void StopAllPropertyAnimations(AnimationClip clip)
        {
            var animations = _propertyAnimationsSnapshot;
            for (int i = 0; i < animations.Length; i++)
            {
                var anim = animations[i];
                anim.Stop();
            }
        }

        protected override void OnComponentActivated()
        {
            base.OnComponentActivated();

            if (Animation is not null && StartOnActivate)
                Start();
        }
        protected override void OnComponentDeactivated()
        {
            base.OnComponentDeactivated();

            if (Animation is not null)
                Stop();
        }
    }
}
