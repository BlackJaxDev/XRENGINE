using System.Numerics;
using XREngine.Animation;
using XREngine.Components;
using XREngine.Data;

namespace XREngine.Components.Animation
{
    public class AnimationClipComponent : XRComponent
    {
        private bool _initialized;
        private readonly List<AnimationMember> _animatedMembers = [];

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

        private bool _suspendSiblingStateMachine = true;
        public bool SuspendSiblingStateMachine
        {
            get => _suspendSiblingStateMachine;
            set => SetField(ref _suspendSiblingStateMachine, value);
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

            EnsureInitialized();

            // Bind members to this component/SceneNode via the anim state machine.
            // Start the underlying property animations so Tick() advances them.
            StartAllPropertyAnimations(Animation);

            RegisterTick(ETickGroup.Normal, ETickOrder.Animation, TickAnimation);
        }

        private void Stop()
        {
            if (Animation is not null)
                StopAllPropertyAnimations(Animation);

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

        private void TickAnimation()
        {
            if (Animation is null || !_initialized)
                return;

            float delta = Engine.Delta * Speed;
            foreach (var member in _animatedMembers)
                member.Animation?.Tick(delta);

            ApplyAnimatedValues();
        }

        private void EnsureInitialized()
        {
            if (_initialized || Animation?.RootMember is null)
                return;

            _animatedMembers.Clear();
            InitializeMembers(Animation.RootMember, this, _animatedMembers);
            _initialized = true;
        }

        private void Deinitialize()
        {
            _animatedMembers.Clear();
            _initialized = false;
        }

        private static void InitializeMembers(AnimationMember member, object? parentObject, List<AnimationMember> animatedMembers)
        {
            object? currentObject = parentObject;

            if (member.MemberType != EAnimationMemberType.Group)
            {
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
                InitializeMembers(child, currentObject, animatedMembers);
        }

        private void ApplyAnimatedValues()
        {
            if (Animation is null)
                return;

            foreach (var member in _animatedMembers)
            {
                if (member.Animation is null && member.MemberType != EAnimationMemberType.Method)
                {
                    member.ApplyAnimationValue(member.DefaultValue);
                    continue;
                }

                object? defaultValue = member.DefaultValue;
                object? animatedValue = member.GetAnimationValue();
                object? weightedValue = LerpValue(defaultValue, animatedValue, Weight);
                member.ApplyAnimationValue(weightedValue);
            }
        }

        private static object? LerpValue(object? defaultValue, object? animatedValue, float weight) => defaultValue switch
        {
            float df when animatedValue is float af => Interp.Lerp(df, af, weight),
            Vector2 df2 when animatedValue is Vector2 af2 => Vector2.Lerp(df2, af2, weight),
            Vector3 df3 when animatedValue is Vector3 af3 => Vector3.Lerp(df3, af3, weight),
            Vector4 df4 when animatedValue is Vector4 af4 => Vector4.Lerp(df4, af4, weight),
            Quaternion dfq when animatedValue is Quaternion afq => Quaternion.Slerp(dfq, afq, weight),
            _ => weight > 0.5f ? animatedValue : defaultValue,
        };

        private static void StartAllPropertyAnimations(AnimationClip clip)
        {
            foreach (var anim in clip.GetAllAnimations().Values)
            {
                anim.Looped = clip.Looped;
                anim.Start();
                // Force an initial value evaluation at t=0.
                anim.CurrentTime = 0.0f;
            }
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
