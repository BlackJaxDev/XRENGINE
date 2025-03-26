using Extensions;
using System.Collections.Concurrent;
using XREngine.Animation;
using XREngine.Scene.Components.Animation;

namespace XREngine.Components
{
    public class AnimStateMachineComponent : XRComponent
    {
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

        private ConcurrentDictionary<string, SkeletalAnimation> _animationTable = new();
        public ConcurrentDictionary<string, SkeletalAnimation> AnimationTable
        {
            get => _animationTable;
            set => SetField(ref _animationTable, value);
        }

        private BlendManager? _blendManager;
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
    }
}
