using Extensions;
using System.Diagnostics;
using System.Numerics;
using System.Reflection.Emit;
using XREngine.Data;
using XREngine.Data.Core;

namespace XREngine.Animation
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

        public AnyState AnyState { get; } = new AnyState();

        public AnimStateMachine? OwningStateMachine { get; internal set; } = null;

        protected internal readonly Dictionary<string, object?> _animatedValues = [];
        
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

        private float _weight = 1.0f;
        public float Weight
        {
            get => _weight;
            set => SetField(ref _weight, value);
        }

        private int _initialStateIndex = -1;
        public int InitialStateIndex
        {
            get => _initialStateIndex;
            set => SetField(ref _initialStateIndex, value);
        }

        private AnimState? _currentState = null;
        /// <summary>
        /// The state that's currently executing.
        /// </summary>
        public AnimState? CurrentState
        {
            get => _currentState;
            set => SetField(ref _currentState, value);
        }

        private AnimState? _nextState = null;
        /// <summary>
        /// The state we're currently blending into, and also executing, if any.
        /// </summary>
        public AnimState? NextState
        {
            get => _nextState;
            set => SetField(ref _nextState, value);
        }

        public AnimStateTransition? CurrentTransition
            => _blendManager.CurrentTransition;

        public AnimLayer() { }
        public AnimLayer(params AnimState[] states)
            => States = [.. states];
        public AnimLayer(List<AnimState> states)
            => States = [.. states];
        public AnimLayer(EventList<AnimState> states)
            => States = [.. states];

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

        public void Initialize(AnimStateMachine owner, object? rootObject)
        {
            OwningStateMachine = owner;
            CurrentState = InitialState;
            foreach (var state in States)
                state.Initialize(this, owner, rootObject);
        }

        public void Deinitialize()
        {
            OwningStateMachine = null;
            foreach (var state in States)
                state.Deinitialize();
        }

        public void EvaluationTick(object? rootObject, float delta, IDictionary<string, AnimVar> variables)
        {
            //TODO: evaluate next state's transitions while still blending, stack multiple blends?
            //Right now we can only perform one transition at a time.

            //Get the current state or set to the initial
            var currState = CurrentState;
            if (currState is null)
            {
                //No current state, set it to the initial state
                CurrentState = currState = InitialState;
                if (currState is null)
                    return; //No initial state, nothing to do
            }

            CurrentState?.EvaluateValues(variables);
            NextState?.EvaluateValues(variables);

            //Check if we're blending to a new state
            var nextState = NextState;
            if (nextState is not null || TryTransition(variables, currState, out nextState) || TryTransition(variables, AnyState, out nextState))
            {
                //The blend manager will update animation values
                if (_blendManager.TickBlend(this, delta))
                {
                    CurrentState = nextState;
                    CurrentState?.OnEnter(variables);
                    NextState = null;
                }
            }
            else
            {
                NextState = null;

                //Copy animation values from the current state
                CopyAnimationValuesFromMotion(_currentState?.Motion);
            }

            CurrentState?.Tick(delta, variables);
            NextState?.Tick(delta, variables);
        }

        private void CopyAnimationValuesFromMotion(MotionBase? motion)
            => CopyAnimationValues(motion?.AnimationValues);
        private void CopyAnimationValues(Dictionary<string, object?>? values)
        {
            if (values is null)
                return;

            foreach (string key in values.Keys)
                if (values.TryGetValue(key, out object? v1Value))
                    SetAnimValue(key, v1Value);
        }

        internal void SetAnimValue(string path, object? animValue)
        {
            if (!_animatedValues.TryAdd(path, animValue))
                _animatedValues[path] = animValue;
        }

        private bool TryTransition(IDictionary<string, AnimVar> variables, AnimStateBase testState, out AnimState? nextState)
        {
            //If we're not blending, check for a transition
            AnimStateTransition? transition = testState.TryTransition(variables);
            nextState = transition?.DestinationState;

            //If the new state is different, queue it
            if (nextState != null && (nextState != CurrentState || transition!.CanTransitionToSelf))
            {
                Debug.WriteLine($"Transitioning from {CurrentState} to {nextState} with transition {transition}");
                CurrentState?.OnExit(variables);
                NextState = nextState;
                _blendManager.BeginBlend(transition!, CurrentState, nextState);
                return true;
            }

            //No transition found
            nextState = null;
            return false;
        }
    }
}
