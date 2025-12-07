using System.ComponentModel;
using System.Numerics;
using MemoryPack;
using XREngine.Data.Core;

namespace XREngine.Animation
{
    public abstract partial class AnimStateBase : XRBase
    {
        public AnimStateBase(params AnimStateTransition[] transitions)
            => Transitions = [.. transitions];
        public AnimStateBase(IEnumerable<AnimStateTransition> transitions)
            => Transitions = [.. transitions];
        public AnimStateBase(EventList<AnimStateTransition> transitions)
            => Transitions = transitions;

        public AnimStateBase() { }

        [Browsable(false)]
        [MemoryPackIgnore]
        public AnimLayer? OwningLayer { get; internal set; }

        private EventList<AnimStateTransition> _transitions = [];
        /// <summary>
        /// All possible transitions to move out of this state and into another state.
        /// </summary>
        public EventList<AnimStateTransition> Transitions
        {
            get => _transitions;
            set
            {
                if (_transitions != null)
                {
                    _transitions.PostAnythingAdded -= Transitions_PostAnythingAdded;
                    _transitions.PostAnythingRemoved -= Transitions_PostAnythingRemoved;
                }
                _transitions = value ?? [];
                _transitions.PostAnythingAdded += Transitions_PostAnythingAdded;
                _transitions.PostAnythingRemoved += Transitions_PostAnythingRemoved;
                foreach (AnimStateTransition transition in _transitions)
                    Transitions_PostAnythingAdded(transition);
            }
        }

        private Vector2 _position;
        public Vector2 Position
        {
            get => _position;
            set => SetField(ref _position, value);
        }

        /// <summary>
        /// Attempts to find any transitions that evaluate to true and returns the one with the highest priority.
        /// </summary>
        public AnimStateTransition? TryTransition(IDictionary<string, AnimVar> variables)
            => Transitions.
                FindAll(x => x.AllConditionsValid(variables)).
                OrderBy(x => x.Priority).
                FirstOrDefault();

        private void Transitions_PostAnythingRemoved(AnimStateTransition item)
        {
            if (item.Owner == this)
                item.Owner = null;
        }
        private void Transitions_PostAnythingAdded(AnimStateTransition item)
        {
            item.Owner = this;
        }

        public AnimStateTransition AddTransitionTo(
            AnimState nextState,
            AnimTransitionCondition[] conditions,
            float exitTime = 0.0f,
            bool fixedDuration = true,
            float transitionDuration = 0.0f,
            float transitionOffset = 0.0f,
            ETransitionInterruptionSource interruptionSource = ETransitionInterruptionSource.Neither,
            bool orderedInterruption = true,
            bool canTransitionToSelf = false)
        {
            ArgumentNullException.ThrowIfNull(nextState);
            AnimStateTransition transition = new()
            {
                DestinationState = nextState,
                BlendDuration = transitionDuration,
                BlendType = EAnimBlendType.Linear,
                Priority = 0,
                Name = "Transition to " + nextState.Name,
                ExitTime = exitTime,
                FixedDuration = fixedDuration,
                TransitionOffset = transitionOffset,
                InterruptionSource = interruptionSource,
                OrderedInterruption = orderedInterruption,
                CanTransitionToSelf = canTransitionToSelf,
                Conditions = [.. conditions],
            };
            Transitions.Add(transition);
            return transition;
        }
    }
}
