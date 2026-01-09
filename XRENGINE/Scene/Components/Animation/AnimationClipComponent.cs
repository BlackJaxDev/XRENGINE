using XREngine.Animation;
using XREngine.Components;

namespace XREngine.Components.Animation
{
    public class AnimationClipComponent : XRComponent
    {
        private AnimStateMachine? _stateMachine;
        private AnimLayer? _layer;
        private AnimState? _state;
        private bool _initialized;

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

            EnsurePlaybackGraph();

            // Bind members to this component/SceneNode via the anim state machine.
            if (!_initialized)
            {
                _stateMachine!.Initialize(this);
                _initialized = true;
            }

            // Start the underlying property animations so Tick() advances them.
            StartAllPropertyAnimations(Animation);

            RegisterTick(ETickGroup.Normal, ETickOrder.Animation, TickAnimation);
        }

        private void Stop()
        {
            if (Animation is not null)
                StopAllPropertyAnimations(Animation);

            UnregisterTick(ETickGroup.Normal, ETickOrder.Animation, TickAnimation);

            if (_initialized)
            {
                _stateMachine?.Deinitialize();
                _initialized = false;
            }
        }

        private void TickAnimation()
        {
            if (Animation is null || !_initialized)
                return;

            // Keep layer weight in sync with component weight.
            if (_layer is not null)
                _layer.Weight = Weight;

            _stateMachine?.EvaluationTick(this, Engine.Delta * Speed);
        }

        private void EnsurePlaybackGraph()
        {
            if (Animation is null)
                return;

            if (_stateMachine is not null && _state?.Motion == Animation)
                return;

            _stateMachine = new AnimStateMachine();
            _layer = new AnimLayer();
            _state = new AnimState(Animation, "Clip");

            _layer.States = [_state];
            _layer.InitialState = _state;
            _stateMachine.Layers = [_layer];

            _initialized = false;
        }

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
