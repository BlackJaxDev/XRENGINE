using System.Diagnostics;

namespace XREngine.Animation
{
    public class AnimState : AnimStateBase
    {
        public override string ToString()
            => $"AnimState: {Name} / ({Motion?.ToString() ?? "null"})";

        private string _name = "";
        public string Name
        {
            get => _name;
            set => SetField(ref _name, value);
        }

        private List<AnimStateComponent> _components = [];
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

        private MotionBase? _animation;
        public MotionBase? Motion
        {
            get => _animation;
            set => SetField(ref _animation, value);
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

        public void EvaluateValues(IDictionary<string, AnimVar> variables)
        {
            Motion?.EvaluateMotion(variables);
        }
        public void Tick(float delta, IDictionary<string, AnimVar> variables)
        {
            Motion?.Tick(delta);
            foreach (var component in Components)
                component.StateTick(this, variables, delta);
        }

        public void OnEnter(IDictionary<string, AnimVar> variables)
        {
            foreach (var component in Components)
                component.StateEntered(this, variables);
        }
        
        public void OnExit(IDictionary<string, AnimVar> variables)
        {
            foreach (var component in Components)
                component.StateExited(this, variables);
        }

        public void Initialize(AnimLayer layer, AnimStateMachine owner, object? rootObject)
        {
            OwningLayer = layer;
            Motion?.Initialize(layer, owner, rootObject);
        }

        public void Deinitialize()
        {
            OwningLayer = null;
            Motion?.Deinitialize();
        }
    }
}
