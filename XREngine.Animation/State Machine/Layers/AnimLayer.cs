using XREngine.Extensions;
using MemoryPack;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using XREngine.Data;
using XREngine.Data.Core;

namespace XREngine.Animation
{
    [MemoryPackable]
    public partial class AnimLayer : XRBase
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

        [MemoryPackIgnore]
        public AnyState AnyState { get; } = new AnyState();

        [MemoryPackIgnore]
        public AnimStateMachine? OwningStateMachine { get; internal set; } = null;

        [MemoryPackIgnore]
        protected internal readonly Dictionary<string, object?> _animatedValues = [];
        [MemoryPackIgnore]
        internal readonly object _animationValuesLock = new();

        /// <summary>
        /// Typed value store for this layer, sized to the owning state machine's slot layout.
        /// </summary>
        [MemoryPackIgnore]
        internal AnimationValueStore ValueStore { get; } = new();

        /// <summary>
        /// Shared slot layout from the owning state machine.
        /// </summary>
        [MemoryPackIgnore]
        internal AnimationSlotLayout? SlotLayout { get; set; }

        [MemoryPackIgnore]
        internal HumanoidMotionContributionBuffer HumanoidContributions { get; } = new();
        
        [MemoryPackIgnore]
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

        [MemoryPackIgnore]
        private AnimState? _currentState = null;
        /// <summary>
        /// The state that's currently executing.
        /// </summary>
        [MemoryPackIgnore]
        public AnimState? CurrentState
        {
            get => _currentState;
            set => SetField(ref _currentState, value);
        }

        [MemoryPackIgnore]
        private AnimState? _nextState = null;
        /// <summary>
        /// The state we're currently blending into, and also executing, if any.
        /// </summary>
        [MemoryPackIgnore]
        public AnimState? NextState
        {
            get => _nextState;
            set => SetField(ref _nextState, value);
        }

        [MemoryPackIgnore]
        public AnimStateTransition? CurrentTransition
            => _blendManager.CurrentTransition;

        [MemoryPackConstructor]
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

