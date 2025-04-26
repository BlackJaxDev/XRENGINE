using XREngine.Data.Core;

namespace XREngine.Animation
{
    public class BlendTreeDirect : BlendTree
    {
        public override string ToString()
            => $"BlendTreeDirect: {Name}";

        public class Child : XRBase
        {
            private MotionBase? _motion;
            /// <summary>
            /// The motion to play when this child is active.
            /// </summary>
            public MotionBase? Motion
            {
                get => _motion;
                set => SetField(ref _motion, value);
            }

            private string? _weightParameterName = null;
            /// <summary>
            /// The name of the parameter that controls the weight of this motion.
            /// If null, the weight is 1.0f.
            /// </summary>
            public string? WeightParameterName
            {
                get => _weightParameterName;
                set => SetField(ref _weightParameterName, value);
            }

            private float _speed = 1.0f;
            /// <summary>
            /// The speed at which the motion plays back.
            /// </summary>
            public float Speed
            {
                get => _speed;
                set => SetField(ref _speed, value);
            }

            private bool _humanoidMirror = false;
            /// <summary>
            /// Whether or not to mirror the motion for humanoid characters.
            /// </summary>
            public bool HumanoidMirror
            {
                get => _humanoidMirror;
                set => SetField(ref _humanoidMirror, value);
            }
        }

        private List<Child> _children = [];
        public List<Child> Children
        {
            get => _children;
            set => SetField(ref _children, value);
        }

        public override void GetAnimationValues()
        {
            base.GetAnimationValues();
            foreach (var child in Children)
                child.Motion?.GetAnimationValues();
        }

        public override void Tick(float delta)
        {
            foreach (var child in Children)
                child.Motion?.Tick(delta * child.Speed);
        }

        public override void BlendAnimationValues(IDictionary<string, AnimVar> variables)
        {
            foreach (Child child in Children)
            {
                child.Motion?.EvaluateMotion(variables);
                CopyAnimatedValues(child.Motion?.AnimationValues);
            }
        }

        private void CopyAnimatedValues(Dictionary<string, object?>? animationValues)
        {
            if (animationValues is null)
                return;
            
            //TODO: lerp to defaults using weight here?
            foreach (var kvp in animationValues)
            {
                if (_animationValues.ContainsKey(kvp.Key))
                    _animationValues[kvp.Key] = kvp.Value;
                else
                    _animationValues.Add(kvp.Key, kvp.Value);
            }
        }
    }
}
