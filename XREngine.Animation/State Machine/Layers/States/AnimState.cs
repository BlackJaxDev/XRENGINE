using System.Diagnostics;
using MemoryPack;
using XREngine.Animation.Importers;

namespace XREngine.Animation
{
    [MemoryPackable]
    public partial class AnimState : AnimStateBase
    {
        [MemoryPackIgnore]
        internal AnimationValueStore RuntimeValueStore { get; } = new();

        [MemoryPackIgnore]
        internal UnityHumanoidMotionContributionBuffer UnityHumanoidContributions { get; } = new();

        [MemoryPackIgnore]
        internal UnityAnimationEventBuffer UnityAnimationEvents { get; } = new();

        [MemoryPackIgnore]
        private double _normalizedPlaybackTime;

        [MemoryPackIgnore]
        private double _previousNormalizedPlaybackTime;

        [MemoryPackIgnore]
        private ulong _motionLifecycleGeneration;

        [MemoryPackIgnore]
        private bool _isEntered;

        [MemoryPackIgnore]
        internal ulong MotionLifecycleGeneration => _motionLifecycleGeneration;

        [MemoryPackIgnore]
        public double NormalizedPlaybackTime => _normalizedPlaybackTime;

        [MemoryPackIgnore]
        public bool IsEntered => _isEntered;

        public override string ToString()
            => $"AnimState: {Name} / ({Motion?.ToString() ?? "null"})";

        private string _name = "";
        public string Name
        {
            get => _name;
            set => SetField(ref _name, value);
        }

        private List<AnimStateComponent> _components = [];
        [MemoryPackIgnore]
        public List<AnimStateComponent> Components
        {
            get => _components;
            set => SetField(ref _components, value);
        }

        public T AddComponent<T>() where T : AnimStateComponent, new()
        {
            var comp = new T();
            Components.Add(comp);
            return comp;
        }

        private MotionBase? _motion;
        public MotionBase? Motion
        {
            get => _motion;
            set => SetField(ref _motion, value);
        }

        private Guid _motionOccurrenceId = Guid.NewGuid();
        /// <summary>
        /// Persistent identity for this state's independent runtime playback occurrence.
        /// Clip names are deliberately not part of evaluation identity.
        /// </summary>
        public Guid MotionOccurrenceId
        {
            get => _motionOccurrenceId;
            set => SetField(ref _motionOccurrenceId, value == Guid.Empty ? Guid.NewGuid() : value);
        }

        private float _startSecond = 0.0f;
        public float StartSecond
        {
            get => _startSecond;
            set => SetField(ref _startSecond, value);
        }

        private float _endSecond = 0.0f;
        public float EndSecond
        {
            get => _endSecond;
            set => SetField(ref _endSecond, value);
        }

        [MemoryPackConstructor]
        public AnimState() { }
        public AnimState(string name)
            => Name = name;
        public AnimState(MotionBase motion, string name)
        {
            Motion = motion;
            Name = name;
        }
        public AnimState(MotionBase motion)
            => Motion = motion;
        public AnimState(MotionBase motion, params AnimStateTransition[] transitions) : base(transitions)
            => Motion = motion;
        public AnimState(MotionBase motion, IEnumerable<AnimStateTransition> transitions) : base(transitions)
            => Motion = motion;
        public AnimState(MotionBase motion, EventList<AnimStateTransition> transitions) : base(transitions)
            => Motion = motion;

        /// <summary>
        /// Gets & blends the animation values from the motion for this state.
        /// </summary>
        /// <param name="variables"></param>
        public void EvaluateValues(IDictionary<string, AnimVar> variables)
        {
            MotionBase? motion = Motion;
            if (motion is null)
            {
                RuntimeValueStore.Clear();
                UnityHumanoidContributions.Clear();
                return;
            }

            motion.EvaluateAnimationValuesAtNormalizedStateTime(
                variables,
                _normalizedPlaybackTime,
                OwningLayer?.ApplyType == AnimLayer.EApplyType.Additive);
            RuntimeValueStore.CopyFrom(motion.ValueStore);
            UnityHumanoidContributions.Clear();
            motion.CollectUnityHumanoidContributions(
                UnityHumanoidContributions,
                variables,
                _normalizedPlaybackTime,
                1.0f,
                MotionBase.CombineOccurrenceId(0UL, MotionOccurrenceId),
                _motionLifecycleGeneration,
                mirror: false);
        }

        /// <summary>
        /// Advances the property animations in this state's motion by the given delta time.
        /// </summary>
        /// <param name="delta"></param>
        /// <param name="variables"></param>
        public void Tick(float delta, IDictionary<string, AnimVar> variables)
        {
            AdvanceNormalizedPlayback(delta, variables);
            CollectUnityAnimationEvents(variables, includePrevious: false);

            foreach (var component in Components)
                component.StateTick(this, variables, delta);
        }

        public void Tick(long deltaTicks, IDictionary<string, AnimVar> variables)
        {
            float delta = deltaTicks == 0L ? 0.0f : (float)(deltaTicks / (double)Stopwatch.Frequency);
            AdvanceNormalizedPlayback(delta, variables);
            CollectUnityAnimationEvents(variables, includePrevious: false);
            foreach (var component in Components)
                component.StateTick(this, variables, delta);
        }