            CurrentState?.RestartMotionPlayback(owner.Variables);
            CurrentState?.OnEnter(owner.Variables);
            if (CurrentState is not null)
                owner.NotifyHumanoidMotionContinuityChanged(EAnimMotionContinuityChange.StateEntry);
        }

        public void Deinitialize()
        {
            IDictionary<string, AnimVar>? variables = OwningStateMachine?.Variables;
            if (variables is not null)
            {
                CurrentState?.OnExit(variables);
                if (NextState is not null && !ReferenceEquals(NextState, CurrentState))
                    NextState.OnExit(variables);
            }

            foreach (var state in States)
                state.StopMotionPlayback();

            _blendManager.ResetRuntimeState();
            CurrentState = null;
            NextState = null;
            OwningStateMachine = null;
            foreach (var state in States)
                state.Deinitialize();
        }

        internal void SeekActiveMotionPlayback(float timeSeconds, bool collectEvents = false)
        {
            IDictionary<string, AnimVar>? variables = OwningStateMachine?.Variables;
            if (variables is null)
                return;

            CurrentState?.SeekMotionPlayback(timeSeconds, variables, collectEvents);
            if (NextState is not null && !ReferenceEquals(NextState, CurrentState))
                NextState.SeekMotionPlayback(timeSeconds, variables, collectEvents);
            if (collectEvents)
            {
                OwningStateMachine?.DispatchImportedAnimationEvents(CurrentState);
                if (NextState is not null && !ReferenceEquals(NextState, CurrentState))
                    OwningStateMachine?.DispatchImportedAnimationEvents(NextState);
            }
            OwningStateMachine?.NotifyHumanoidMotionContinuityChanged(EAnimMotionContinuityChange.Seek);
        }

        internal void PrepareImportedHumanoidContributionCapacity(int capacity)
        {
            HumanoidContributions.EnsureCapacity(capacity);
            if (SlotLayout is not null)
                _blendManager.PrepareRuntimeEvaluation(SlotLayout, capacity);
        }

        public void EvaluationTick(object? rootObject, float delta, IDictionary<string, AnimVar> variables)
            => EvaluateFrame(variables, delta, null);

        public void EvaluationTick(object? rootObject, long deltaTicks, IDictionary<string, AnimVar> variables)
        {
            float delta = deltaTicks == 0L
                ? 0.0f
                : (float)(deltaTicks / (double)Stopwatch.Frequency);
            EvaluateFrame(variables, delta, deltaTicks);
        }

        private void EvaluateFrame(
            IDictionary<string, AnimVar> variables,
            float delta,
            long? deltaTicks)
        {
            ValueStore.Clear();
            HumanoidContributions.Clear();
            AnimState? currState = CurrentState;
            if (currState is null)
            {
                InitialState ??= States.Count > 0 ? States[0] : null;
                CurrentState = currState = InitialState;
                if (currState is null)
                    return;

                currState.RestartMotionPlayback(variables);
                currState.OnEnter(variables);
                OwningStateMachine?.NotifyHumanoidMotionContinuityChanged(EAnimMotionContinuityChange.StateEntry);
            }

            currState.EvaluateValues(variables);
            if (NextState is AnimState nextToEvaluate
                && !ReferenceEquals(nextToEvaluate, currState))
                nextToEvaluate.EvaluateValues(variables);

            if (_blendManager.IsBlending)
            {
                if (_blendManager.TickBlend(this, delta, variables))
                    CompleteTransition(variables);
                else
                    TryInterruptTransition(variables);
            }
            else if (TryTransition(variables, AnyState, out _)
                || TryTransition(variables, currState, out _))
            {
                if (_blendManager.TickBlend(this, delta, variables))
                    CompleteTransition(variables);
            }
            else
            {
                NextState = null;
                CopyAnimationValuesFromState(currState);
            }

            TickActiveStates(variables, delta, deltaTicks);
        }

        private void TickActiveStates(
            IDictionary<string, AnimVar> variables,
            float delta,
            long? deltaTicks)
        {
            AnimState? current = CurrentState;
            AnimState? next = NextState;
            if (deltaTicks is long ticks)
            {
                current?.Tick(ticks, variables);
                OwningStateMachine?.DispatchImportedAnimationEvents(current);
                if (next is not null && !ReferenceEquals(next, current))
                {
                    next.Tick(ticks, variables);
                    OwningStateMachine?.DispatchImportedAnimationEvents(next);
                }
                return;
            }

            current?.Tick(delta, variables);
            OwningStateMachine?.DispatchImportedAnimationEvents(current);
            if (next is not null && !ReferenceEquals(next, current))
            {
                next.Tick(delta, variables);
                OwningStateMachine?.DispatchImportedAnimationEvents(next);
            }
        }

        private void CompleteTransition(IDictionary<string, AnimVar> variables)
        {
            AnimState? completedState = NextState;
            AnimState? previousState = CurrentState;
            CurrentState = completedState;
            if (previousState is not null && !ReferenceEquals(previousState, completedState))
            {
                previousState.OnExit(variables);
                previousState.StopMotionPlayback();
            }
            NextState = null;
            OwningStateMachine?.NotifyHumanoidMotionContinuityChanged(
                EAnimMotionContinuityChange.TransitionCompleted);
        }

        private void CopyAnimationValuesFromState(AnimState? state)
        {
            if (state?.Motion is not MotionBase motion)
                return;

            // Typed store path: bulk copy (zero-alloc)
            if (SlotLayout is not null && motion.SlotLayout is not null)
            {
                ValueStore.CopyFrom(state.RuntimeValueStore);
                HumanoidContributions.CopyFrom(state.HumanoidContributions);
                return;
            }

            // Legacy fallback: snapshot + copy
#pragma warning disable CS0618 // Obsolete GetAnimationValuesSnapshot - legacy path only
            CopyAnimationValuesLegacy(motion.GetAnimationValuesSnapshot());
#pragma warning restore CS0618
        }

        private void CopyAnimationValuesLegacy(IEnumerable<KeyValuePair<string, object?>>? values)
        {
            if (values is null)
                return;

            foreach (var kvp in values)
                SetAnimValue(kvp.Key, kvp.Value);
        }

        internal void SetAnimValue(string path, object? animValue)
        {
            lock (_animationValuesLock)
            {
                if (!_animatedValues.TryAdd(path, animValue))
                    _animatedValues[path] = animValue;
            }
        }

        /// <summary>
        /// Writes a value directly into the typed store at the given slot. No boxing for typed paths.
        /// </summary>
        internal void SetAnimValueTyped(in AnimSlot slot, object? animValue)
        {
            if (SlotLayout is not null && slot.IsValid)
                ValueStore.SetValue(slot, animValue);
            else if (animValue is not null)
                SetAnimValue(slot.TypeIndex.ToString(), animValue); // Fallback should not normally happen
        }

        private bool TryTransition(IDictionary<string, AnimVar> variables, AnimStateBase testState, out AnimState? nextState)
        {
            AnimStateTransition? transition = testState.TryTransition(
                variables,
                CurrentState,
                orderedBefore: null);
            nextState = transition?.DestinationState;

            if (nextState != null && (nextState != CurrentState || transition!.CanTransitionToSelf))
            {
                Debug.WriteLine($"Transitioning from {CurrentState} to {nextState} with transition {transition}");
                bool replayingCurrentState = ReferenceEquals(nextState, CurrentState);
                if (replayingCurrentState)
                {
                    CopyAnimationValuesFromState(CurrentState);
                    CurrentState?.OnExit(variables);
                }
                nextState.RestartMotionPlayback(variables, transition!.TransitionOffset);
                nextState.EvaluateValues(variables);
                nextState.OnEnter(variables);
                NextState = nextState;
                if (replayingCurrentState)
                {
                    _blendManager.BeginBlendFromSnapshot(
                        transition!,
                        CurrentState,
                        nextState,
                        ValueStore,
                        HumanoidContributions);
                    OwningStateMachine?.NotifyHumanoidMotionContinuityChanged(
                        EAnimMotionContinuityChange.Replay);
                }
                else
                {
                    _blendManager.BeginBlend(transition!, CurrentState, nextState);
                    OwningStateMachine?.NotifyHumanoidMotionContinuityChanged(
                        EAnimMotionContinuityChange.TransitionStarted);
                }
                return true;
            }

            nextState = null;
            return false;
        }

        private bool TryInterruptTransition(IDictionary<string, AnimVar> variables)
        {
            AnimStateTransition? activeTransition = _blendManager.CurrentTransition;
            if (activeTransition is null
                || activeTransition.InterruptionSource is ETransitionInterruptionSource.Neither)
                return false;

            if (TryInterruptFrom(AnyState, CurrentState, activeTransition, variables))
                return true;

            bool interrupted = activeTransition.InterruptionSource switch
            {
                ETransitionInterruptionSource.Current =>
                    TryInterruptFrom(CurrentState, CurrentState, activeTransition, variables),
                ETransitionInterruptionSource.Next =>
                    TryInterruptFrom(NextState, NextState, activeTransition, variables),
                ETransitionInterruptionSource.CurrentThenNext =>
                    TryInterruptFrom(CurrentState, CurrentState, activeTransition, variables)
                    || TryInterruptFrom(NextState, NextState, activeTransition, variables),
                ETransitionInterruptionSource.NextThenCurrent =>
                    TryInterruptFrom(NextState, NextState, activeTransition, variables)
                    || TryInterruptFrom(CurrentState, CurrentState, activeTransition, variables),
                _ => false,
            };
            if (interrupted)
                return true;
            return false;
        }

        private bool TryInterruptFrom(
            AnimStateBase? transitionSource,
            AnimState? semanticSourceState,
            AnimStateTransition activeTransition,
            IDictionary<string, AnimVar> variables)
        {
            if (transitionSource is null)
                return false;

            AnimStateTransition? orderedBefore = activeTransition.OrderedInterruption
                && ReferenceEquals(activeTransition.Owner, transitionSource)
                    ? activeTransition
                    : null;
            AnimStateTransition? interruption = transitionSource.TryTransition(
                variables,
                semanticSourceState,
                orderedBefore,
                activeTransition);
            AnimState? destination = interruption?.DestinationState;
            if (interruption is null
                || destination is null
                || (ReferenceEquals(destination, semanticSourceState)
                    && !interruption.CanTransitionToSelf))
                return false;

            Debug.WriteLine(
                $"Interrupting {activeTransition} from {semanticSourceState} with {interruption}");
            AnimState? previousCurrent = CurrentState;
            AnimState? previousNext = NextState;
            bool replayingSource = ReferenceEquals(destination, semanticSourceState);
            if (replayingSource)
                destination.OnExit(variables);
            destination.RestartMotionPlayback(variables, interruption.TransitionOffset);
            destination.EvaluateValues(variables);
            destination.OnEnter(variables);
            CurrentState = semanticSourceState ?? previousCurrent;
            NextState = destination;
            _blendManager.BeginBlendFromSnapshot(
                interruption,
                CurrentState,
                destination,
                ValueStore,
                HumanoidContributions);

            StopAbandonedInterruptedState(previousCurrent, CurrentState, destination, variables);
            StopAbandonedInterruptedState(previousNext, CurrentState, destination, variables);
            OwningStateMachine?.NotifyHumanoidMotionContinuityChanged(
                EAnimMotionContinuityChange.TransitionInterrupted);
            return true;
        }

        private static void StopAbandonedInterruptedState(
            AnimState? candidate,
            AnimState? semanticSource,
            AnimState destination,
            IDictionary<string, AnimVar> variables)
        {
            if (candidate is not null
                && !ReferenceEquals(candidate, semanticSource)
                && !ReferenceEquals(candidate, destination))
            {
                candidate.OnExit(variables);
                candidate.StopMotionPlayback();
            }
        }
    }
}
