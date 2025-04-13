using Extensions;
using XREngine.Data.Core;
using XREngine.Scene.Components.Animation;

namespace XREngine.Components
{
    public class AnimLayer : XRBase
    {
        public enum EApplyType
        {
            /// <summary>
            /// Values are added together.
            /// </summary>
            Additive,
            /// <summary>
            /// Values are set regardless of the current value.
            /// </summary>
            Override,
        }

        public AnimStateMachineComponent? OwningStateMachine { get; internal set; } = null;

        private readonly BlendManager _blendManager = new();

        private EApplyType _applyType = EApplyType.Override;
        public EApplyType ApplyType
        {
            get => _applyType;
            set => SetField(ref _applyType, value);
        }

        private EventList<AnimState> _states = [];
        public EventList<AnimState> States
        {
            get => _states;
            set => SetField(ref _states, value, UnlinkStates, LinkStates);
        }

        public AnimLayer() { }
        public AnimLayer(params AnimState[] states)
            => States = [.. states];
        public AnimLayer(List<AnimState> states)
            => States = [.. states];
        public AnimLayer(EventList<AnimState> states)
            => States = [.. states];

        private int _initialStateIndex = -1;
        public int InitialStateIndex
        {
            get => _initialStateIndex;
            set => SetField(ref _initialStateIndex, value);
        }

        public AnimState? InitialState
        {
            get => States.IndexInRange(InitialStateIndex) ? States[InitialStateIndex] : null;
            set
            {
                if (value is null)
                {
                    //Clear state index but don't remove from state list
                    InitialStateIndex = -1;
                }
                else
                {
                    int newIndex = States.IndexOf(value);
                    if (newIndex >= 0)
                        InitialStateIndex = newIndex; //Set to existing index
                    else
                    {
                        //Add it to the states list
                        InitialStateIndex = States.Count;
                        States.Add(value);
                    }
                }
            }
        }

        private void LinkStates(EventList<AnimState> states)
        {
            foreach (AnimState state in states)
                StateAdded(state);

            states.PostAnythingAdded += StateAdded;
            states.PostAnythingRemoved += StateRemoved;
        }
        private void UnlinkStates(EventList<AnimState> states)
        {
            states.PostAnythingAdded -= StateAdded;
            states.PostAnythingRemoved -= StateRemoved;

            foreach (AnimState state in states)
                StateRemoved(state);
        }
        private void StateRemoved(AnimState state)
        {
            if (state?.OwningLayer == this)
                state.OwningLayer = null;
        }
        private void StateAdded(AnimState state)
        {
            if (state != null)
                state.OwningLayer = this;
        }

        public void Initialize(AnimStateMachineComponent owner)
        {
            OwningStateMachine = owner;
            CurrentState = InitialState;
        }

        public void Deinitialize()
        {
            OwningStateMachine = null;
        }

        /// <summary>
        /// The state that's currently executing.
        /// </summary>
        public AnimState? CurrentState { get; set; } = null;
        /// <summary>
        /// The state we're currently blending into, and also executing, if any.
        /// </summary>
        public AnimState? NextState { get; set; } = null;

        public void Tick(float delta, HumanoidComponent? skeleton, IDictionary<string, AnimVar> variable)
        {
            //TODO: evaluate next state's transitions while still blending, stack multiple blends?
            //Right now we can only perform one transition at a time.

            if (NextState is null)
            {
                var transition = CurrentState?.TryTransition(variable);
                var nextState = transition?.DestinationState;
                if (nextState != null && nextState != CurrentState)
                {
                    //If the new state is different, queue it
                    NextState = nextState;
                    _blendManager.BeginBlend(transition!);
                }
            }

            CurrentState?.Tick(delta);
            NextState?.Tick(delta);

            if (CurrentState is not null && NextState is not null)
            {
                bool blendDone = _blendManager.TickBlend(CurrentState, NextState, delta);
                if (blendDone)
                {
                    CurrentState = NextState;
                    NextState = null;
                }
            }
        }

        public AnimStateTransition? CurrentTransition
            => _blendManager.CurrentTransition;
    }
}
