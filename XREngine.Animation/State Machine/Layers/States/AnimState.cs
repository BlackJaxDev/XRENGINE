
namespace XREngine.Animation
{
    public class AnimState : AnimStateBase
    {
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
        public MotionBase? Animation
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
        public AnimState(MotionBase animation, string name)
        {
            Animation = animation;
            Name = name;
        }
        public AnimState(MotionBase animation)
            => Animation = animation;
        public AnimState(MotionBase animation, params AnimStateTransition[] transitions) : base(transitions)
            => Animation = animation;
        public AnimState(MotionBase animation, IEnumerable<AnimStateTransition> transitions) : base(transitions)
            => Animation = animation;
        public AnimState(MotionBase animation, EventList<AnimStateTransition> transitions) : base(transitions)
            => Animation = animation;

        public void Tick(object? rootObject, float delta, IDictionary<string, AnimVar> variables, float weight)
        {
            Animation?.Tick(rootObject, delta, variables, weight);
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
    }
}
