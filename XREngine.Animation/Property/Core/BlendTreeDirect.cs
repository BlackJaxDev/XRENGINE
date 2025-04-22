using XREngine.Data.Core;

namespace XREngine.Animation
{
    public class BlendTreeDirect : BlendTree
    {
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

            private string _weightParameterName = string.Empty;
            /// <summary>
            /// The name of the parameter that controls the weight of this motion.
            /// </summary>
            public string WeightParameterName
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

            public void Tick(object? rootObject, float delta, IDictionary<string, AnimVar> variables, float weight)
            {
                var m = Motion;
                if (m is null)
                    return;

                if (variables.TryGetValue(WeightParameterName, out AnimVar? var))
                    weight *= var.FloatValue;
                
                m.Tick(rootObject, delta * Speed, variables, weight);
            }
        }

        private List<Child> _children = [];
        public List<Child> Children
        {
            get => _children;
            set => SetField(ref _children, value);
        }

        public override void Tick(object? rootObject, float delta, IDictionary<string, AnimVar> variables, float weight)
        {
            foreach (Child child in Children)
                child.Tick(rootObject, delta, variables, weight);
        }
    }
}
