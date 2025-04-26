using XREngine.Data.Core;

namespace XREngine.Animation
{
    /// <summary>
    /// Describes a condition and how to transition to a new state.
    /// </summary>
    public class AnimStateTransition : XRBase
    {
        public override string ToString()
            => $"AnimStateTransition: {Name} ({DestinationState?.ToString() ?? "null"})";

        //[Browsable(false)]
        public AnimStateBase? Owner { get; internal set; }

        public event Action? Started;
        public event Action? Finished;

        private AnimState? _destinationState;
        /// <summary>
        /// The index of the next state to go to if this transition's condition method returns true.
        /// </summary>
        public AnimState? DestinationState
        {
            get => _destinationState;
            set
            {
                ArgumentNullException.ThrowIfNull(value);
                SetField(ref _destinationState, value);
            }
        }

        private List<AnimTransitionCondition> _conditions = [];
        /// <summary>
        /// The condition to test if this transition should occur; run every frame.
        /// </summary>
        public List<AnimTransitionCondition> Conditions
        {
            get => _conditions;
            set => SetField(ref _conditions, value);
        }

        private float _blendDuration = 0.0f;
        /// <summary>
        /// How quickly the current state should blend into the next, in seconds.
        /// </summary>
        public float BlendDuration
        {
            get => _blendDuration;
            set => SetField(ref _blendDuration, value);
        }

        private EAnimBlendType _blendType = EAnimBlendType.Linear;
        /// <summary>
        /// The interpolation method to use to blend to the next state.
        /// </summary>
        public EAnimBlendType BlendType
        {
            get => _blendType;
            set => SetField(ref _blendType, value);
        }

        private PropAnimFloat? _customBlendFunction;
        /// <summary>
        /// If <see cref="BlendType"/> == <see cref="EAnimBlendType.Custom"/>, 
        /// uses these keyframes to interpolate between 0.0f and 1.0f.
        /// </summary>
        public PropAnimFloat? CustomBlendFunction
        {
            get => _customBlendFunction;
            set => SetField(ref _customBlendFunction, value);
        }

        private int _priority = 0;
        /// <summary>
        /// If multiple transitions evaluate to true at the same time, this dictates which transition will occur.
        /// </summary>
        public int Priority
        {
            get => _priority;
            set => SetField(ref _priority, value);
        }

        private string _name = "";
        public string Name
        {
            get => _name;
            set => SetField(ref _name, value);
        }

        private float _exitTime = 0.0f;
        public float ExitTime
        {
            get => _exitTime;
            set => SetField(ref _exitTime, value);
        }

        private bool _fixedDuration = true;
        public bool FixedDuration
        {
            get => _fixedDuration;
            set => SetField(ref _fixedDuration, value);
        }

        private float _transitionOffset = 0.0f;
        public float TransitionOffset
        {
            get => _transitionOffset;
            set => SetField(ref _transitionOffset, value);
        }

        private ETransitionInterruptionSource _interruptionSource = ETransitionInterruptionSource.Neither;
        public ETransitionInterruptionSource InterruptionSource
        {
            get => _interruptionSource;
            set => SetField(ref _interruptionSource, value);
        }

        private bool _orderedInterruption = true;
        public bool OrderedInterruption
        {
            get => _orderedInterruption;
            set => SetField(ref _orderedInterruption, value);
        }

        private bool _canTransitionToSelf = false;
        public bool CanTransitionToSelf
        {
            get => _canTransitionToSelf;
            set => SetField(ref _canTransitionToSelf, value);
        }

        internal void OnFinished()
            => Finished?.Invoke();
        internal void OnStarted()
            => Started?.Invoke();

        public bool AllConditionsValid(IDictionary<string, AnimVar> variables)
            => Conditions.Count == 0 || Conditions.All(x => x.Evaluate(variables));

        public bool NameEquals(string name, StringComparison comp = StringComparison.Ordinal)
            => string.Equals(Name, name, comp);
    }
}
