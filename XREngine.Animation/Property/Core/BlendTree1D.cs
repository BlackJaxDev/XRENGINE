using XREngine.Data.Core;

namespace XREngine.Animation
{
    public class BlendTree1D : BlendTree
    {
        private string _parameterName = string.Empty;
        public string ParameterName
        {
            get => _parameterName;
            set => SetField(ref _parameterName, value);
        }

        public class Child : XRBase
        {
            private MotionBase? _motion;
            public MotionBase? Motion
            {
                get => _motion;
                set => SetField(ref _motion, value);
            }

            private float _speed = 1.0f;
            public float Speed
            {
                get => _speed;
                set => SetField(ref _speed, value);
            }

            private float _threshold = 0.0f;
            public float Threshold
            {
                get => _threshold;
                set => SetField(ref _threshold, value);
            }

            private bool _humanoidMirror = false;
            public bool HumanoidMirror
            {
                get => _humanoidMirror;
                set => SetField(ref _humanoidMirror, value);
            }

            public void Tick(object? rootObject, float delta, IDictionary<string, AnimVar> variables, float weight)
                => Motion?.Tick(rootObject, delta * Speed, variables, weight);
        }

        private EventList<Child> _children = [];
        public EventList<Child> Children
        {
            get => _children;
            set => SetField(ref _children, value);
        }

        private readonly Comparer<Child> _childComparer = Comparer<Child>.Create((a, b) => a.Threshold.CompareTo(b.Threshold));

        private bool _needsSort = true;
        public bool NeedsSort
        {
            get => _needsSort;
            private set => SetField(ref _needsSort, value);
        }

        protected override bool OnPropertyChanging<T>(string? propName, T field, T @new)
        {
            bool change = base.OnPropertyChanging(propName, field, @new);
            if (change)
            {
                switch (propName)
                {
                    case nameof(Children):
                        if (_children is null)
                            return change;
                        for (int i = 0; i < _children.Count; i++)
                            _children[i].PropertyChanged -= Child_PropertyChanged;
                        _children.PostAnythingAdded -= ChildAdded;
                        _children.PostAnythingRemoved -= ChildRemoved;
                        break;
                }
            }
            return change;
        }
        protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
        {
            base.OnPropertyChanged(propName, prev, field);
            switch (propName)
            {
                case nameof(Children):
                    NeedsSort = true;
                    if (_children is null)
                        return;
                    for (int i = 0; i < _children.Count; i++)
                        _children[i].PropertyChanged += Child_PropertyChanged;
                    _children.PostAnythingAdded += ChildAdded;
                    _children.PostAnythingRemoved += ChildRemoved;
                    break;
            }
        }

        private void ChildAdded(Child item)
        {
            item.PropertyChanged += Child_PropertyChanged;
            NeedsSort = true;
        }
        private void ChildRemoved(Child item)
        {
            item.PropertyChanged -= Child_PropertyChanged;
            NeedsSort = true;
        }

        private void Child_PropertyChanged(object? sender, IXRPropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(Child.Threshold))
                NeedsSort = true;
        }

        public override void Tick(object? rootObject, float delta, IDictionary<string, AnimVar> variables, float weight)
        {
            if (_children.Count == 0)
                return;

            if (_children.Count == 1)
            {
                _children[0].Tick(rootObject, delta, variables, weight);
                return;
            }

            if (_needsSort)
            {
                _needsSort = false;
                _children.Sort(_childComparer);
            }

            float parameterValue = variables.TryGetValue(ParameterName, out AnimVar? var) ? var.FloatValue : 0.0f;
            if (parameterValue < _children[0].Threshold)
            {
                _children[0].Tick(rootObject, delta, variables, weight);
                return;
            }
            if (parameterValue > _children[^1].Threshold)
            {
                _children[^1].Tick(rootObject, delta, variables, weight);
                return;
            }

            Child min, max;

            if (_children.Count == 2)
            {
                min = _children[0];
                max = _children[1];
            }
            else
            {
                //Binary search for the child with the closest threshold to the parameter value
                int l = 0;
                int r = Children.Count - 1;
                int m;
                while (l < r)
                {
                    m = (l + r) / 2;
                    if (Children[m].Threshold < parameterValue)
                        l = m + 1;
                    else
                        r = m;
                }

                min = Children[l];
                if (l == r) //Parameter value is equal to the threshold of a child
                {
                    min.Tick(rootObject, delta, variables, weight);
                    return;
                }
                else
                    max = Children[l + 1];
            }

            GetWeights(parameterValue, min, max, out float minWeight, out float maxWeight);
            min.Tick(rootObject, delta, variables, minWeight * weight);
            max.Tick(rootObject, delta, variables, maxWeight * weight);
        }

        private static void GetWeights(float parameterValue, Child min, Child max, out float minWeight, out float maxWeight)
        {
            float t = (parameterValue - min.Threshold) / (max.Threshold - min.Threshold);
            minWeight = 1.0f - t;
            maxWeight = t;
        }
    }
}
