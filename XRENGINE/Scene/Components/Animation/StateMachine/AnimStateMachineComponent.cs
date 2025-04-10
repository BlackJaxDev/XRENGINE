using Extensions;
using System.Collections.Concurrent;
using System.Numerics;
using XREngine.Animation;
using XREngine.Scene.Components.Animation;

namespace XREngine.Components
{
    public class AnimStateMachineComponent : XRComponent
    {
        private bool _animatePhysics = false;
        public bool AnimatePhysics
        {
            get => _animatePhysics;
            set => SetField(ref _animatePhysics, value);
        }

        private int _initialStateIndex = -1;
        public int InitialStateIndex
        {
            get => _initialStateIndex;
            set => SetField(ref _initialStateIndex, value);
        }

        private HumanoidComponent? _skeleton;
        public HumanoidComponent? Skeleton
        {
            get => _skeleton;
            set => SetField(ref _skeleton, value);
        }

        private EventList<AnimState> _states;
        public EventList<AnimState> States
        {
            get => _states;
            set => SetField(ref _states, value, UnlinkStates, LinkStates);
        }

        private EventList<AnimTransition> _transitions = [];
        public EventList<AnimTransition> Transitions
        {
            get => _transitions;
            set => SetField(ref _transitions, value);
        }

        private ConcurrentDictionary<string, SkeletalAnimation> _animationTable = new();
        public ConcurrentDictionary<string, SkeletalAnimation> AnimationTable
        {
            get => _animationTable;
            set => SetField(ref _animationTable, value);
        }

        private BlendManager? _blendManager;
        internal Vector3 deltaPosition;

        public AnimState? InitialState
        {
            get => States.IndexInRange(InitialStateIndex) ? States[InitialStateIndex] : null;
            set
            {
                if (value == null)
                {
                    if (States.IndexInRange(InitialStateIndex))
                        States.RemoveAt(InitialStateIndex);
                    InitialStateIndex = -1;
                    return;
                }

                bool wasNull = !States.IndexInRange(InitialStateIndex);

                int newIndex = States.IndexOf(value);
                if (newIndex >= 0)
                    InitialStateIndex = newIndex;
                else if (value != null)
                {
                    InitialStateIndex = States.Count;
                    States.Add(value);
                }
                else
                    InitialStateIndex = -1;

                if (!wasNull || !IsActiveInHierarchy)
                    return;
                
                var initialState = InitialState;
                if (initialState != null)
                {
                    _blendManager = new BlendManager(initialState);
                    RegisterTick(ETickGroup.PrePhysics, (int)ETickOrder.Animation, Tick);
                }
            }
        }

        public AnimStateMachineComponent()
        {
            InitialStateIndex = -1;
            _states = [];
            Skeleton = null;
        }
        public AnimStateMachineComponent(HumanoidComponent skeleton)
        {
            InitialStateIndex = -1;
            _states = [];
            Skeleton = skeleton;
        }

        protected internal override void OnComponentActivated()
        {
            base.OnComponentActivated();

            var initialState = InitialState;
            if (initialState != null)
            {
                _blendManager = new BlendManager(initialState);
                RegisterTick(ETickGroup.Normal, ETickOrder.Animation, Tick);
            }
        }
        protected internal override void OnComponentDeactivated()
        {
            base.OnComponentDeactivated();
            _blendManager = null;
        }

        protected internal void Tick()
        {
            var skeleton = Skeleton;
            if (skeleton is null)
                return;

            _blendManager?.Tick(Engine.Delta, States, skeleton);
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
            if (state?.Owner == this)
                state.Owner = null;
        }
        private void StateAdded(AnimState state)
        {
            if (state != null)
                state.Owner = this;
        }

        private EventDictionary<string, AnimVar> _variables = [];
        internal bool applyRootMotion;
        internal Vector3 pivotPosition;

        public EventDictionary<string, AnimVar> Variables
        {
            get => _variables;
            set => SetField(ref _variables, value);
        }

        public void SetInt(string index, int value)
        {
            if (Variables.TryGetValue(index, out AnimVar? var))
                var.SetInt(value);
        }

        public void SetFloat(string index, float value)
        {
            if (Variables.TryGetValue(index, out AnimVar? var))
                var.SetFloat(value);
        }

        public void SetBool(string index, bool value)
        {
            if (Variables.TryGetValue(index, out AnimVar? var))
                var.SetBool(value);
        }

        public AnimTransition? GetAnimatorTransitionInfo(int index)
            => Transitions.TryGet(index);
    }

    public abstract class AnimVar
    {
        public abstract void SetBool(bool value);
        public abstract void SetFloat(float value);
        public abstract void SetInt(int value);
    }
    public class AnimFloat : AnimVar
    {
        public float Value { get; set; }

        public override void SetBool(bool value) => Value = value ? 1.0f : 0.0f;
        public override void SetFloat(float value) => Value = value;
        public override void SetInt(int value) => Value = value;
    }
    public class AnimInt : AnimVar
    {
        public int Value { get; set; }

        public override void SetBool(bool value) => Value = value ? 1 : 0;
        public override void SetFloat(float value) => Value = (int)value;
        public override void SetInt(int value) => Value = value;
    }
    public class AnimBool : AnimVar
    {
        public bool Value { get; set; }

        public override void SetBool(bool value) => Value = value;
        public override void SetFloat(float value) => Value = value > 0.5f;
        public override void SetInt(int value) => Value = value != 0;
    }
}
