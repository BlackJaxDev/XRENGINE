using System.ComponentModel;
using XREngine.Animation;
using XREngine.Data.Core;

namespace XREngine.Components
{
    public class AnimState : XRBase
    {
        private EventList<AnimStateTransition> _transitions = [];
        private PoseGenBase? _animation;
        private float _startSecond = 0.0f;
        private float _endSecond = 0.0f;

        [Browsable(false)]
        public AnimStateMachineComponent? Owner { get; internal set; }

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
                    _transitions.PostAnythingAdded -= _transitions_PostAnythingAdded;
                    _transitions.PostAnythingRemoved -= _transitions_PostAnythingRemoved;
                }
                _transitions = value ?? [];
                _transitions.PostAnythingAdded += _transitions_PostAnythingAdded;
                _transitions.PostAnythingRemoved += _transitions_PostAnythingRemoved;
                foreach (AnimStateTransition transition in _transitions)
                    _transitions_PostAnythingAdded(transition);
            }
        }

        /// <summary>
        /// The pose retrieval animation to use to retrieve a result.
        /// </summary>
        public PoseGenBase? Animation
        {
            get => _animation;
            set => SetField(ref _animation, value);
        }
        public float StartSecond
        {
            get => _startSecond;
            set => SetField(ref _startSecond, value);
        }
        public float EndSecond
        {
            get => _endSecond;
            set => SetField(ref _endSecond, value);
        }

        public AnimState() { }
        public AnimState(PoseGenBase animation)
        {
            Animation = animation;
        }
        public AnimState(PoseGenBase animation, params AnimStateTransition[] transitions)
        {
            Animation = animation;
            Transitions = [.. transitions];
        }
        public AnimState(PoseGenBase animation, List<AnimStateTransition> transitions)
        {
            Animation = animation;
            Transitions = [.. transitions];
        }
        public AnimState(PoseGenBase animation, EventList<AnimStateTransition> transitions)
        {
            Animation = animation;
            Transitions = [.. transitions];
        }

        /// <summary>
        /// Attempts to find any transitions that evaluate to true and returns the one with the highest priority.
        /// </summary>
        public AnimStateTransition TryTransition()
        {
            AnimStateTransition[] transitions =
                [.. Transitions.
                FindAll(x => x.Condition()).
                OrderBy(x => x.Priority)];

            return transitions.Length > 0 ? transitions[0] : null;
        }
        public void Tick(float delta)
        {
            Animation?.Tick(delta);
        }
        public HumanoidPose GetFrame()
        {
            return Animation?.GetPose();
        }
        public void OnStarted()
        {

        }
        public void OnEnded()
        {

        }
        private void _transitions_PostAnythingRemoved(AnimStateTransition item)
        {
            if (item.Owner == this)
                item.Owner = null;
        }
        private void _transitions_PostAnythingAdded(AnimStateTransition item)
        {
            item.Owner = this;
        }
    }
}