        public void OnEnter(IDictionary<string, AnimVar> variables)
        {
            if (_isEntered)
                return;

            _isEntered = true;
            foreach (var component in Components)
                component.StateEntered(this, variables);
        }
        
        public void OnExit(IDictionary<string, AnimVar> variables)
        {
            if (!_isEntered)
                return;

            _isEntered = false;
            foreach (var component in Components)
                component.StateExited(this, variables);
        }

        internal void RestartMotionPlayback(
            IDictionary<string, AnimVar> variables,
            float normalizedOffset = 0.0f)
        {
            double duration = GetEffectiveDurationSeconds(variables);
            double startNormalized = duration > double.Epsilon && double.IsFinite(duration)
                ? StartSecond / duration
                : 0.0;
            if (float.IsFinite(normalizedOffset))
                startNormalized += normalizedOffset;

            _normalizedPlaybackTime = startNormalized;
            _previousNormalizedPlaybackTime = startNormalized;
            _motionLifecycleGeneration = unchecked(_motionLifecycleGeneration + 1UL);
            Motion?.RestartPlayback(0.0f);
            Motion?.SeekPlaybackFromNormalizedStateTime(startNormalized);
        }

        internal void SeekMotionPlayback(
            float timeSeconds,
            IDictionary<string, AnimVar> variables,
            bool collectEvents = false)
        {
            double duration = GetEffectiveDurationSeconds(variables);
            double normalizedTime = duration > double.Epsilon && double.IsFinite(duration)
                ? timeSeconds / duration
                : 0.0;
            _previousNormalizedPlaybackTime = _normalizedPlaybackTime;
            _normalizedPlaybackTime = normalizedTime;
            if (collectEvents)
                CollectUnityAnimationEvents(variables, includePrevious: false);
            else
            {
                _previousNormalizedPlaybackTime = normalizedTime;
                UnityAnimationEvents.Clear();
            }
            _motionLifecycleGeneration = unchecked(_motionLifecycleGeneration + 1UL);
            Motion?.SeekPlaybackFromNormalizedStateTime(normalizedTime);
        }

        internal void StopMotionPlayback()
            => Motion?.StopPlayback();

        internal void PrepareRuntimeEvaluation(AnimationSlotLayout layout, int contributionCapacity)
        {
            RuntimeValueStore.Resize(layout);
            UnityHumanoidContributions.EnsureCapacity(contributionCapacity);
            UnityAnimationEvents.EnsureCapacity(Motion?.GetUnityAnimationEventCapacity() ?? 0);
        }

        internal double GetEffectiveDurationSeconds(IDictionary<string, AnimVar> variables)
            => Motion?.GetEffectiveDurationSeconds(variables) ?? 0.0;

        internal bool HasCrossedExitTime(float exitTime)
        {
            if (!float.IsFinite(exitTime))
                return false;

            double previous = _previousNormalizedPlaybackTime;
            double current = _normalizedPlaybackTime;
            if (current == previous)
                return false;

            bool forward = current > previous;
            if (exitTime >= 1.0f)
                return forward
                    ? previous < exitTime && current >= exitTime
                    : previous > exitTime && current <= exitTime;

            if (forward)
            {
                double threshold = Math.Floor(previous - exitTime) + 1.0 + exitTime;
                return threshold <= current;
            }

            double reverseThreshold = Math.Ceiling(previous - exitTime) - 1.0 + exitTime;
            return reverseThreshold >= current;
        }

        private void AdvanceNormalizedPlayback(float deltaSeconds, IDictionary<string, AnimVar> variables)
        {
            _previousNormalizedPlaybackTime = _normalizedPlaybackTime;
            if (!float.IsFinite(deltaSeconds) || deltaSeconds == 0.0f)
                return;

            double duration = GetEffectiveDurationSeconds(variables);
            if (!(duration > double.Epsilon) || !double.IsFinite(duration))
                return;

            double next = _normalizedPlaybackTime + deltaSeconds / duration;
            _normalizedPlaybackTime = double.IsFinite(next)
                ? next
                : Math.CopySign(double.MaxValue, next);
        }

        private void CollectUnityAnimationEvents(
            IDictionary<string, AnimVar> variables,
            bool includePrevious)
        {
            UnityAnimationEvents.Clear();
            Motion?.CollectUnityAnimationEvents(
                UnityAnimationEvents,
                variables,
                _previousNormalizedPlaybackTime,
                _normalizedPlaybackTime,
                includePrevious,
                weight: 1.0f,
                MotionBase.CombineOccurrenceId(0UL, MotionOccurrenceId));
        }

        public void Initialize(AnimLayer layer, AnimStateMachine owner, object? rootObject)
        {
            OwningLayer = layer;
            Motion?.Initialize(layer, owner, rootObject);
        }

        public void Deinitialize()
        {
            _isEntered = false;
            OwningLayer = null;
            Motion?.Deinitialize();
        }
    }
}
